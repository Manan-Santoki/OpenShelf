using System.Text.Json;
using Microsoft.Win32;
using OpenShelf.Core;

namespace OpenShelf.Settings;

/// <summary>The registry's Run key, behind an interface so tests never touch the real one.</summary>
public interface IRunKeyRegistry
{
    string? GetValue(string name);
    void SetValue(string name, string value);
    void DeleteValue(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public sealed class RunKeyRegistry : IRunKeyRegistry
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// User settings in %APPDATA%\OpenShelf\preferences.json: written atomically (temp file, then replace),
/// about 300 ms after the last change, and read tolerantly: a missing or damaged file, or a single odd
/// value, falls back to the default for that setting only. Open at login lives in the registry instead.
/// </summary>
public sealed class PreferencesStore : IPreferences, IDisposable
{
    public const string RunValueName = "OpenShelf";
    public static readonly HotKey DefaultHotKey = new(Modifiers.Control | Modifiers.Alt, 0x20);

    private readonly string _path;
    private readonly IRunKeyRegistry _registry;
    private readonly string _exePath;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Dictionary<WheelKind, Modifiers> _triggers = Defaults();
    private readonly Dictionary<string, string> _json = [];
    private readonly Timer _timer;
    private bool _dirty;
    private bool _disposed;

    private HotKey? _hotKey = DefaultHotKey;
    private bool _shake = true, _removeAfterDragOut, _restoreShelves = true, _hideDocked = true, _resultsBack = true, _updates = true;

    public PreferencesStore() : this(DefaultPath) { }

    public PreferencesStore(string path, IRunKeyRegistry? registry = null, string? exePath = null, TimeSpan? debounce = null)
    {
        _path = path;
        _registry = registry ?? new RunKeyRegistry();
        _exePath = exePath ?? Environment.ProcessPath ?? "";
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenShelf", "preferences.json");

    public event Action? Changed;

    private static Dictionary<WheelKind, Modifiers> Defaults() => new()
    {
        [WheelKind.Convert] = Modifiers.Shift,
        [WheelKind.Tools] = Modifiers.Shift | Modifiers.Alt,
        [WheelKind.Actions] = Modifiers.Control,
    };

    // ------------------------------------------------------------ settings

    public IReadOnlyDictionary<WheelKind, Modifiers> Triggers
    {
        get { lock (_gate) return new Dictionary<WheelKind, Modifiers>(_triggers); }
    }

    /// <summary>Throws <see cref="ArgumentException"/> with a message fit to show the user when the combination is not allowed.</summary>
    public void SetTrigger(WheelKind wheel, Modifiers modifiers)
    {
        lock (_gate)
        {
            var error = TriggerError(wheel, modifiers, _triggers);
            if (error is not null) throw new ArgumentException(error, nameof(modifiers));
            if (_triggers[wheel] == modifiers) return;
            _triggers[wheel] = modifiers;
        }
        Touch();
    }

    /// <summary>Why <paramref name="modifiers"/> can't be <paramref name="wheel"/>'s trigger, or null when it can.</summary>
    public static string? TriggerError(WheelKind wheel, Modifiers modifiers, IReadOnlyDictionary<WheelKind, Modifiers> current)
    {
        if (modifiers.HasFlag(Modifiers.Win))
            return "The Windows key can't be used here. Choose from Ctrl, Alt and Shift.";
        if ((modifiers & (Modifiers.Control | Modifiers.Alt | Modifiers.Shift)) == 0)
            return "Choose at least one of Ctrl, Alt or Shift.";
        foreach (var (other, existing) in current)
            if (other != wheel && existing == modifiers)
                return $"{Describe(modifiers)} already opens the {WheelName(other)} wheel. Choose a different combination.";
        return null;
    }

    public static string WheelName(WheelKind kind) => kind switch
    {
        WheelKind.Convert => "Convert", WheelKind.Tools => "Tools", _ => "Actions",
    };

    /// <summary>"Ctrl + Shift + Alt".</summary>
    public static string Describe(Modifiers m) => string.Join(" + ", KeyNames.ModifierNames(m));

    public HotKey? SelectionHotKey
    {
        get { lock (_gate) return _hotKey; }
        set { if (Set(ref _hotKey, value)) Touch(); }
    }

    public bool ShakeToOpen { get => _shake; set { if (Set(ref _shake, value)) Touch(); } }
    public bool RemoveAfterDragOut { get => _removeAfterDragOut; set { if (Set(ref _removeAfterDragOut, value)) Touch(); } }
    public bool RestoreShelves { get => _restoreShelves; set { if (Set(ref _restoreShelves, value)) Touch(); } }
    public bool HideDockedShelves { get => _hideDocked; set { if (Set(ref _hideDocked, value)) Touch(); } }
    public bool ResultsBackOnShelf { get => _resultsBack; set { if (Set(ref _resultsBack, value)) Touch(); } }
    public bool AutomaticUpdates { get => _updates; set { if (Set(ref _updates, value)) Touch(); } }

    /// <summary>Read from and written to the Run key, which is the source of truth.</summary>
    public bool OpenAtLogin
    {
        get
        {
            try { return !string.IsNullOrEmpty(_registry.GetValue(RunValueName)); }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
        set
        {
            if (OpenAtLogin == value) return;
            try
            {
                if (value) _registry.SetValue(RunValueName, "\"" + _exePath + "\"");
                else _registry.DeleteValue(RunValueName);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return; }
            Changed?.Invoke();
        }
    }

    public string? GetJson(string key)
    {
        lock (_gate) return _json.GetValueOrDefault(key);
    }

    public void SetJson(string key, string? json)
    {
        lock (_gate)
        {
            if (json is null) { if (!_json.Remove(key)) return; }
            else
            {
                if (_json.TryGetValue(key, out var old) && old == json) return;
                _json[key] = json;
            }
        }
        Touch();
    }

    private bool Set<T>(ref T field, T value)
    {
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            return true;
        }
    }

    private void Touch()
    {
        lock (_gate)
        {
            _dirty = true;
            if (!_disposed) _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ files

    /// <summary>Writes any pending change now. Call on quit.</summary>
    public void Flush()
    {
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            text = Serialize();
        }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _dirty = true;   // try again on the next change
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
        Flush();
    }

    private string Serialize()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartObject("triggers");
            foreach (var (wheel, m) in _triggers) w.WriteNumber(wheel.ToString(), (int)m);
            w.WriteEndObject();
            if (_hotKey is { } hk)
            {
                w.WriteStartObject("selectionHotKey");
                w.WriteNumber("modifiers", (int)hk.Modifiers);
                w.WriteNumber("virtualKey", hk.VirtualKey);
                w.WriteEndObject();
            }
            else w.WriteNull("selectionHotKey");
            w.WriteBoolean("shakeToOpen", _shake);
            w.WriteBoolean("removeAfterDragOut", _removeAfterDragOut);
            w.WriteBoolean("restoreShelves", _restoreShelves);
            w.WriteBoolean("hideDockedShelves", _hideDocked);
            w.WriteBoolean("resultsBackOnShelf", _resultsBack);
            w.WriteBoolean("automaticUpdates", _updates);
            w.WriteStartObject("values");
            foreach (var (k, v) in _json) w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private void Load()
    {
        string text;
        try { text = File.ReadAllText(_path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("triggers", out var triggers) && triggers.ValueKind == JsonValueKind.Object)
            {
                var loaded = new Dictionary<WheelKind, Modifiers>(_triggers);
                foreach (var wheel in Enum.GetValues<WheelKind>())
                    if (triggers.TryGetProperty(wheel.ToString(), out var v) && TryInt(v, out var bits)
                        && (bits & ~15) == 0 && TriggerError(wheel, (Modifiers)bits, loaded) is null)
                        loaded[wheel] = (Modifiers)bits;
                // A hand-edited file could give two wheels the same keys; keep defaults then.
                if (loaded.Values.Distinct().Count() == loaded.Count) foreach (var (k, v) in loaded) _triggers[k] = v;
            }

            if (root.TryGetProperty("selectionHotKey", out var hk))
            {
                if (hk.ValueKind == JsonValueKind.Null) _hotKey = null;
                else if (hk.ValueKind == JsonValueKind.Object
                    && hk.TryGetProperty("modifiers", out var mods) && TryInt(mods, out var m) && (m & ~7) == 0 && m != 0
                    && hk.TryGetProperty("virtualKey", out var vk) && TryInt(vk, out var code) && code is > 0 and < 256)
                    _hotKey = new HotKey((Modifiers)m, code);
            }

            ReadBool(root, "shakeToOpen", ref _shake);
            ReadBool(root, "removeAfterDragOut", ref _removeAfterDragOut);
            ReadBool(root, "restoreShelves", ref _restoreShelves);
            ReadBool(root, "hideDockedShelves", ref _hideDocked);
            ReadBool(root, "resultsBackOnShelf", ref _resultsBack);
            ReadBool(root, "automaticUpdates", ref _updates);

            if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var p in values.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) _json[p.Name] = p.Value.GetString()!;
        }
        catch (JsonException) { /* keep whatever was read before the damage; the rest stays default */ }
    }

    private static bool TryInt(JsonElement e, out int value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }

    private static void ReadBool(JsonElement root, string name, ref bool field)
    {
        if (root.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False) field = e.GetBoolean();
    }
}

/// <summary>Output settings through the shared preferences store (key "output").</summary>
public static class PreferencesExtensions
{
    public const string OutputKey = "output";

    public static OutputSettings GetOutput(this IPreferences preferences) => OutputSettings.Decode(preferences.GetJson(OutputKey));

    public static void SetOutput(this IPreferences preferences, OutputSettings settings) =>
        preferences.SetJson(OutputKey, settings == OutputSettings.Automatic ? null : settings.Stored);
}

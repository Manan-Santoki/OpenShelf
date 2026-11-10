using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Settings;

/// <summary>One tab's content. Controls are built once; <see cref="Sync"/> re-reads the preferences into them.</summary>
internal abstract class Pane
{
    private readonly List<Action> _syncs = [];
    private bool _syncing;

    protected Pane(IPreferences preferences, AppCommands commands)
    {
        Preferences = preferences;
        Commands = commands;
    }

    protected IPreferences Preferences { get; }
    protected AppCommands Commands { get; }

    public StackPanel Content { get; } = new();

    protected bool Syncing => _syncing;

    /// <summary>Re-reads every value; called when the pane is shown and when the window is activated.</summary>
    public void Sync()
    {
        _syncing = true;
        try { foreach (var sync in _syncs) sync(); }
        finally { _syncing = false; }
    }

    protected void OnSync(Action sync) { _syncs.Add(sync); _syncing = true; try { sync(); } finally { _syncing = false; } }

    /// <summary>Stacks sections with even spacing.</summary>
    protected void Stack(params UIElement[] sections)
    {
        for (var i = 0; i < sections.Length; i++)
        {
            if (i > 0 && sections[i] is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, 28, fe.Margin.Right, fe.Margin.Bottom);
            Content.Children.Add(sections[i]);
        }
    }

    protected SettingRow ToggleRow(string title, string? caption, Func<bool> get, Action<bool> set)
    {
        var toggle = Kit.Switch(title);
        var row = new SettingRow(title, caption, toggle) { Cursor = Cursors.Hand };
        toggle.Click += (_, _) => set(toggle.IsChecked == true);
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            toggle.IsChecked = toggle.IsChecked != true;
            set(toggle.IsChecked == true);
            e.Handled = true;
        };
        OnSync(() => toggle.IsChecked = get());
        return row;
    }

    protected Segmented<T> Segment<T>(IReadOnlyList<(string, T)> options, Func<T> get, Action<T> set, string name) where T : notnull
    {
        var control = new Segmented<T>(options, v => { if (!Syncing) set(v); }, name);
        OnSync(() => control.Selected = get());
        return control;
    }

    protected Dropdown<T> Drop<T>(IReadOnlyList<(string, T)> options, Func<T> get, Action<T> set, string name, double width = 180)
    {
        var control = new Dropdown<T>(options, v => { if (!Syncing) set(v); }, name, width);
        OnSync(() => control.Selected = get());
        return control;
    }
}

// ------------------------------------------------------------------ General

internal sealed class GeneralPane : Pane
{
    public GeneralPane(IPreferences preferences, AppCommands commands, SettingsHooks hooks) : base(preferences, commands)
    {
        var login = ToggleRow("Open at login", "Starts OpenShelf quietly when you sign in to Windows.",
            () => preferences.OpenAtLogin, v => preferences.OpenAtLogin = v);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var startOver = Kit.Button("Start Over", (_, _) =>
        {
            preferences.SetJson("tour.progress", null);
            commands.TakeTour();
        });
        var take = Kit.Button("Take the Tour", (_, _) => commands.TakeTour(), "AccentBtn");
        take.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(startOver);
        buttons.Children.Add(take);

        var tour = new SettingRow("Guided tour", "Practice files for converting, the Tools wheel, PDFs, video, shelves and the keyboard shortcut. Pick up where you left off, or start over.", buttons);

        var tip = Kit.Footnote("The OpenShelf icon may be tucked into the notification area's overflow (the ^ next to the clock). If you can't find it, opening OpenShelf again from the Start menu shows Settings.");
        var explorer = ToggleRow("Show “Open with OpenShelf” in the Explorer right-click menu",
            "On Windows 11 it appears under “Show more options”.",
            () => hooks.ExplorerEntryEnabled?.Invoke() ?? false,
            v =>
            {
                try { hooks.SetExplorerEntry?.Invoke(v); }
                catch (Exception) { /* the toggle is re-read below, so it shows what really happened */ }
                Sync();
            });
        OnSync(() => explorer.Visibility = hooks.SetExplorerEntry is null ? Visibility.Collapsed : Visibility.Visible);

        var startup = Kit.Section("Startup", null, new Card(login, explorer));
        startup.Children.Add(tip);
        Stack(startup, Kit.Section("Learn OpenShelf", null, new Card(tour)));
    }
}

// ------------------------------------------------------------------ Wheels

internal sealed class WheelsPane : Pane
{
    private const string ContextMenuKey = "explorer.contextMenu";

    private static readonly (WheelKind Kind, string Title)[] Wheels =
    [
        (WheelKind.Convert, "Convert wheel"),
        (WheelKind.Tools, "Tools wheel"),
        (WheelKind.Actions, "Actions wheel"),
    ];

    public WheelsPane(IPreferences preferences, AppCommands commands) : base(preferences, commands)
    {
        var rows = new List<Border>();
        foreach (var (kind, title) in Wheels) rows.Add(TriggerRow(kind, title));
        // Changing one wheel's keys can change what the others should say.
        preferences.Changed += () => { foreach (var refresh in _notes) refresh(); };
        rows.Add(ShortcutRow());

        var triggers = Kit.Section("Trigger keys",
            "Tap while dragging a file. A tap is enough: the wheel stays open until you drop.", new Card([.. rows]));

        var shake = ToggleRow("Shake the pointer mid-drag to open the Actions wheel", null,
            () => preferences.ShakeToOpen, v => preferences.ShakeToOpen = v);
        var menu = ToggleRow("Show “Open OpenShelf wheel” in the Explorer right-click menu", null,
            () => preferences.GetJson(ContextMenuKey) != "false",
            v => preferences.SetJson(ContextMenuKey, v ? "true" : "false"));
        Stack(triggers, Kit.Section("Without the keyboard", null, new Card(shake, menu)));
    }

    private readonly List<Action> _notes = [];

    private SettingRow TriggerRow(WheelKind kind, string title)
    {
        var row = new SettingRow(title);
        string? error = null;
        void Note()
        {
            if (error is not null) { row.SetNote(error); return; }
            row.SetNote(PreferencesStore.TriggerNote(kind, Preferences.Triggers));
        }
        _notes.Add(Note);
        void Show()
        {
            Note();
            var change = Kit.Button("Change", (_, _) => Record(), "GhostBtn", $"Change the {title} trigger keys");
            var strip = Kit.Keycaps(KeyNames.ModifierNames(Preferences.Triggers[kind]));
            change.Margin = new Thickness(10, 0, 0, 0);
            row.Trailing = Row(strip, change);
        }
        void Record()
        {
            error = null;
            row.SetNote(null);
            var box = new RecorderBox(RecorderMode.Trigger, "Press and release the keys…");
            box.Chosen = chord =>
            {
                try { Preferences.SetTrigger(kind, chord); error = null; }
                catch (ArgumentException e) { error = e.Message; }
                Show();
            };
            box.Rejected = message => row.SetNote(message);
            box.Ended = Show;
            row.Trailing = box;
            box.Begin();
        }
        OnSync(Show);
        return row;
    }

    private SettingRow ShortcutRow()
    {
        const string title = "Open wheel for Explorer selection";
        var row = new SettingRow(title);
        void Show()
        {
            var hotKey = Preferences.SelectionHotKey;
            var change = Kit.Button(hotKey is null ? "Set" : "Change", (_, _) => Record(), "GhostBtn",
                hotKey is null ? "Set the shortcut" : "Change the shortcut");
            change.Margin = new Thickness(10, 0, 0, 0);
            var parts = new List<UIElement>();
            if (hotKey is { } hk) parts.Add(Kit.Keycaps(KeyNames.Keycaps(hk)));
            else parts.Add(Kit.Text("Off", 13, "Text3"));
            parts.Add(change);
            if (hotKey is not null)
            {
                var off = Kit.Button("Turn off", (_, _) => { row.SetNote(null); Preferences.SelectionHotKey = null; Show(); }, "GhostBtn", "Turn off the shortcut");
                parts.Add(off);
            }
            row.Trailing = Row([.. parts]);
        }
        void Record()
        {
            row.SetNote(null);
            var box = new RecorderBox(RecorderMode.Shortcut, "Press the new shortcut…");
            box.ChosenShortcut = hk => { Preferences.SelectionHotKey = hk; Show(); };
            box.Rejected = message => row.SetNote(message);
            box.Ended = Show;
            row.Trailing = box;
            box.Begin();
        }
        OnSync(Show);
        return row;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items) panel.Children.Add(item);
        return panel;
    }
}

internal enum RecorderMode { Trigger, Shortcut }

/// <summary>Takes the keyboard and records a modifier combination (press and release) or a shortcut (modifiers plus a key). Esc cancels.</summary>
internal sealed class RecorderBox : Border
{
    private readonly RecorderMode _mode;
    private readonly HashSet<Key> _down = [];
    private readonly ModifierRecorder _recorder = new();
    private readonly TextBlock _label;
    private readonly string _prompt;
    private bool _ended;

    public Action<Modifiers>? Chosen { get; set; }
    public Action<HotKey>? ChosenShortcut { get; set; }
    public Action<string>? Rejected { get; set; }
    public Action? Ended { get; set; }

    public RecorderBox(RecorderMode mode, string prompt)
    {
        _mode = mode;
        _prompt = prompt;
        Focusable = true;
        FocusVisualStyle = null;
        Background = Kit.Brush("Fill");
        BorderBrush = Kit.Brush("Accent");
        BorderThickness = new Thickness(1.5);
        CornerRadius = new CornerRadius(6);
        Height = 30;
        Padding = new Thickness(12, 0, 12, 0);
        _label = Kit.Text(prompt + "  Esc to cancel", 12, "Text2");
        _label.VerticalAlignment = VerticalAlignment.Center;
        Child = _label;
        Kit.Name(this, prompt);
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        LostKeyboardFocus += (_, _) => End();
    }

    public void Begin() => Dispatcher.BeginInvoke(() => Keyboard.Focus(this), System.Windows.Threading.DispatcherPriority.Input);

    private void End()
    {
        if (_ended) return;
        _ended = true;
        Ended?.Invoke();
    }

    private static Key Real(KeyEventArgs e) => e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };

    private static Modifiers? Modifier(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => Modifiers.Control,
        Key.LeftShift or Key.RightShift => Modifiers.Shift,
        Key.LeftAlt or Key.RightAlt => Modifiers.Alt,
        _ => null,
    };

    private Modifiers Held()
    {
        var m = Modifiers.None;
        foreach (var k in _down) m |= Modifier(k) ?? Modifiers.None;
        return m;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;   // keep Alt from opening the window menu and keys from reaching other controls
        var key = Real(e);
        if (key == Key.Escape) { End(); return; }
        if (key is Key.LWin or Key.RWin) { Rejected?.Invoke("The Windows key can't be used here. Choose from Ctrl, Alt and Shift."); return; }
        if (Modifier(key) is not null)
        {
            _down.Add(key);
            var held = Held();
            _recorder.Update(held);
            if (_mode == RecorderMode.Trigger) _label.Text = string.Join(" + ", KeyNames.ModifierNames(_recorder.Current));
            return;
        }
        if (_mode != RecorderMode.Shortcut) return;
        var mods = Held();
        if ((mods & (Modifiers.Control | Modifiers.Alt)) == 0)
        {
            Rejected?.Invoke("Include Ctrl or Alt so the shortcut doesn't get in the way of typing.");
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        _ended = true;       // done: skip the focus-loss path
        ChosenShortcut?.Invoke(new HotKey(mods, vk));
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = Real(e);
        if (Modifier(key) is null || _mode != RecorderMode.Trigger) { _down.Remove(key); return; }
        _down.Remove(key);
        if (_recorder.Update(Held()) is { } chord)
        {
            _ended = true;
            Chosen?.Invoke(chord);
        }
    }
}

// ------------------------------------------------------------------ Shelves

internal sealed class ShelvesPane : Pane
{
    private readonly Card _folderCard = new();
    private readonly StackPanel _folderSection;
    private readonly Button _screenshots;
    private IReadOnlyList<WatchedFolder> _folders = [];

    public ShelvesPane(IPreferences preferences, AppCommands commands) : base(preferences, commands)
    {
        var behavior = new Card(
            ToggleRow("Remove files from a shelf after dragging them out", null, () => preferences.RemoveAfterDragOut, v => preferences.RemoveAfterDragOut = v),
            ToggleRow("Restore shelves when OpenShelf opens", null, () => preferences.RestoreShelves, v => preferences.RestoreShelves = v),
            ToggleRow("Hide docked shelves to the screen edge", null, () => preferences.HideDockedShelves, v => preferences.HideDockedShelves = v),
            ToggleRow("Add conversion and tool results back to the shelf",
                "When a file on a shelf is converted or edited, the result joins that shelf too. Files dragged in from elsewhere aren't affected.",
                () => preferences.ResultsBackOnShelf, v => preferences.ResultsBackOnShelf = v));

        var add = Kit.Button("Add Folder…", (_, _) => AddFolders());
        _screenshots = Kit.Button("Watch Screenshots", (_, _) => Add(WatchedFolderList.ScreenshotsFolder));
        _screenshots.ToolTip = "Watches " + WatchedFolderList.ScreenshotsFolder;
        _screenshots.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(_screenshots);

        var list = new StackPanel();
        list.Children.Add(_folderCard);
        list.Children.Add(buttons);
        _folderSection = Kit.Section("Watched folders",
            "New files that appear in these folders are put on a shelf of their own. Files already there, hidden files and unfinished downloads are left alone; subfolders aren't watched.", list);

        Stack(Kit.Section("Shelf behavior", null, behavior), _folderSection);
        OnSync(Reload);
    }

    private void Reload()
    {
        _folders = WatchedFolderList.Decode(Preferences.GetJson(WatchedFolderList.Key));
        Render();
    }

    private void Save(IEnumerable<WatchedFolder> folders)
    {
        _folders = [.. folders];
        Preferences.SetJson(WatchedFolderList.Key, _folders.Count == 0 ? null : WatchedFolderList.Encode(_folders));
        Render();
    }

    private void Add(string path)
    {
        if (_folders.Any(f => WatchedFolderList.SamePath(f.Path, path))) return;
        Save([.. _folders, new WatchedFolder(path)]);
    }

    private void AddFolders()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose folders whose new files OpenShelf puts on a shelf",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(Content)) != true) return;
        foreach (var path in dialog.FolderNames) Add(path);
    }

    private void Render()
    {
        var rows = new List<Border>();
        foreach (var folder in _folders) rows.Add(FolderRow(folder));
        if (rows.Count == 0)
        {
            var empty = new SettingRow("No folders are being watched yet", "Add a folder, or watch where Windows saves screenshots.");
            rows.Add(empty);
        }
        _folderCard.SetRows(rows);
        _screenshots.IsEnabled = !_folders.Any(f => WatchedFolderList.SamePath(f.Path, WatchedFolderList.ScreenshotsFolder));
    }

    private Border FolderRow(WatchedFolder folder)
    {
        var name = Path.GetFileName(folder.Path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = folder.Path;
        var missing = !Directory.Exists(folder.Path);

        var icon = Kit.Icon("folder", 18);
        var action = new Dropdown<string?>(ActionOptions(folder.Action), id => Replace(folder, folder with { Action = id }), "New files in " + name, 170)
        { Selected = folder.Action, IsEnabled = folder.Enabled };
        var toggle = Kit.Switch("Watch " + name);
        toggle.IsChecked = folder.Enabled;
        toggle.Click += (_, _) => Replace(folder, folder with { Enabled = toggle.IsChecked == true });
        var remove = Kit.IconButton("x", "Stop watching " + name, (_, _) => Save(_folders.Where(f => f != folder)));
        remove.ToolTip = "Stop watching";

        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        action.Margin = new Thickness(0, 0, 14, 0);
        toggle.Margin = new Thickness(0, 0, 8, 0);
        trailing.Children.Add(action);
        trailing.Children.Add(toggle);
        trailing.Children.Add(remove);

        var row = new SettingRow(name, missing ? null : folder.Path, trailing, icon);
        if (row.Caption is { } caption) { caption.TextTrimming = TextTrimming.CharacterEllipsis; caption.TextWrapping = TextWrapping.NoWrap; caption.ToolTip = folder.Path; }
        if (missing) row.SetNote("Folder not found. Switch it off and on to try again.");
        return row;
    }

    private static IReadOnlyList<(string, string?)> ActionOptions(string? current)
    {
        var options = WatchedFolderList.Actions.Select(a => (a.Title, a.Id)).ToList();
        if (current is not null && options.All(o => o.Id != current)) options.Add((current, current));
        return options;
    }

    private void Replace(WatchedFolder old, WatchedFolder updated) =>
        Save(_folders.Select(f => f == old ? updated : f));
}

// ------------------------------------------------------------------ Output

internal sealed class OutputPane : Pane
{
    public OutputPane(IPreferences preferences, AppCommands commands) : base(preferences, commands)
    {
        OutputSettings Current() => preferences.GetOutput();
        void Edit(Func<OutputSettings, OutputSettings> change)
        {
            if (Syncing) return;
            preferences.SetOutput(change(Current()));
            Sync();
        }

        var codec = Segment([.. Enum.GetValues<VideoCodec>().Select(c => (VideoOutput.Title(c), c))],
            () => Current().Video.Codec, v => Edit(o => o with { Video = o.Video with { Codec = v } }), "Codec");
        var quality = Segment([.. Enum.GetValues<VideoQuality>().Select(q => (VideoOutput.Title(q), q))],
            () => Current().Video.Quality, v => Edit(o => o with { Video = o.Video with { Quality = v } }), "Quality");
        var bitrate = new MbpsSlider(mbps => Edit(o => o with { Video = o.Video with { CustomMbps = mbps } }));
        var size = Segment([.. Enum.GetValues<VideoMaxSize>().Select(s => (VideoOutput.Title(s), s))],
            () => Current().Video.MaxSize, v => Edit(o => o with { Video = o.Video with { MaxSize = v } }), "Largest size");

        var bitrateRow = new SettingRow("Bitrate", null, bitrate);
        var video = new Card(new SettingRow("Codec", null, codec), new SettingRow("Quality", null, quality), bitrateRow, new SettingRow("Largest size", null, size));
        OnSync(() =>
        {
            var v = Current().Video;
            bitrate.Mbps = v.CustomMbps;
            bitrateRow.Visibility = v.Quality == VideoQuality.Custom ? Visibility.Visible : Visibility.Collapsed;
        });

        var audio = Drop([("Automatic", (int?)null), .. AudioOutput.Choices.Select(k => ($"{k} kb/s", (int?)k))],
            () => Current().Audio.Kbps, v => Edit(o => o with { Audio = new AudioOutput { Kbps = v } }), "Audio bitrate");

        var imageQuality = Drop([("Automatic", (double?)null), .. ImageOutput.QualityChoices.Select(q => ($"{(int)Math.Round(q * 100)}%", (double?)q))],
            () => Current().Image.Quality, v => Edit(o => o with { Image = o.Image with { Quality = v } }), "Image quality");
        var metadata = ToggleRow("Keep metadata", "Copies camera, date and location from the original into the converted image.",
            () => Current().Image.KeepsMetadata, v => Edit(o => o with { Image = o.Image with { KeepsMetadata = v } }));

        var gifWidth = Drop([.. Enum.GetValues<GifWidth>().Select(w => (GifOutput.Title(w), w))],
            () => Current().Gif.Width, v => Edit(o => o with { Gif = o.Gif with { Width = v } }), "GIF width", 220);
        var gifRate = Segment([.. GifOutput.FrameRates.Select(r => ($"{r} fps", r))],
            () => Current().Gif.FrameRate, v => Edit(o => o with { Gif = o.Gif with { FrameRate = v } }), "GIF frame rate");

        var reset = Kit.Button("Reset to Automatic", (_, _) => { preferences.SetOutput(OutputSettings.Automatic); Sync(); });
        OnSync(() => reset.IsEnabled = Current() != OutputSettings.Automatic);
        var footer = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = Kit.Text("Hold Alt on a format to choose just once.", 12, "Text3");
        hint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(reset, 1);
        footer.Children.Add(hint);
        footer.Children.Add(reset);

        Stack(
            Kit.Section("Video", "Automatic converts as OpenShelf always has. The codec is for MP4, MOV and MKV; HDR videos stay HEVC to keep their colours.", video),
            Kit.Section("Audio", "Applies to MP3, M4A, OGG, Opus and WMA, and the sound in videos.", new Card(new SettingRow("Bitrate", null, audio))),
            Kit.Section("Images", "Quality applies to JPEG, HEIC, AVIF and WebP.", new Card(new SettingRow("Quality", null, imageQuality), metadata)),
            Kit.Section("GIFs from videos", null, new Card(new SettingRow("Width", null, gifWidth), new SettingRow("Frame rate", null, gifRate))),
            footer);
    }
}

// ------------------------------------------------------------------ Extra Formats

internal sealed class ToolsPane : Pane
{
    private sealed class Job
    {
        public CancellationTokenSource Cts { get; } = new();
        public bool Busy { get; set; } = true;
        public bool Removing { get; set; }
        public string? Error { get; set; }
        public ProgressBar? Bar { get; set; }
        public TextBlock? Percent { get; set; }
        public double Progress { get; set; }
    }

    private static readonly ToolRow[] Placeholders =
    [
        new("ffmpeg", "ffmpeg", "Video, audio and GIF formats: MKV, WebM, AVI, WMV, MP3, OGG, Opus and more.", false, null, false),
        new("libreoffice", "LibreOffice", "Office documents, slides and spreadsheets: DOCX, PPTX, XLSX, ODT and more.", false, null, false),
        new("pandoc", "Pandoc", "Markdown, Word, EPUB and OpenDocument text conversions.", false, null, false),
        new("calibre", "calibre", "E-books: EPUB, MOBI, AZW3 and more.", false, null, false),
    ];

    private readonly Card _card = new();
    private readonly Dictionary<string, Job> _jobs = [];
    private IToolInstallerUi? _installer;

    public ToolsPane(IPreferences preferences, AppCommands commands) : base(preferences, commands)
    {
        Stack(Kit.Section("Extra formats",
            "Free software, downloaded only when you click Install, and checked before it's installed. OpenShelf also uses copies you already have.", _card));
        Render();
    }

    public IToolInstallerUi? Installer
    {
        get => _installer;
        set
        {
            if (_installer is not null) _installer.Changed -= OnChanged;
            _installer = value;
            if (_installer is not null) _installer.Changed += OnChanged;
            Render();
        }
    }

    private void OnChanged() => Content.Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        var tools = _installer?.Tools ?? Placeholders;
        var rows = new List<Border>();
        foreach (var tool in tools) rows.Add(RowFor(tool));
        _card.SetRows(rows);
    }

    private Border RowFor(ToolRow tool)
    {
        if (_installer is null)
        {
            var coming = new SettingRow(tool.Name, tool.Description, Kit.Text("Coming soon", 12, "Text3")) { Opacity = 0.5 };
            return coming;
        }

        _jobs.TryGetValue(tool.Id, out var job);
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string? note = null, noteBrush = "Text3";

        if (job is { Busy: true, Removing: false })
        {
            // Verifying and unpacking report no progress of their own: the bar slides instead of sitting still at 85%.
            var phase = _installer.PhaseOf(tool.Id) ?? InstallPhase.Downloading;
            var waiting = phase is InstallPhase.Verifying or InstallPhase.Unpacking or InstallPhase.Finishing or InstallPhase.Winget;
            var bar = new ProgressBar { Width = 120, Minimum = 0, Maximum = 100, Value = job.Progress * 100, IsIndeterminate = waiting, VerticalAlignment = VerticalAlignment.Center };
            var pct = Kit.Text($"{Math.Round(job.Progress * 100)}%", 12, "Text2");
            pct.Width = 40;
            pct.TextAlignment = TextAlignment.Right;
            pct.VerticalAlignment = VerticalAlignment.Center;
            job.Bar = bar;
            job.Percent = pct;
            var cancel = Kit.Button("Cancel", (_, _) => job.Cts.Cancel(), "GhostBtn", $"Cancel installing {tool.Name}");
            cancel.Margin = new Thickness(6, 0, 0, 0);
            trailing.Children.Add(bar);
            if (!waiting) trailing.Children.Add(pct);
            trailing.Children.Add(cancel);
            note = phase switch
            {
                InstallPhase.Verifying => "Verifying…",
                InstallPhase.Unpacking => "Unpacking…",
                InstallPhase.Finishing => "Installing…",
                InstallPhase.Winget => "Installing with winget…",
                _ => "Downloading…",
            };
        }
        else if (job is { Busy: true })
        {
            note = "Removing…";
        }
        else
        {
            if (tool.Installed)
            {
                note = tool.Location is { Length: > 0 } ? $"Installed ({tool.Location})" : "Installed";
                noteBrush = "Success";
                if (tool.CanRemove)
                    trailing.Children.Add(Kit.Button("Remove", (_, _) => Remove(tool), "Btn", $"Remove {tool.Name}"));
            }
            else
            {
                note = "Not installed";
                if (job?.Error is not null && _installer.OffersWinget(tool.Id))
                {
                    var winget = Kit.Button("Try winget", (_, _) => Install(tool, viaWinget: true), "Btn", $"Install {tool.Name} with winget");
                    winget.Margin = new Thickness(0, 0, 6, 0);
                    trailing.Children.Add(winget);
                }
                trailing.Children.Add(Kit.Button(job?.Error is null ? "Install" : "Try Again", (_, _) => Install(tool), "AccentBtn", $"Install {tool.Name}"));
            }
        }

        if (job?.Error is { } error)
        {
            note = error;
            noteBrush = "Danger";
        }

        var row = new SettingRow(tool.Name, tool.Description, trailing.Children.Count == 0 ? null : trailing);
        row.SetNote(note, noteBrush);
        return row;
    }

    internal async void Install(ToolRow tool, bool viaWinget = false)
    {
        if (_installer is not { } installer) return;
        var job = new Job();
        _jobs[tool.Id] = job;
        Render();
        var progress = new Progress<double>(value =>
        {
            job.Progress = value;
            if (job.Bar is { } bar) bar.Value = value * 100;
            if (job.Percent is { } pct) pct.Text = $"{Math.Round(value * 100)}%";
        });
        try
        {
            if (viaWinget) await installer.InstallWithWingetAsync(tool.Id, progress, job.Cts.Token);
            else await installer.InstallAsync(tool.Id, progress, job.Cts.Token);
            _jobs.Remove(tool.Id);
        }
        catch (OperationCanceledException) { _jobs.Remove(tool.Id); }
        catch (Exception e)
        {
            job.Busy = false;
            job.Error = string.IsNullOrWhiteSpace(e.Message) ? "The download failed. Try again later." : e.Message;
        }
        Render();
    }

    private async void Remove(ToolRow tool)
    {
        if (_installer is not { } installer) return;
        var job = new Job { Removing = true };
        _jobs[tool.Id] = job;
        Render();
        try
        {
            await installer.RemoveAsync(tool.Id);
            _jobs.Remove(tool.Id);
        }
        catch (Exception e)
        {
            job.Busy = false;
            job.Error = string.IsNullOrWhiteSpace(e.Message) ? $"{tool.Name} couldn't be removed." : e.Message;
        }
        Render();
    }
}

// ------------------------------------------------------------------ Updates

internal sealed class UpdatesPane : Pane
{
    public UpdatesPane(IPreferences preferences, AppCommands commands) : base(preferences, commands)
    {
        var auto = ToggleRow("Check for updates automatically", "OpenShelf looks for a new version now and then and tells you when one is ready.",
            () => preferences.AutomaticUpdates, v => preferences.AutomaticUpdates = v);
        var check = Kit.Button("Check for Updates", (_, _) => commands.CheckForUpdates(), "Btn", "Check for Updates");
        var version = new SettingRow("OpenShelf " + VersionText(), "You're using this version.", check);
        Stack(Kit.Section("Updates", null, new Card(auto, version)));
    }

    internal static string VersionText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdatesPane).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}

using OpenShelf.Core;
using OpenShelf.Settings;

namespace OpenShelf.Settings.Tests;

internal sealed class FakeRegistry : IRunKeyRegistry
{
    public Dictionary<string, string> Values { get; } = [];
    public string? GetValue(string name) => Values.GetValueOrDefault(name);
    public void SetValue(string name, string value) => Values[name] = value;
    public void DeleteValue(string name) => Values.Remove(name);
}

public sealed class PreferencesStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openshelf-prefs-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "preferences.json");
    private readonly FakeRegistry _registry = new();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private PreferencesStore Store(string? exe = null) => new(FilePath, _registry, exe ?? @"C:\Apps\OpenShelf.exe", TimeSpan.FromHours(1));

    [Fact]
    public void Defaults()
    {
        var p = Store();
        Assert.Equal(Modifiers.Shift, p.Triggers[WheelKind.Convert]);
        Assert.Equal(Modifiers.Shift | Modifiers.Alt, p.Triggers[WheelKind.Tools]);
        Assert.Equal(Modifiers.Control, p.Triggers[WheelKind.Actions]);
        Assert.Equal(new HotKey(Modifiers.Control | Modifiers.Alt, 0x20), p.SelectionHotKey);
        Assert.True(p.ShakeToOpen); Assert.False(p.RemoveAfterDragOut); Assert.True(p.RestoreShelves);
        Assert.True(p.HideDockedShelves); Assert.True(p.ResultsBackOnShelf); Assert.False(p.OpenAtLogin); Assert.True(p.AutomaticUpdates);
    }

    [Fact]
    public void RoundTrips()
    {
        var p = Store();
        p.SetTrigger(WheelKind.Convert, Modifiers.Control | Modifiers.Shift);
        p.SelectionHotKey = new HotKey(Modifiers.Control, 0x41);
        p.ShakeToOpen = false; p.RemoveAfterDragOut = true; p.RestoreShelves = false; p.AutomaticUpdates = false;
        p.SetJson("k", "{\"a\":1}");
        p.Flush();
        var q = Store();
        Assert.Equal(Modifiers.Control | Modifiers.Shift, q.Triggers[WheelKind.Convert]);
        Assert.Equal(new HotKey(Modifiers.Control, 0x41), q.SelectionHotKey);
        Assert.False(q.ShakeToOpen); Assert.True(q.RemoveAfterDragOut); Assert.False(q.RestoreShelves); Assert.False(q.AutomaticUpdates);
        Assert.Equal("{\"a\":1}", q.GetJson("k"));
    }

    [Fact]
    public void HotKeyTurnedOffStaysOff()
    {
        var p = Store();
        p.SelectionHotKey = null;
        p.Flush();
        Assert.Null(Store().SelectionHotKey);
    }

    [Fact]
    public void WriteHappensOnFlushAndLeavesNoTempFile()
    {
        var p = Store();
        p.ShakeToOpen = false;
        Assert.False(File.Exists(FilePath));
        p.Flush();
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
        p.ShakeToOpen = true;   // second write replaces the first
        p.Flush();
        Assert.True(Store().ShakeToOpen);
    }

    [Fact]
    public void DebounceTimerWritesByItself()
    {
        var p = new PreferencesStore(FilePath, _registry, "x", TimeSpan.FromMilliseconds(50));
        p.ShakeToOpen = false;
        for (var i = 0; i < 60 && !File.Exists(FilePath); i++) Thread.Sleep(50);
        Assert.True(File.Exists(FilePath));
        p.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"triggers\":")]
    public void CorruptFileFallsBackToDefaults(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, text);
        var p = Store();
        Assert.Equal(Modifiers.Shift, p.Triggers[WheelKind.Convert]);
        Assert.True(p.ShakeToOpen);
        Assert.NotNull(p.SelectionHotKey);
    }

    [Fact]
    public void OddValuesFallBackSettingBySetting()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath,
            "{\"triggers\":{\"Convert\":\"x\",\"Tools\":6,\"Actions\":99},\"selectionHotKey\":{\"modifiers\":\"a\"},\"shakeToOpen\":false,\"restoreShelves\":7,\"values\":{\"k\":\"v\",\"n\":5}}");
        var p = Store();
        Assert.Equal(Modifiers.Shift, p.Triggers[WheelKind.Convert]);
        Assert.Equal(Modifiers.Control | Modifiers.Alt, p.Triggers[WheelKind.Tools]);
        Assert.Equal(Modifiers.Control, p.Triggers[WheelKind.Actions]);   // 99 is out of range
        Assert.NotNull(p.SelectionHotKey);
        Assert.False(p.ShakeToOpen);
        Assert.True(p.RestoreShelves);
        Assert.Equal("v", p.GetJson("k"));
        Assert.Null(p.GetJson("n"));
    }

    [Theory]
    [InlineData(Modifiers.None)]
    [InlineData(Modifiers.Win)]
    public void TriggerNeedsCtrlAltOrShift(Modifiers m)
    {
        var p = Store();
        var e = Assert.Throws<ArgumentException>(() => p.SetTrigger(WheelKind.Convert, m));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
        Assert.Equal(Modifiers.Shift, p.Triggers[WheelKind.Convert]);
    }

    [Fact]
    public void TwoWheelsMayNotShareACombination()
    {
        var p = Store();
        var e = Assert.Throws<ArgumentException>(() => p.SetTrigger(WheelKind.Convert, Modifiers.Control));
        Assert.Contains("Actions", e.Message);
        p.SetTrigger(WheelKind.Convert, Modifiers.Shift);   // unchanged value is fine
        p.SetTrigger(WheelKind.Convert, Modifiers.Control | Modifiers.Win | Modifiers.Alt);
    }

    [Fact]
    public void ChangedIsRaisedOnTheSettingThread()
    {
        var p = Store();
        var seen = new List<int>();
        p.Changed += () => { lock (seen) seen.Add(Environment.CurrentManagedThreadId); };
        p.ShakeToOpen = false;
        p.ShakeToOpen = false;   // no change, no event
        var t = new Thread(() => p.RestoreShelves = false);
        t.Start(); t.Join();
        Assert.Equal(2, seen.Count);
        Assert.Equal(Environment.CurrentManagedThreadId, seen[0]);
        Assert.Equal(t.ManagedThreadId, seen[1]);
    }

    [Fact]
    public void OpenAtLoginUsesTheRunKey()
    {
        var p = Store(@"C:\Program Files\OpenShelf\OpenShelf.exe");
        Assert.False(p.OpenAtLogin);
        p.OpenAtLogin = true;
        Assert.Equal("\"C:\\Program Files\\OpenShelf\\OpenShelf.exe\"", _registry.Values["OpenShelf"]);
        Assert.True(p.OpenAtLogin);
        p.OpenAtLogin = false;
        Assert.Empty(_registry.Values);

        _registry.Values["OpenShelf"] = "\"elsewhere\"";   // the registry is the truth, not the file
        Assert.True(Store().OpenAtLogin);
    }

    [Fact]
    public void OutputIsKeptUnderTheOutputKey()
    {
        var p = Store();
        var o = new OutputSettings { Audio = new AudioOutput { Kbps = 320 } };
        p.SetOutput(o);
        p.Flush();
        Assert.Equal(o, Store().GetOutput());
        p.SetOutput(OutputSettings.Automatic);
        Assert.Equal(OutputSettings.Automatic, p.GetOutput());
    }

    [Fact]
    public void WatchedFoldersRoundTripAndSkipBadEntries()
    {
        var json = WatchedFolderList.Encode([new(@"C:\a", true, null), new(@"C:\b", false, "convert.jpg")]);
        var list = WatchedFolderList.Decode(json);
        Assert.Equal(2, list.Count);
        Assert.Equal(new WatchedFolder(@"C:\b", false, "convert.jpg"), list[1]);
        var odd = WatchedFolderList.Decode("[{\"path\":\"C:\\\\x\"},5,{\"enabled\":true},{\"path\":\"c:\\\\X\\\\\"}]");
        Assert.Single(odd);
        Assert.Empty(WatchedFolderList.Decode("nope"));
    }

    [Fact]
    public void ModifierRecorderChoosesWhenEverythingIsReleased()
    {
        var r = new ModifierRecorder();
        Assert.Null(r.Update(Modifiers.Shift));
        Assert.Null(r.Update(Modifiers.Shift | Modifiers.Alt));
        Assert.Null(r.Update(Modifiers.Alt));
        Assert.Equal(Modifiers.Shift | Modifiers.Alt, r.Update(Modifiers.None));
        Assert.Null(r.Update(Modifiers.None));
    }

    [Fact]
    public void KeyNamesAreReadable()
    {
        Assert.Equal("Space", KeyNames.KeyName(0x20));
        Assert.Equal("A", KeyNames.KeyName(0x41));
        Assert.Equal("F5", KeyNames.KeyName(0x74));
        Assert.Equal(["Ctrl", "Alt", "Space"], KeyNames.Keycaps(new HotKey(Modifiers.Control | Modifiers.Alt, 0x20)));
        Assert.Equal(["Ctrl", "Shift", "Alt"], KeyNames.ModifierNames(Modifiers.Alt | Modifiers.Shift | Modifiers.Control));
    }
}

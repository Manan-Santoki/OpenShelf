using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Settings.Demo;

/// <summary>Shows the settings window at a fixed position with scratch preferences and saves a screenshot of it.
/// Usage: --tab general|wheels|shelves|output|formats|updates  --shot file.png  [--install id]  [--no-exit]</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var tab = Arg("--tab") ?? "general";
        var shot = Arg("--shot");
        var install = Arg("--install");
        var optionsId = Arg("--options");

        var temp = Path.Combine(Path.GetTempPath(), "openshelf-settings-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var preferences = new PreferencesStore(Path.Combine(temp, "preferences.json"), new MemoryRunKey());
        preferences.SetOutput(new OutputSettings
        {
            Video = new VideoOutput { Codec = VideoCodec.Hevc, Quality = VideoQuality.Custom, CustomMbps = 12 },
            Audio = new AudioOutput { Kbps = 192 },
        });
        preferences.SetJson(WatchedFolderList.Key, WatchedFolderList.Encode(
        [
            new WatchedFolder(WatchedFolderList.ScreenshotsFolder, true, null),
            new WatchedFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), true, "convert.jpg"),
            new WatchedFolder(@"D:\Scans\Receipts", false, "convert.pdf"),
        ]));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var commands = new AppCommands();
        var optionsService = new OutputOptionsService(preferences);
        var service = new SettingsWindowService(preferences, commands) { FixedPosition = new System.Windows.Point(120, 40), ToolInstaller = new FakeInstaller() };
        app.Startup += (_, _) =>
        {
            if (optionsId is null)
            {
                service.Show(tab);
                service.Window!.Topmost = true;   // keep other windows from covering the capture
            }
            else
            {
                _ = optionsService.AskAsync(optionsId, optionsId == "pdf-jpeg" ? "JPG" : optionsId.ToUpperInvariant(), ["clip.mov"], new OpenShelf.Core.ScreenPoint(700, 420),
                    async (_, ct) => { await Task.Delay(400, ct); return 24_000_000L; });
                optionsService.Current!.Topmost = true;
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(install is null ? 1100 : 1700) };
            if (install is not null)
            {
                var kick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                kick.Tick += (_, _) => { kick.Stop(); ((SettingsWindow)service.Window!).Tools.Install(new ToolRow(install, install, "", false, null, false)); };
                kick.Start();
            }
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (shot is not null) { Capture(optionsId is null ? service.Window! : optionsService.Current!, shot); app.Shutdown(); }
            };
            timer.Start();
        };
        app.Run();
        try { Directory.Delete(temp, true); } catch (IOException) { }
        return 0;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static void Capture(Window window, string file)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        SetForegroundWindow(handle);
        DwmGetWindowAttribute(handle, 9, out var r, Marshal.SizeOf<Rect>());   // DWMWA_EXTENDED_FRAME_BOUNDS
        using var bitmap = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        bitmap.Save(file, ImageFormat.Png);
    }
}

internal sealed class MemoryRunKey : IRunKeyRegistry
{
    private readonly Dictionary<string, string> _values = [];
    public string? GetValue(string name) => _values.GetValueOrDefault(name);
    public void SetValue(string name, string value) => _values[name] = value;
    public void DeleteValue(string name) => _values.Remove(name);
}

internal sealed class FakeInstaller : IToolInstallerUi
{
    public IReadOnlyList<ToolRow> Tools { get; } =
    [
        new("ffmpeg", "ffmpeg", "Video, audio and GIF formats: MKV, WebM, AVI, WMV, MP3, OGG, Opus and more.", true, @"C:\Users\you\AppData\Local\OpenShelf\tools\ffmpeg\ffmpeg.exe", true),
        new("libreoffice", "LibreOffice", "Office documents, slides and spreadsheets: DOCX, PPTX, XLSX, ODT and more.", false, null, false),
        new("pandoc", "Pandoc", "Markdown, Word, EPUB and OpenDocument text conversions.", true, @"C:\Program Files\Pandoc\pandoc.exe", false),
        new("calibre", "calibre", "E-books: EPUB, MOBI, AZW3 and more.", false, null, false),
    ];

    public event Action? Changed;

    public async Task InstallAsync(string id, IProgress<double> progress, CancellationToken ct)
    {
        if (id == "calibre") { await Task.Delay(100, ct); throw new InvalidOperationException("Couldn't reach download.calibre-ebook.com. Check your connection and try again."); }
        progress.Report(0.42);
        await Task.Delay(TimeSpan.FromMinutes(5), ct);   // never finishes: the screenshot shows the progress state
        Changed?.Invoke();
    }

    public Task RemoveAsync(string id) => Task.CompletedTask;
}

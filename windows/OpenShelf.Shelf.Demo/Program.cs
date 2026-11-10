using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Shelf;
using OpenShelf.Ui;

namespace OpenShelf.Shelf.Demo;

/// <summary>Puts the shelf UI into a named state by calling its code directly, then screenshots a screen region and exits.
/// Usage: --state collapsed|expanded-grid|expanded-list|selected|docked-left|hidden-tab|context-menu|preview|progress|toast|many --shot file.png</summary>
public static class Program
{
    sealed class FakePreferences : IPreferences
    {
        public event Action? Changed { add { } remove { } }
        public IReadOnlyDictionary<WheelKind, Modifiers> Triggers { get; } = new Dictionary<WheelKind, Modifiers>();
        public void SetTrigger(WheelKind wheel, Modifiers modifiers) { }
        public HotKey? SelectionHotKey { get; set; }
        public bool ShakeToOpen { get; set; } = true;
        public bool RemoveAfterDragOut { get; set; }
        public bool RestoreShelves { get; set; }
        public bool HideDockedShelves { get; set; } = true;
        public bool ResultsBackOnShelf { get; set; } = true;
        public bool OpenAtLogin { get; set; }
        public bool AutomaticUpdates { get; set; }
        public string? GetJson(string key) => null;
        public void SetJson(string key, string? json) { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string state = "collapsed", shot = "shot.png";
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--state") state = args[i + 1];
            if (args[i] == "--shot") shot = args[i + 1];
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 0;
        app.Startup += (_, _) =>
        {
            try { Run(app, state, shot); }
            catch (Exception ex) { File.WriteAllText(shot + ".error.txt", ex.ToString()); code = 1; app.Shutdown(); }
        };
        app.Run();
        return code;
    }

    static void Run(Application app, string state, string shot)
    {
        var dir = Path.Combine(Path.GetTempPath(), "openshelf-shelf-demo");
        if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch (Exception) { }
        Directory.CreateDirectory(dir);
        var prefs = new FakePreferences();
        var toasts = new ToastService();
        var shelves = new ShelfService(prefs, toasts, Path.Combine(dir, "shelves.json"));
        shelves.RestoreSaved();

        var files = state == "many" ? SampleFiles(dir, 25) : SampleFiles(dir, 5);

        // Capture region (physical pixels). Docked states need the screen edge in it; the preview is centred on the screen.
        int rx = 300, ry = 200, rw = 1000, rh = 860;
        bool edgeState = state is "docked-left" or "hidden-tab";
        if (edgeState) rx = 0;
        int sw = (int)SystemParameters.PrimaryScreenWidth, sh = (int)SystemParameters.PrimaryScreenHeight;
        double dpi = VisualTreeHelper.GetDpi(new Window()).DpiScaleX;
        sw = (int)(sw * dpi); sh = (int)(sh * dpi);
        if (state == "preview") { rx = 0; ry = 0; rw = sw; rh = sh; }
        rw = Math.Min(rw, sw - rx); rh = Math.Min(rh, sh - ry);

        var stage = MakeStage(rx, ry, rw, rh);
        stage.Show();
        DoEvents();

        var at = new ScreenPoint(400, 300);
        ShelfWindow? w = null;
        switch (state)
        {
            case "collapsed": w = Open(shelves, files, false, ShelfViewMode.Grid, at); break;
            case "expanded-grid": w = Open(shelves, files, true, ShelfViewMode.Grid, at); break;
            case "hittest": w = Open(shelves, files, true, ShelfViewMode.Grid, at); break;
            case "expanded-list": w = Open(shelves, files, true, ShelfViewMode.List, at); break;
            case "selected": w = Open(shelves, files, true, ShelfViewMode.Grid, at); w.SelectIndices(1, 3); break;
            case "docked-left":
                w = Open(shelves, files, true, ShelfViewMode.Grid, at);
                w.MoveCardTo(30, 300, settle: true);
                break;
            case "hidden-tab":
                w = Open(shelves, files, true, ShelfViewMode.Grid, at);
                w.MoveCardTo(30, 300, settle: true);
                break;
            case "context-menu":
                w = Open(shelves, files, true, ShelfViewMode.Grid, at);
                w.SelectIndices(1);
                break;
            case "preview":
                w = Open(shelves, files, true, ShelfViewMode.Grid, at);
                break;
            case "many":
                w = Open(shelves, files, true, ShelfViewMode.Grid, at);
                w.SelectIndices(11, 12);
                break;
            case "progress":
                {
                    var cts = new CancellationTokenSource();
                    var card = new ProgressService(toasts).Begin("Converting to HEIC", 3, new ScreenPoint(450, 350), cts);
                    card.Report(2, 0.4);
                    break;
                }
            case "toast": toasts.Show("Copied path", new ScreenPoint(450, 350)); break;
            default: throw new ArgumentException("Unknown state " + state);
        }

        // Things that need the window on screen first.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            try
            {
                if (state == "hittest") { var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) }; later.Tick += (_, _) => { later.Stop(); File.WriteAllText(shot + ".txt", HitReport(w!)); }; later.Start(); }
                if (state == "hidden-tab") w!.TuckAway();
                if (state == "context-menu") w!.ShowMenu(w.Model.Selected, w.TileCentre(1));
                if (state == "preview") shelves.Preview.Open(files, 0, new ScreenPoint(400, 300));
                if (state == "many") w!.ScrollToIndexNow(12);
            }
            catch (Exception ex) { File.WriteAllText(shot + ".error.txt", ex.ToString()); }
        };
        delay.Start();

        var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(state == "toast" ? 600 : state == "many" ? 2200 : 1800) };
        done.Tick += (_, _) =>
        {
            done.Stop();
            try
            {
                var bmp = Glass.Capture(rx, ry, rw, rh);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp!));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shot))!);
                using var fs = File.Create(shot);
                enc.Save(fs);
            }
            catch (Exception ex) { File.WriteAllText(shot + ".error.txt", ex.ToString()); }
            app.Shutdown();
        };
        done.Start();
    }

    /// <summary>What the pointer would land on at the tile's icon, its label and its padding corners (no mouse is used).</summary>
    static string HitReport(ShelfWindow w)
    {
        FrameworkElement? tile = null;
        void Find(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d) && tile is null; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is Border { Width: 96, Height: 112 } b) { tile = b; return; }
                Find(c);
            }
        }
        Find(w);
        if (tile is null) return "no tile found";
        var lines = new List<string>();
        foreach (var (name, x, y) in new (string, double, double)[]
        {
            ("icon centre", 48, 34), ("label", 48, 88), ("top-left padding", 4, 4), ("top-right padding", 92, 4),
            ("left of icon", 6, 34), ("bottom edge", 48, 108),
        })
            lines.Add($"{name}: {(VisualTreeHelper.HitTest(tile, new Point(x, y))?.VisualHit.GetType().Name ?? "nothing (falls through to the canvas)")}");
        return string.Join(Environment.NewLine, lines);
    }

    static ShelfWindow Open(ShelfService shelves, List<string> files, bool expanded, ShelfViewMode mode, ScreenPoint at)
    {
        var w = shelves.OpenWindow(files, expanded, mode);
        w.PlaceNear(at);
        w.MoveCardTo(at.X, at.Y, settle: false);
        return w;
    }

    static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>A light, softly coloured backdrop so shadows and the glass blur are visible in the screenshot.</summary>
    static Window MakeStage(int x, int y, int w, int h)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            ShowActivated = false, AllowsTransparency = false, Left = 0, Top = 0, Width = 100, Height = 100,
        };
        var grid = new Grid
        {
            Background = new LinearGradientBrush(Color.FromRgb(0xE9, 0xEC, 0xF3), Color.FromRgb(0xC9, 0xD3, 0xE6), 60),
        };
        var rnd = new Random(3);
        var canvas = new Canvas();
        for (int i = 0; i < 14; i++)
        {
            var e = new System.Windows.Shapes.Ellipse
            {
                Width = 80 + rnd.Next(160), Height = 80 + rnd.Next(160),
                Fill = new SolidColorBrush(Color.FromArgb(120, (byte)rnd.Next(80, 255), (byte)rnd.Next(80, 255), (byte)rnd.Next(120, 255))),
            };
            Canvas.SetLeft(e, rnd.Next(0, 700)); Canvas.SetTop(e, rnd.Next(0, 650));
            canvas.Children.Add(e);
        }
        grid.Children.Add(canvas);
        win.Content = grid;
        win.SourceInitialized += (_, _) =>
        {
            var h2 = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            Native.SetPos(h2, x, y, w, h);
        };
        return win;
    }

    // ---------- sample files ----------

    static List<string> SampleFiles(string dir, int count)
    {
        var list = new List<string>();
        var scenes = new (string Name, Color Top, Color Bottom, int Kind)[]
        {
            ("Beach Sunset.jpg", Color.FromRgb(0xF4, 0x9A, 0x7B), Color.FromRgb(0x2D, 0x6C, 0xA8), 0),
            ("Mountain Trail.jpg", Color.FromRgb(0x7F, 0xB4, 0xE8), Color.FromRgb(0x4E, 0x8F, 0x5A), 1),
            ("City Lights.jpg", Color.FromRgb(0x14, 0x14, 0x40), Color.FromRgb(0x3A, 0x2A, 0x70), 2),
            ("Product Demo.png", Color.FromRgb(0xE0, 0x48, 0x6A), Color.FromRgb(0xF5, 0xC8, 0x3D), 3),
        };
        foreach (var s in scenes.Take(Math.Min(count, scenes.Length)))
        {
            var path = Path.Combine(dir, s.Name);
            DrawImage(path, s.Top, s.Bottom, s.Kind);
            list.Add(path);
        }
        var md = Path.Combine(dir, "Trip Notes.md");
        File.WriteAllText(md, "# Trip notes\n\nPack light. Bring the good camera.\n\n- Passport\n- Charger\n- `tripod`\n");
        list.Add(md);
        if (count > 5)
        {
            var names = new[] { "Agenda", "Budget", "Contract", "Draft", "Eclair recipe", "Flights", "Guide", "Hotel", "Invoice", "Journal",
                "Keynote", "Letter", "Menu", "Notes", "Outline", "Plan", "Quote", "Report", "Summary", "Todo" };
            for (int i = 0; i < count - 5; i++)
            {
                var p = Path.Combine(dir, names[i % names.Length] + (i % 3 == 0 ? ".txt" : i % 3 == 1 ? ".md" : ".csv"));
                File.WriteAllText(p, "Sample " + i + "\n");
                list.Add(p);
            }
        }
        else
        {
            var txt = Path.Combine(dir, "Notes.txt");
            File.WriteAllText(txt, "Remember to send the files on Friday.\n");
            list.Insert(Math.Min(4, list.Count), txt);
            list.Remove(txt);
        }
        return list;
    }

    static void DrawImage(string path, Color top, Color bottom, int kind)
    {
        const int W = 480, H = 320;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(0, 0, W, H));
            switch (kind)
            {
                case 0:
                    dc.DrawEllipse(Brushes.White, null, new Point(240, 170), 36, 36);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xD3, 0xA2)), null, new Rect(0, 250, W, 70));
                    break;
                case 1:
                    dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x5B, 0x6B, 0x8C)), null, Geometry.Parse("M0,260 L150,90 L260,230 L340,140 L480,270 L480,320 L0,320 Z"));
                    break;
                case 2:
                    var r = new Random(5);
                    for (int i = 0; i < 24; i++)
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x55)), null, new Rect(i * 20, 320 - 40 - r.Next(150), 16, 400));
                    for (int i = 0; i < 80; i++)
                        dc.DrawRectangle(Brushes.Gold, null, new Rect(r.Next(480), 150 + r.Next(170), 3, 3));
                    break;
                default:
                    for (int i = 0; i < 6; i++)
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(40 * i + 20), (byte)(255 - 30 * i), (byte)(60 + 25 * i))), null, new Rect(i * 80, 60, 80, 200));
                    break;
            }
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        BitmapEncoder enc = path.EndsWith(".png") ? new PngBitmapEncoder() : new JpegBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void SetPos(IntPtr hwnd, int x, int y, int w, int h) => SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, 0x10);
}

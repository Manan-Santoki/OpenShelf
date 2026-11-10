using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Shelf;

/// <summary>OpenShelf's own preview (the Quick Look equivalent): images fitted, text as monospaced text,
/// anything else as its large shell thumbnail with name, size and type. Space or Esc closes it; Left/Right move through the files.</summary>
internal sealed class PreviewWindow : Window
{
    const double Pad = 28;
    const double Radius = 16;
    const double HeaderHeight = 44;
    const int MaxTextBytes = 200 * 1024;

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".xml", ".csv", ".tsv", ".yml", ".yaml", ".toml", ".ini", ".cfg",
        ".conf", ".html", ".htm", ".css", ".js", ".ts", ".tsx", ".jsx", ".cs", ".vb", ".py", ".rb", ".go", ".rs", ".java",
        ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".sh", ".bat", ".cmd", ".ps1", ".sql", ".tex", ".rtf", ".srt", ".vtt",
        ".gitignore", ".env", ".csproj", ".sln", ".xaml", ".svg", ".reg", ".lua", ".php", ".r", ".m",
    };

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".jfif", ".dib", ".wdp",
    };

    readonly IReadOnlyList<string> _paths;
    readonly Border _card = new();
    readonly TextBlock _title = new();
    readonly TextBlock _counter = new();
    readonly Border _body = new();
    int _index;
    IntPtr _hwnd;
    bool _closing;

    public event Action? Dismissed;

    public PreviewWindow(IReadOnlyList<string> paths, int index)
    {
        AppIcons.Apply(this);
        _paths = paths;
        _index = Math.Clamp(index, 0, Math.Max(0, paths.Count - 1));

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Theme.TextFont;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Background = Brushes.Transparent };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount > 1) return;
            try { DragMove(); } catch (Exception) { }
        };
        _title.FontFamily = Theme.Font;
        _title.FontSize = 14;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Theme.White(0.75);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.Margin = new Thickness(70, 0, 70, 0);
        _title.IsHitTestVisible = false;
        _counter.FontSize = 12;
        _counter.Foreground = Theme.White(0.42);
        _counter.HorizontalAlignment = HorizontalAlignment.Right;
        _counter.VerticalAlignment = VerticalAlignment.Center;
        _counter.Margin = new Thickness(0, 0, 16, 0);
        _counter.IsHitTestVisible = false;
        header.Children.Add(_title);
        header.Children.Add(_counter);
        root.Children.Add(header);

        Grid.SetRow(_body, 1);
        root.Children.Add(_body);

        _card.Margin = new Thickness(Pad);
        _card.CornerRadius = new CornerRadius(Radius);
        _card.Background = Theme.Vertical((Color.FromRgb(0x2C, 0x2C, 0x30), 0.98, 0), (Theme.Surface, 0.98, 1));
        _card.BorderBrush = Theme.White(0.15);
        _card.BorderThickness = new Thickness(1);
        _card.Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 12, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        _card.Child = root;
        Content = _card;

        PreviewKeyDown += OnKey;
        Closed += (_, _) => Dismissed?.Invoke();
        Load();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: case Key.Space: Dismiss(); break;
            case Key.Right: case Key.Down: Step(1); break;
            case Key.Left: case Key.Up: Step(-1); break;
            default: return;
        }
        e.Handled = true;
    }

    void Step(int delta)
    {
        if (_paths.Count < 2) return;
        _index = (_index + delta + _paths.Count) % _paths.Count;
        Load();
        Recentre();
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch (Exception) { }
    }

    /// <summary>Shows the window centred on the work area under <paramref name="near"/> and takes the keyboard.</summary>
    public void ShowCentred(ScreenPoint near)
    {
        _nearPoint = near;
        Show();
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW);
        Recentre();
        Native.SetForegroundWindow(_hwnd);
        Activate();
        Focus();
        Motion.Animate(_card, OpacityProperty, 1, 140, Motion.EaseOut, from: 0);
    }

    ScreenPoint _nearPoint;

    void Recentre()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            UpdateLayout();
            var (scale, work) = Native.MonitorAt(_nearPoint);
            int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
            int x = (int)(work.Left + (work.Right - work.Left - w) / 2.0);
            int y = (int)(work.Top + (work.Bottom - work.Top - h) / 2.0);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER);
        }
        catch (Exception ex) { Log.Error("Preview centre", ex); }
    }

    // ---------- content ----------

    void Load()
    {
        var path = _paths[_index];
        _title.Text = Path.GetFileName(path.TrimEnd('\\', '/'));
        _counter.Text = _paths.Count > 1 ? $"{_index + 1} of {_paths.Count}" : "";
        try
        {
            _body.Child = BuildBody(path);
        }
        catch (Exception ex)
        {
            Log.Error("Preview load " + path, ex);
            _body.Child = Message("Can't preview this file.");
        }
    }

    UIElement BuildBody(string path)
    {
        var ext = Path.GetExtension(path);
        bool isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return Message("This file is no longer there.");

        if (!isDir && ImageExtensions.Contains(ext) && TryImage(path) is { } image) return image;
        if (!isDir && (TextExtensions.Contains(ext) || LooksLikeText(path)) && TryText(path) is { } text) return text;
        return Generic(path, isDir);
    }

    UIElement Message(string text) => new TextBlock
    {
        Text = text, Foreground = Theme.White(0.55), FontSize = 14, Width = 420, Height = 240,
        TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 100, 0, 0),
    };

    UIElement? TryImage(string path)
    {
        BitmapSource? source = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 0;
            bmp.EndInit();
            if (bmp.PixelWidth > 2400) { var scaled = new BitmapImage(); scaled.BeginInit(); scaled.CacheOption = BitmapCacheOption.OnLoad; scaled.UriSource = new Uri(path); scaled.DecodePixelWidth = 2400; scaled.EndInit(); scaled.Freeze(); bmp = scaled; }
            else bmp.Freeze();
            source = bmp;
        }
        catch (Exception) { source = ShellThumbnails.Load(path, 1024, 1024); }
        if (source == null) return null;

        // Source pixels are in device pixels at 96 dpi; fit them to a comfortable window in DIPs.
        double pw = source.PixelWidth * 96.0 / source.DpiX, ph = source.PixelHeight * 96.0 / source.DpiY;
        const double maxW = 880, maxH = 600, minW = 360, minH = 220;
        double scale = Math.Min(1, Math.Min(maxW / pw, maxH / ph));
        double w = pw * scale, h = ph * scale;
        double grow = Math.Max(1, Math.Max(minW / w, minH / h));
        if (grow > 1 && pw * grow < maxW * 2) { w *= grow; h *= grow; w = Math.Min(w, maxW); h = Math.Min(h, maxH); }
        var img = new Image
        {
            Source = source, Width = Math.Round(w), Height = Math.Round(h), Stretch = Stretch.Uniform,
            Clip = new RectangleGeometry(new Rect(0, 0, Math.Round(w), Math.Round(h)), 8, 8),
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var panel = new StackPanel { Margin = new Thickness(18, 0, 18, 14), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(img);
        panel.Children.Add(Caption($"{TypeName(path)}  ·  {source.PixelWidth} × {source.PixelHeight}  ·  {Size(path)}"));
        return panel;
    }

    UIElement? TryText(string path)
    {
        string text;
        bool truncated;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[MaxTextBytes];
            int read = 0, n;
            while (read < buffer.Length && (n = fs.Read(buffer, read, buffer.Length - read)) > 0) read += n;
            truncated = fs.Length > MaxTextBytes;
            text = Decode(buffer, read, truncated);
        }
        catch (Exception) { return null; }

        var block = new TextBox
        {
            Text = text, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Theme.White(0.88), FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"), FontSize = 12.5,
            TextWrapping = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0), CaretBrush = Brushes.Transparent, SelectionBrush = Theme.Brush(Theme.Accent, 0.5),
            Focusable = false, IsHitTestVisible = true, IsTabStop = false,
        };
        var holder = new Border
        {
            Width = 720, Height = 480, Margin = new Thickness(18, 0, 18, 0), Padding = new Thickness(14, 12, 6, 12),
            Background = Theme.Black(0.22), CornerRadius = new CornerRadius(8), Child = block,
        };
        var panel = new StackPanel();
        panel.Children.Add(holder);
        panel.Children.Add(Caption(truncated ? $"Showing the first 200 KB  ·  {Size(path)}" : $"{TypeName(path)}  ·  {Size(path)}"));
        return panel;
    }

    UIElement Generic(string path, bool isDir)
    {
        var thumb = ShellThumbnails.Load(path, 512, 512);
        var panel = new StackPanel { Width = 420, Margin = new Thickness(0, 6, 0, 22), HorizontalAlignment = HorizontalAlignment.Center };
        var img = new Image { Source = thumb, Width = 192, Height = 192, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        panel.Children.Add(img);
        panel.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path.TrimEnd('\\', '/')), FontFamily = Theme.Font, FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = Theme.White(0.92), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 14, 24, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = isDir ? "Folder" : $"{TypeName(path)}  ·  {Size(path)}", FontSize = 13, Foreground = Theme.White(0.5),
            TextAlignment = TextAlignment.Center,
        });
        return panel;
    }

    static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Theme.White(0.45), TextAlignment = TextAlignment.Center,
        Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
    };

    // ---------- helpers ----------

    static bool LooksLikeText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4096];
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read == 0) return false;
            for (int i = 0; i < read; i++) if (buffer[i] == 0) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    static string Decode(byte[] bytes, int length, bool truncated)
    {
        int start = 0;
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
        if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, length - 2);
        if (length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, length - 2);
        try
        {
            // A file cut at 200 KB may end mid-character; trim a partial sequence before the strict decode.
            int end = length;
            if (truncated) while (end > start && (bytes[end - 1] & 0xC0) == 0x80) end--;
            if (truncated && end > start && bytes[end - 1] >= 0xC0) end--;
            return new UTF8Encoding(false, true).GetString(bytes, start, end - start);
        }
        catch (Exception) { return Encoding.GetEncoding(1252).GetString(bytes, start, length - start); }
    }

    static string Size(string path)
    {
        try
        {
            double bytes = new FileInfo(path).Length;
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
            return u == 0 ? $"{bytes:0} bytes" : $"{bytes:0.#} {units[u]}";
        }
        catch (Exception) { return ""; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    static string TypeName(string path)
    {
        var info = new ShFileInfo();
        const uint TypeNameFlag = 0x400;
        SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), TypeNameFlag);
        return string.IsNullOrEmpty(info.TypeName) ? "File" : info.TypeName;
    }
}

/// <summary>Owns the single preview window.</summary>
internal sealed class PreviewController
{
    PreviewWindow? _window;
    Action? _closed;

    public bool IsOpen => _window != null;

    /// <summary>Opens the preview for <paramref name="paths"/>, or closes it when it is already open.</summary>
    public void Toggle(IReadOnlyList<string> paths, ScreenPoint near, Action? closed = null)
    {
        if (_window != null) { _window.Dismiss(); return; }
        if (paths.Count == 0) return;
        Open(paths, 0, near, closed);
    }

    public void Open(IReadOnlyList<string> paths, int index, ScreenPoint near, Action? closed = null)
    {
        _window?.Dismiss();
        var w = new PreviewWindow(paths, index);
        _window = w;
        _closed = closed;
        w.Dismissed += () =>
        {
            if (ReferenceEquals(_window, w)) _window = null;
            var cb = _closed;
            _closed = null;
            cb?.Invoke();
        };
        w.ShowCentred(near);
    }

    public void Close() => _window?.Dismiss();
}

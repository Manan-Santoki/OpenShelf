using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Settings;

/// <summary>The small window shown when a format's wedge runs with Alt held: only the output settings that
/// apply to that format, prefilled from Settings → Output, for one conversion (SPEC 2.3.1).</summary>
public sealed class OutputOptionsService
{
    private readonly IPreferences _preferences;

    public OutputOptionsService(IPreferences preferences) => _preferences = preferences;

    internal Window? Current { get; private set; }

    /// <summary>Asks for the settings to use once. Returns null when cancelled. Formats with nothing to choose return
    /// the stored settings at once. "Use as default" also saves the rows shown, leaving the rest of Settings → Output alone.</summary>
    /// <param name="formatId">Lower-case format id: mp4, mov, mkv, webm, avi, wmv, m4a, mp3, ogg, opus, wma, jpg, heic, avif, webp, gif, or "pdf-jpeg".</param>
    /// <param name="near">Physical screen pixels; the window is centred there and kept inside the monitor's work area.</param>
    /// <param name="estimateBytes">Estimated output size in bytes for video formats (null result: unknown, line hidden).</param>
    public Task<OutputSettings?> AskAsync(string formatId, string formatTitle, IReadOnlyList<string> files, ScreenPoint near,
        Func<OutputSettings, CancellationToken, Task<long?>>? estimateBytes = null)
    {
        var options = OutputOptions.For(formatId);
        if (options.IsEmpty) return Task.FromResult<OutputSettings?>(_preferences.GetOutput());

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => AskAsync(formatId, formatTitle, files, near, estimateBytes)).Task.Unwrap();

        var stored = _preferences.GetOutput();
        var completion = new TaskCompletionSource<OutputSettings?>();
        var window = new OptionsWindow($"Convert to {formatTitle}", options, stored, estimateBytes, near);
        Current = window;
        window.Closed += (_, _) =>
        {
            Current = null;
            if (!window.Accepted) { completion.TrySetResult(null); return; }
            var result = _preferences.GetOutput().Applying(options, window.Shown);
            if (window.UseAsDefault) _preferences.SetOutput(result);
            completion.TrySetResult(result);
        };
        window.Show();
        window.Activate();
        return completion.Task;
    }
}

internal sealed class OptionsWindow : Window
{
    private readonly OutputOptions _options;
    private readonly Func<OutputSettings, CancellationToken, Task<long?>>? _estimate;
    private readonly ScreenPoint _near;
    private readonly TextBlock _estimateLabel = Kit.Text("", 12, "Text3");
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _estimating;
    private readonly CheckBox _default;

    public OptionsWindow(string title, OutputOptions options, OutputSettings initial, Func<OutputSettings, CancellationToken, Task<long?>>? estimate, ScreenPoint near)
    {
        _options = options;
        _estimate = estimate;
        _near = near;
        Shown = initial;

        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;   // placed in Loaded, once the size and the monitor's scale are known
        ShowActivated = true;
        Background = Kit.Brush("Window");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = Theme.TextFont;
        FontSize = 13;
        Foreground = Kit.Brush("Text");
        Resources.MergedDictionaries.Add(Kit.Dictionary);

        var rows = new List<Border>();
        var bitrateRow = default(SettingRow);

        void Edit(Func<OutputSettings, OutputSettings> change)
        {
            Shown = change(Shown);
            if (bitrateRow is not null)
                bitrateRow.Visibility = Shown.Video.Quality == VideoQuality.Custom ? Visibility.Visible : Visibility.Collapsed;
            ScheduleEstimate();
        }

        if (options.VideoCodec)
        {
            var c = new Segmented<VideoCodec>([.. Enum.GetValues<VideoCodec>().Select(v => (VideoOutput.Title(v), v))],
                v => Edit(o => o with { Video = o.Video with { Codec = v } }), "Codec") { Selected = initial.Video.Codec };
            rows.Add(new SettingRow("Codec", null, c));
        }
        if (options.Video)
        {
            var q = new Segmented<VideoQuality>([.. Enum.GetValues<VideoQuality>().Select(v => (VideoOutput.Title(v), v))],
                v => Edit(o => o with { Video = o.Video with { Quality = v } }), "Quality") { Selected = initial.Video.Quality };
            rows.Add(new SettingRow("Quality", null, q));
            var slider = new MbpsSlider(m => Edit(o => o with { Video = o.Video with { CustomMbps = m } })) { Mbps = initial.Video.CustomMbps };
            bitrateRow = new SettingRow("Bitrate", null, slider)
            {
                Visibility = initial.Video.Quality == VideoQuality.Custom ? Visibility.Visible : Visibility.Collapsed,
            };
            rows.Add(bitrateRow);
            var s = new Segmented<VideoMaxSize>([.. Enum.GetValues<VideoMaxSize>().Select(v => (VideoOutput.Title(v), v))],
                v => Edit(o => o with { Video = o.Video with { MaxSize = v } }), "Largest size") { Selected = initial.Video.MaxSize };
            rows.Add(new SettingRow("Largest size", null, s));
        }
        if (options.AudioBitRate)
        {
            var d = new Dropdown<int?>([("Automatic", (int?)null), .. AudioOutput.Choices.Select(k => ($"{k} kb/s", (int?)k))],
                v => Edit(o => o with { Audio = new AudioOutput { Kbps = v } }), "Audio bitrate") { Selected = initial.Audio.Kbps };
            rows.Add(new SettingRow(options.Video ? "Audio" : "Bitrate", null, d));
        }
        if (options.ImageQuality)
        {
            var d = new Dropdown<double?>([("Automatic", (double?)null), .. ImageOutput.QualityChoices.Select(q => ($"{(int)Math.Round(q * 100)}%", (double?)q))],
                v => Edit(o => o with { Image = o.Image with { Quality = v } }), "Image quality") { Selected = initial.Image.Quality };
            rows.Add(new SettingRow("Quality", null, d));
        }
        if (options.ImageMetadata)
        {
            var toggle = Kit.Switch("Keep metadata");
            toggle.IsChecked = initial.Image.KeepsMetadata;
            toggle.Click += (_, _) => Edit(o => o with { Image = o.Image with { KeepsMetadata = toggle.IsChecked == true } });
            var row = new SettingRow("Keep metadata", null, toggle) { Cursor = Cursors.Hand };
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (e.Handled) return;
                toggle.IsChecked = toggle.IsChecked != true;
                Edit(o => o with { Image = o.Image with { KeepsMetadata = toggle.IsChecked == true } });
                e.Handled = true;
            };
            rows.Add(row);
        }
        if (options.Gif)
        {
            var w = new Dropdown<GifWidth>([.. Enum.GetValues<GifWidth>().Select(v => (GifOutput.Title(v), v))],
                v => Edit(o => o with { Gif = o.Gif with { Width = v } }), "GIF width", 220) { Selected = initial.Gif.Width };
            rows.Add(new SettingRow("Width", null, w));
            var r = new Segmented<int>([.. GifOutput.FrameRates.Select(v => ($"{v} fps", v))],
                v => Edit(o => o with { Gif = o.Gif with { FrameRate = v } }), "GIF frame rate") { Selected = initial.Gif.FrameRate };
            rows.Add(new SettingRow("Frame rate", null, r));
        }

        var stack = new StackPanel { Width = 560, Margin = new Thickness(24, 22, 24, 22) };
        stack.Children.Add(Kit.Text(title, 17, weight: FontWeights.SemiBold));
        var card = new Card([.. rows]) { Margin = new Thickness(0, 14, 0, 0) };
        stack.Children.Add(card);

        _estimateLabel.Margin = new Thickness(2, 10, 0, 0);
        _estimateLabel.Visibility = Visibility.Collapsed;
        stack.Children.Add(_estimateLabel);

        _default = new CheckBox { Style = Kit.Style("Switch"), VerticalAlignment = VerticalAlignment.Center };
        Kit.Name(_default, "Use as default");
        var defaultLabel = Kit.Text("Use as default", 13);
        defaultLabel.Margin = new Thickness(10, 0, 0, 0);
        defaultLabel.VerticalAlignment = VerticalAlignment.Center;
        defaultLabel.MouseLeftButtonUp += (_, _) => _default.IsChecked = _default.IsChecked != true;

        var cancel = Kit.Button("Cancel", (_, _) => Close());
        cancel.IsCancel = true;
        var convert = Kit.Button("Convert", (_, _) => { Accepted = true; Close(); }, "AccentBtn");
        convert.IsDefault = true;
        convert.Margin = new Thickness(8, 0, 0, 0);
        convert.MinWidth = 90;
        cancel.MinWidth = 80;

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_default);
        left.Children.Add(defaultLabel);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(cancel);
        right.Children.Add(convert);
        Grid.SetColumn(right, 1);
        footer.Children.Add(left);
        footer.Children.Add(right);
        stack.Children.Add(footer);
        Content = stack;

        _debounce.Tick += (_, _) => { _debounce.Stop(); RunEstimate(); };
        SourceInitialized += (_, _) => DarkTitle();
        Loaded += (_, _) => { Place(); AppIcons.Apply(this); };
        Closed += (_, _) => { _debounce.Stop(); _estimating?.Cancel(); };
        if (options.Video && estimate is not null) ScheduleEstimate();
    }

    public OutputSettings Shown { get; private set; }
    public bool Accepted { get; private set; }
    public bool UseAsDefault => _default.IsChecked == true;

    // ------------------------------------------------------------ estimate

    private void ScheduleEstimate()
    {
        if (!_options.Video || _estimate is null) return;
        _estimating?.Cancel();
        _estimateLabel.Text = "Estimating…";
        _estimateLabel.Visibility = Visibility.Visible;
        _debounce.Stop();
        _debounce.Start();
    }

    private async void RunEstimate()
    {
        _estimating?.Cancel();
        var cts = _estimating = new CancellationTokenSource();
        long? bytes = null;
        try { bytes = await _estimate!(Shown, cts.Token); }
        catch (Exception) { bytes = null; }
        if (cts.IsCancellationRequested) return;
        if (bytes is { } b) { _estimateLabel.Text = "About " + FormatSize(b); _estimateLabel.Visibility = Visibility.Visible; }
        else _estimateLabel.Visibility = Visibility.Collapsed;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1_000_000 => $"{Math.Max(1, Math.Round(bytes / 1000.0)):0} KB",
        < 1_000_000_000 => $"{bytes / 1_000_000.0:0} MB",
        _ => $"{bytes / 1_000_000_000.0:0.0} GB",
    };

    // ------------------------------------------------------------ placement

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Centres on the point in physical pixels, inside the work area of the monitor that holds it.</summary>
    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        GetWindowRect(handle, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_near.X, _near.Y)).WorkingArea;
        var x = Math.Clamp(_near.X - w / 2, area.Left, Math.Max(area.Left, area.Right - w));
        var y = Math.Clamp(_near.Y - h / 2, area.Top, Math.Max(area.Top, area.Bottom - h));
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
    }

    private void DarkTitle()
    {
        var on = 1;
        var handle = new WindowInteropHelper(this).Handle;
        if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
    }
}

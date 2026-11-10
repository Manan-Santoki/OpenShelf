using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Wheel;

/// <summary>
/// The one reusable wheel window. It first appears as an invisible "probe" (1/255 opaque so it
/// hit-tests everywhere); when an OLE drag with files enters it, the wheel is revealed.
/// </summary>
internal sealed class WheelWindow : Window
{
    private enum Phase { Hidden, Probing, Revealed }
    private enum RebuildMode { Open, Swap }

    private static readonly double Side = WheelGeometry.PanelSide;
    private static readonly Brush HitBrush = MakeHitBrush();

    private readonly Grid _root;
    private readonly Canvas _host;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly DispatcherTimer _probeTimer;
    private readonly DispatcherTimer _graceTimer;
    private readonly ShellDragHelper _shell = new();

    private Phase _phase = Phase.Hidden;
    private WheelKind _kind;
    private IReadOnlyList<string> _files = [];
    private WheelModel? _model;
    private WheelVisual? _visual;
    private WheelGeometry _geometry;
    private BitmapSource? _backdrop;
    private int _generation;
    private bool _helperIn;
    private bool _warm;
    private DispatcherTimer? _warmTimer;

    /// <summary>Builds the wheel for a kind and the dragged files; null means nothing to offer.</summary>
    public Func<WheelKind, IReadOnlyList<string>, WheelModel?>? BuildModel { get; set; }
    /// <summary>Called when a wheel kind has nothing to offer for the files (so the service can toast).</summary>
    public Action<WheelKind>? Unavailable { get; set; }
    /// <summary>Called after a wedge was dropped on, once the drop handler has returned.</summary>
    public Func<WheelItem, IReadOnlyList<string>, ScreenPoint, Task>? Dropped { get; set; }

    public WheelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowDrop = true;
        Focusable = false;
        Width = Side;
        Height = Side;
        Background = HitBrush;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _host = new Canvas
        {
            Width = Side, Height = Side, Opacity = 0, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _scale,
        };
        _root = new Grid { Background = HitBrush, AllowDrop = true };
        _root.Children.Add(_host);
        Content = _root;

        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _probeTimer.Tick += (_, _) =>
        {
            _probeTimer.Stop();
            if (_phase == Phase.Probing) { Log.Write("probe gave up: no DragEnter"); HideNow(); }
        };
        _graceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _graceTimer.Tick += (_, _) => { _graceTimer.Stop(); HideNow(animate: true); };

        SourceInitialized += (_, _) => ApplyStyles();
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave; // never closes the wheel
        Drop += OnDrop;
    }

    private static Brush MakeHitBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        b.Freeze();
        return b;
    }

    private nint Handle => new WindowInteropHelper(this).EnsureHandle();

    private void ApplyStyles()
    {
        try
        {
            var h = Handle;
            var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, ex | (nint)(Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        }
        catch (Exception e) { Log.Error("ApplyStyles", e); }
    }

    // ---- Lifecycle driven by the service ----

    /// <summary>
    /// Creates the HWND and renders a throw-away wheel off-screen so the first real wheel has nothing left to
    /// initialise (window creation, effect pipeline, fonts, geometry).
    /// </summary>
    public void Warmup()
    {
        try
        {
            var h = Handle;
            ApplyStyles();
            var side = (int)Math.Round(Side * Native.ScaleAt(Native.CursorPos()));
            Native.SetWindowPos(h, Native.HWND_TOPMOST, -32000, -32000, side, side, Native.SWP_NOACTIVATE);
            _warm = true;
            Show();
            Task Noop(IReadOnlyList<string> _, ScreenPoint __) => Task.CompletedTask;
            var model = new WheelModel("Actions",
            [
                new WheelItem("Shelf", Noop, ""), new WheelItem("Copy", Noop, ""),
                new WheelItem("Copy path", Noop, ""), new WheelItem("Terminal", Noop, ""),
            ]);
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var visual = new WheelVisual(model, "0 B", Glass.Capture(0, 0, 64, 64), dpi, animateIn: false);
            _host.Children.Clear();
            _host.Children.Add(visual.Root);
            _host.Opacity = 0.01;
            UpdateLayout();
            visual.SetHover(1);
            _warmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _warmTimer.Tick += (_, _) =>
            {
                _warmTimer?.Stop();
                if (!_warm || _phase != Phase.Hidden) return;
                _warm = false;
                _host.Children.Clear();
                _host.Opacity = 0;
                Hide();
                Log.Write("wheel window warmed up");
            };
            _warmTimer.Start();
        }
        catch (Exception e) { Log.Error("Warmup", e); }
    }

    /// <summary>A trigger chord was chosen during a possible drag.</summary>
    public void ShowProbe(WheelKind kind, ScreenPoint pointer)
    {
        _graceTimer.Stop();
        _kind = kind;
        switch (_phase)
        {
            case Phase.Revealed:
                if (!Rebuild(RebuildMode.Swap)) HideNow(); // swap contents in place
                return;
            case Phase.Probing:
                return;
        }

        _generation++;               // cancels a pending close
        _warm = false;
        _warmTimer?.Stop();
        SetClickThrough(false);
        if (IsVisible)
        {
            // Still fading out (or warming up): make sure our own pixels are gone before photographing the screen.
            Hide();
            Thread.Sleep(30);
        }

        var scale = Native.ScaleAt(pointer);
        var centre = KeepOnScreen(pointer, scale);
        _backdrop = CaptureBackdrop(centre, scale);

        _phase = Phase.Probing;
        _files = [];
        _model = null;
        _visual = null;
        _host.BeginAnimation(OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        _host.Opacity = 0;
        _host.Children.Clear();
        Place(centre, pointer, scale);
        _probeTimer.Stop();
        _probeTimer.Start();
        Log.Write($"probe shown ({kind}) at {pointer.X},{pointer.Y}, centre {centre.X},{centre.Y}, backdrop {(_backdrop is null ? "none" : "ok")}");
    }

    /// <summary>The left button was released: hide shortly, leaving room for a pending Drop to arrive first.</summary>
    public void DragEnded()
    {
        if (_phase != Phase.Hidden) { _graceTimer.Stop(); _graceTimer.Start(); }
    }

    /// <summary>Hides at once, or (animated) shrinks and fades out first.</summary>
    public void HideNow(bool animate = false, int delayMilliseconds = 0)
    {
        _probeTimer.Stop();
        _graceTimer.Stop();
        var wasRevealed = _phase == Phase.Revealed;
        _phase = Phase.Hidden;
        _model = null;
        _visual = null;
        EndHelper();
        var generation = ++_generation;
        try
        {
            if (animate && wasRevealed && IsVisible && Motion.Enabled)
            {
                SetClickThrough(true);
                Motion.Animate(_scale, ScaleTransform.ScaleXProperty, 0.85, 140, Motion.EaseOut, delayMilliseconds);
                Motion.Animate(_scale, ScaleTransform.ScaleYProperty, 0.85, 140, Motion.EaseOut, delayMilliseconds);
                Motion.Animate(_host, OpacityProperty, 0, 140, Motion.EaseOut, delayMilliseconds,
                    completed: () => { if (generation == _generation) FinishHide(); });
            }
            else FinishHide();
        }
        catch (Exception e) { Log.Error("HideNow", e); }
    }

    private void FinishHide()
    {
        _host.BeginAnimation(OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        _host.Opacity = 0;
        if (IsVisible) Hide();
        _host.Children.Clear();
        SetClickThrough(false);
    }

    private void EndHelper()
    {
        if (!_helperIn) return;
        _helperIn = false;
        _shell.Leave();
    }

    /// <summary>While fading out the window must not catch the pointer.</summary>
    private void SetClickThrough(bool on)
    {
        try
        {
            var h = Handle;
            var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            var next = on ? ex | (nint)Native.WS_EX_TRANSPARENT : ex & ~(nint)Native.WS_EX_TRANSPARENT;
            if (next != ex) Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, next);
        }
        catch (Exception e) { Log.Error("SetClickThrough", e); }
    }

    /// <summary>Moves the wheel's centre so the whole disc and the name pill stay inside the monitor's work area.</summary>
    private static ScreenPoint KeepOnScreen(ScreenPoint pointer, double scale)
    {
        if (Native.WorkAreaAt(pointer) is not { } work) return pointer;
        static int Clamp(int v, double lo, double hi) => (int)Math.Round(hi < lo ? (lo + hi) / 2 : Math.Clamp(v, lo, hi));
        var side = (WheelVisual.DiscRadius + 6) * scale;
        var top = (WheelVisual.NameCentreAbove + WheelVisual.NameHeight / 2 + 4) * scale;
        var bottom = (WheelVisual.DiscRadius + 12) * scale;
        return new ScreenPoint(Clamp(pointer.X, work.Left + side, work.Right - side), Clamp(pointer.Y, work.Top + top, work.Bottom - bottom));
    }

    /// <summary>Photographs the screen under the disc (plus padding) before our window is shown.</summary>
    private static BitmapSource? CaptureBackdrop(ScreenPoint centre, double scale)
    {
        try
        {
            var half = (int)Math.Ceiling((WheelVisual.DiscRadius + 40) * scale);
            return Glass.Capture(centre.X - half, centre.Y - half, half * 2, half * 2);
        }
        catch (Exception e) { Log.Error("CaptureBackdrop", e); return null; }
    }

    /// <summary>Centres the window on <paramref name="centre"/> in physical pixels; the bloom grows from the pointer.</summary>
    private void Place(ScreenPoint centre, ScreenPoint pointer, double scale)
    {
        var side = (int)Math.Round(Side * scale);   // e.g. 416 DIPs at 150% = 624 px
        var x = centre.X - side / 2;
        var y = centre.Y - side / 2;
        _host.RenderTransformOrigin = new Point(Math.Clamp((pointer.X - x) / (double)side, 0, 1), Math.Clamp((pointer.Y - y) / (double)side, 0, 1));
        var h = Handle;
        ApplyStyles();
        const uint flags = Native.SWP_NOACTIVATE;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, side, side, flags);
        if (!IsVisible) Show();
        // A move onto a monitor with another DPI makes WPF re-apply a suggested rect; set it again afterwards.
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, side, side, flags | Native.SWP_SHOWWINDOW);
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, side, side, flags | Native.SWP_SHOWWINDOW);
    }

    private bool Rebuild(RebuildMode mode)
    {
        var model = BuildModel?.Invoke(_kind, _files);
        if (model is null || model.Items.Count == 0)
        {
            Log.Write($"nothing to offer for {_kind}");
            Unavailable?.Invoke(_kind);
            return false;
        }
        var old = mode == RebuildMode.Swap ? _visual : null;
        _model = model;
        _geometry = new WheelGeometry(model.Items.Count);
        var animated = Motion.Enabled;
        var fresh = new WheelVisual(model, HubDefault(_files), _backdrop, VisualTreeHelper.GetDpi(this).DpiScaleX, animateIn: animated);
        _visual = fresh;
        _phase = Phase.Revealed;
        _probeTimer.Stop();

        if (old is not null)
        {
            // Swapping wheels: the new one sits underneath and its wedges bloom in while the old one dissolves on top.
            _host.Children.Insert(0, fresh.Root);
            var gone = old.Root;
            Motion.Animate(gone, OpacityProperty, 0, 170, Motion.EaseOut, completed: () => _host.Children.Remove(gone));
            fresh.PlayOpen(bloom: false);
        }
        else
        {
            _host.Children.Clear();
            _host.Children.Add(fresh.Root);
            _host.BeginAnimation(OpacityProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (animated)
            {
                Motion.Animate(_scale, ScaleTransform.ScaleXProperty, 1, 380, Motion.Spring, from: 0.55);
                Motion.Animate(_scale, ScaleTransform.ScaleYProperty, 1, 380, Motion.Spring, from: 0.55);
                Motion.Animate(_host, OpacityProperty, 1, 170, Motion.EaseOut, from: 0);
                fresh.PlayOpen(bloom: true);
            }
            else
            {
                _scale.ScaleX = _scale.ScaleY = 1;
                _host.Opacity = 1;
            }
        }
        Log.Write($"wheel revealed: {model.Name}, {model.Items.Count} items");
        return true;
    }

    // ---- OLE drop target ----

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            if (_phase == Phase.Hidden) return;
            _shell.Enter(Handle, e.Data, (int)DragDropEffects.None);
            _helperIn = true;
            if (_phase == Phase.Probing)
            {
                string[]? paths = null;
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths is not { Length: > 0 })
                {
                    Log.Write("DragEnter: no files");
                    HideNow();
                    return;
                }
                Log.Write($"DragEnter: {paths.Length} file(s)");
                _files = paths;
                _shell.Show(false);
                var ok = Rebuild(RebuildMode.Open);
                _shell.Show(true);
                if (!ok) { HideNow(); return; }
            }
            ApplyOver(e);
            _shell.Over((int)e.Effects);
            Describe(e);
        }
        catch (Exception ex) { Log.Error("DragEnter", ex); }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            if (_phase != Phase.Revealed) return;
            ApplyOver(e);
            _shell.Over((int)e.Effects);
            Describe(e);
        }
        catch (Exception ex) { Log.Error("DragOver", ex); }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        EndHelper();
    }

    private int? WedgeUnder(DragEventArgs e)
    {
        var p = e.GetPosition(_root);
        return _geometry.WedgeAt(p.X, p.Y, Side / 2, Side / 2);
    }

    private static DragDropEffects Answer(DragEventArgs e) =>
        e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        : DragDropEffects.None; // never Move: the source would delete the originals

    private void ApplyOver(DragEventArgs e)
    {
        var index = WedgeUnder(e);
        _visual?.SetHover(index);
        e.Effects = index is null ? DragDropEffects.None : Answer(e);
    }

    /// <summary>Tells the shell what a drop here would do; shown next to the drag image.</summary>
    private void Describe(DragEventArgs e)
    {
        if (_model is null) return;
        if (WedgeUnder(e) is { } i && i < _model.Items.Count)
        {
            var message = _model.Name == "Convert" ? "Convert to %1" : "%1";
            _shell.Describe(e.Data, ShellDragHelper.ImageCopy, message, _model.Items[i].Title);
        }
        else _shell.Describe(e.Data, ShellDragHelper.ImageNone, "", "");
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        var flashed = -1;
        var visual = _visual;
        try
        {
            if (_phase != Phase.Revealed || _model is null) { HideNow(); return; }
            var index = WedgeUnder(e);
            if (index is { } i && i < _model.Items.Count)
            {
                flashed = i;
                var item = _model.Items[i];
                var files = _files;
                var at = Native.CursorPos();
                e.Effects = Answer(e);
                _helperIn = false;
                _shell.Drop(e.Data, (int)e.Effects);
                Log.Write($"wedge dropped: {item.Title} ({files.Count} file(s))");
                // The drag source is still inside its modal loop; run after this handler returns.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                {
                    try
                    {
                        if (Dropped is { } run) await run(item, files, at);
                    }
                    catch (Exception ex) { Log.Error($"action {item.Title}", ex); }
                }));
            }
            else
            {
                Log.Write("drop outside any wedge");
            }
        }
        catch (Exception ex) { Log.Error("Drop", ex); }
        finally
        {
            if (flashed >= 0) visual?.Flash(flashed);
            if (_phase != Phase.Hidden) HideNow(animate: true, delayMilliseconds: flashed >= 0 ? 90 : 0);
        }
    }

    // ---- Hub text ----

    private static string HubDefault(IReadOnlyList<string> files)
    {
        long total = 0;
        var any = false;
        foreach (var f in files)
        {
            try { if (File.Exists(f)) { total += new FileInfo(f).Length; any = true; } } catch { }
        }
        var size = any ? FormatSize(total) : "Folder";
        return files.Count > 1 ? $"{files.Count} · {size}" : size;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1000) return $"{bytes} B";
        double v = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var u = -1;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.0} {units[u]}";
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

/// <summary>
/// The one reusable wheel window. It first appears as an invisible "probe" (1/255 opaque so it
/// hit-tests everywhere); when an OLE drag with files enters it, the wheel is revealed.
/// </summary>
internal sealed class WheelWindow : Window
{
    private enum Phase { Hidden, Probing, Revealed }

    private static readonly double Side = WheelGeometry.PanelSide;
    private static readonly Brush HitBrush = MakeHitBrush();

    private readonly Grid _root;
    private readonly Canvas _host;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly DispatcherTimer _probeTimer;
    private readonly DispatcherTimer _graceTimer;

    private Phase _phase = Phase.Hidden;
    private WheelKind _kind;
    private IReadOnlyList<string> _files = [];
    private WheelModel? _model;
    private WheelVisual? _visual;
    private WheelGeometry _geometry;

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
        _graceTimer.Tick += (_, _) => { _graceTimer.Stop(); HideNow(); };

        SourceInitialized += (_, _) => ApplyStyles();
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, e) => e.Handled = true; // never closes the wheel
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

    /// <summary>A trigger chord was chosen during a possible drag.</summary>
    public void ShowProbe(WheelKind kind, ScreenPoint pointer)
    {
        _graceTimer.Stop();
        _kind = kind;
        switch (_phase)
        {
            case Phase.Revealed:
                if (!Rebuild(animate: false)) HideNow(); // swap contents in place
                return;
            case Phase.Probing:
                return;
        }

        _phase = Phase.Probing;
        _files = [];
        _model = null;
        _visual = null;
        _host.BeginAnimation(OpacityProperty, null);
        _host.Opacity = 0;
        _host.Children.Clear();
        Place(pointer);
        _probeTimer.Stop();
        _probeTimer.Start();
        Log.Write($"probe shown ({kind}) at {pointer.X},{pointer.Y}");
    }

    /// <summary>The left button was released: hide shortly, leaving room for a pending Drop to arrive first.</summary>
    public void DragEnded()
    {
        if (_phase != Phase.Hidden) { _graceTimer.Stop(); _graceTimer.Start(); }
    }

    public void HideNow()
    {
        _probeTimer.Stop();
        _graceTimer.Stop();
        _phase = Phase.Hidden;
        _model = null;
        _visual = null;
        try
        {
            _host.BeginAnimation(OpacityProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _host.Opacity = 0;
            if (IsVisible) Hide();
        }
        catch (Exception e) { Log.Error("HideNow", e); }
    }

    /// <summary>Centres the window on the pointer using physical pixels and the DPI of the monitor under it.</summary>
    private void Place(ScreenPoint pointer)
    {
        var scale = Native.ScaleAt(pointer);
        var side = (int)Math.Round(Side * scale);   // e.g. 416 DIPs at 150% = 624 px
        var x = pointer.X - side / 2;
        var y = pointer.Y - side / 2;
        var h = Handle;
        ApplyStyles();
        const uint flags = Native.SWP_NOACTIVATE;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, side, side, flags);
        if (!IsVisible) Show();
        // A move onto a monitor with another DPI makes WPF re-apply a suggested rect; set it again afterwards.
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, side, side, flags | Native.SWP_SHOWWINDOW);
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, side, side, flags | Native.SWP_SHOWWINDOW);
    }

    private bool Rebuild(bool animate)
    {
        var model = BuildModel?.Invoke(_kind, _files);
        if (model is null || model.Items.Count == 0)
        {
            Log.Write($"nothing to offer for {_kind}");
            Unavailable?.Invoke(_kind);
            return false;
        }
        _model = model;
        _geometry = new WheelGeometry(model.Items.Count);
        _visual = new WheelVisual(model, HubDefault(_files));
        _host.Children.Clear();
        _host.Children.Add(_visual.Root);
        _phase = Phase.Revealed;
        _probeTimer.Stop();

        if (animate && SystemParameters.ClientAreaAnimation)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(180);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, d) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, d) { EasingFunction = ease });
            _host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        }
        else
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = 1;
            _host.BeginAnimation(OpacityProperty, null);
            _host.Opacity = 1;
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
                if (!Rebuild(animate: true)) { HideNow(); return; }
            }
            ApplyOver(e);
        }
        catch (Exception ex) { Log.Error("DragEnter", ex); }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try { if (_phase == Phase.Revealed) ApplyOver(e); }
        catch (Exception ex) { Log.Error("DragOver", ex); }
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

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            if (_phase != Phase.Revealed || _model is null) { HideNow(); return; }
            var index = WedgeUnder(e);
            if (index is { } i && i < _model.Items.Count)
            {
                var item = _model.Items[i];
                var files = _files;
                var at = Native.CursorPos();
                e.Effects = Answer(e);
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
        finally { HideNow(); }
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

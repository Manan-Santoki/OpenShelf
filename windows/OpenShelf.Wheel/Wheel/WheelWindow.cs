using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenShelf.Core;
using OpenShelf.Ui;

namespace OpenShelf.Wheel;

/// <summary>
/// The one reusable wheel window. It first appears as an invisible "probe" (1/255 opaque so it hit-tests everywhere);
/// when an OLE drag with files enters it, the wheel is revealed. It can also open without a drag as a keyboard wheel.
/// </summary>
internal sealed partial class WheelWindow : Window
{
    private enum Phase { Hidden, Probing, Revealed, Absorbing, Keyboard }

    private static readonly double Side = WheelGeometry.PanelSide;
    private static readonly Brush HitBrush = MakeHitBrush();

    private readonly Grid _root;
    private readonly Canvas _host;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly DispatcherTimer _probeTimer;
    private readonly DispatcherTimer _graceTimer;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _hintTimer;
    private readonly ShellDragHelper _shell = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Phase _phase = Phase.Hidden;
    private WheelKind _kind;
    private IReadOnlyList<string> _files = [];
    private string _hub = "";
    private WheelModel? _model;
    private WheelVisual? _visual;
    private IReadOnlyList<WheelSlot> _slots = [];
    private int _page;
    private WheelGeometry _geometry;
    private BitmapSource? _backdrop;
    private int _generation;
    private bool _helperIn;
    private bool _warm;
    private DispatcherTimer? _warmTimer;

    private int? _current;        // the highlighted wedge (pointer or keys)
    private int? _pointerSlot;    // the wedge under the pointer, if it can be chosen
    private int? _lastWedge;
    private readonly PageTurnDwell _dwell = new();

    private ScreenPoint _centrePx;
    private int _sidePx;
    private double _scalePx = 1;

    /// <summary>Builds the wheel for a kind and the files; an empty model means nothing to offer.</summary>
    public Func<WheelKind, IReadOnlyList<string>, WheelModel>? BuildModel { get; set; }
    /// <summary>Called when a wheel has nothing to offer for the files (so the service can toast).</summary>
    public Action<WheelModel, ScreenPoint>? Unavailable { get; set; }
    /// <summary>A wedge was chosen: the model, entry, files, pointer, Alt held, keyboard wheel, the source shelf token.</summary>
    public Action<WheelModel, WheelEntry, IReadOnlyList<string>, ScreenPoint, bool, bool, object?>? Chosen { get; set; }
    /// <summary>A wheel opened or was switched to (for the tour).</summary>
    public Action<WheelModel, IReadOnlyList<string>, bool>? Opened { get; set; }
    /// <summary>The shelf a drag is coming out of right now.</summary>
    public Func<object?>? DragSource { get; set; }
    /// <summary>The current trigger keys, for tapping them to switch wheels.</summary>
    public Func<TriggerMatcher>? TriggerSource { get; set; }

    public bool IsOpen => _phase != Phase.Hidden;
    public bool IsIdle => _phase == Phase.Hidden;
    public bool IsKeyboardMode => _phase == Phase.Keyboard;

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
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer.Stop();
            if (_phase != Phase.Keyboard) HideNow(animate: true);
        };
        _tick = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        _tick.Tick += (_, _) => OnTick();
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); _visual?.SetNameText(_model?.Name ?? ""); };

        SourceInitialized += (_, _) => ApplyStyles();
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave; // never closes the wheel
        Drop += OnDrop;
        Activated += (_, _) => _wasActivated = true;
        Deactivated += (_, _) =>
        {
            if (_phase == Phase.Keyboard && _wasActivated) { Log.Write("keyboard wheel lost activation"); HideNow(animate: true); }
        };
        MouseMove += OnMouseMoveOverWheel;
        MouseLeave += (_, _) => { if (_phase == Phase.Keyboard) { _pointerSlot = null; _lastWedge = null; _dwell.Update(false, _clock.Elapsed); } };
        MouseLeftButtonUp += OnClickWheel;
        InitKeyboard();
    }

    private static Brush MakeHitBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        b.Freeze();
        return b;
    }

    private nint Handle => new WindowInteropHelper(this).EnsureHandle();

    private bool _activatable, _wasActivated, _badgesOn;
    private DispatcherTimer? _badgeTimer;

    private void ApplyStyles()
    {
        try
        {
            var h = Handle;
            var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            var next = ex | (nint)Native.WS_EX_TOOLWINDOW;
            next = _activatable ? next & ~(nint)Native.WS_EX_NOACTIVATE : next | (nint)Native.WS_EX_NOACTIVATE;
            if (next != ex) Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, next);
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
            Task Noop(WheelRun _) => Task.CompletedTask;
            var model = new WheelModel(WheelKind.Actions,
            [
                new WheelEntry("w.1", "Shelf", Noop, ""), new WheelEntry("w.2", "Copy", Noop, ""),
                new WheelEntry("w.3", "Copy path", Noop, ""), new WheelEntry("w.4", "Terminal", Noop, ""),
            ]);
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var visual = new WheelVisual(model, WheelPaging.Slots(4, 0), 0, "0 B", Glass.Capture(0, 0, 64, 64), dpi, animateIn: false);
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

    /// <summary>Clears the previous wheel, photographs the screen under the new one and puts the window at the pointer.</summary>
    private void Prepare(ScreenPoint pointer, bool room)
    {
        _graceTimer.Stop();
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
        var centre = KeepOnScreen(pointer, scale, room);
        _backdrop = CaptureBackdrop(centre, scale);

        _files = [];
        _model = null;
        _visual = null;
        _slots = [];
        _page = 0;
        _current = _pointerSlot = _lastWedge = null;
        _dwell.Update(false, _clock.Elapsed);
        _host.BeginAnimation(OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        _host.Opacity = 0;
        _host.Children.Clear();
        _scalePx = scale;
        Place(centre, pointer, scale);
    }

    /// <summary>A trigger chord (or a shake) was chosen during a possible drag.</summary>
    public void ShowProbe(WheelKind kind, ScreenPoint pointer)
    {
        _graceTimer.Stop();
        switch (_phase)
        {
            case Phase.Revealed:
                SwitchKind(kind);   // swap contents in place
                return;
            case Phase.Probing or Phase.Absorbing or Phase.Keyboard:
                if (_phase == Phase.Probing) _kind = kind;
                return;
        }

        _kind = kind;
        Prepare(pointer, room: false);
        _phase = Phase.Probing;
        _probeTimer.Stop();
        _probeTimer.Start();
        Log.Write($"probe shown ({kind}) at {pointer.X},{pointer.Y}, centre {_centrePx.X},{_centrePx.Y}, backdrop {(_backdrop is null ? "none" : "ok")}");
    }

    /// <summary>Switches a mid-drag wheel to another kind, keeping the old one when the new has nothing to offer.</summary>
    private void SwitchKind(WheelKind kind)
    {
        if (kind == _kind && _model is not null) return;
        var model = BuildModel!(kind, _files);
        if (model.Items.Count == 0) { Unavailable?.Invoke(model, Native.CursorPos()); return; }
        _kind = kind;
        Reveal(model, resetPage: true, announce: true);
        SetCurrent(_pointerSlot);
    }

    /// <summary>The left button was released: hide shortly, leaving room for a pending Drop to arrive first.</summary>
    public void DragEnded()
    {
        if (_phase is Phase.Hidden or Phase.Keyboard) return;
        _graceTimer.Stop();
        _graceTimer.Start();
    }

    /// <summary>A new drag began: nothing from before may stay open.</summary>
    public void DragBegan()
    {
        if (_phase != Phase.Hidden) HideNow();
    }

    /// <summary>Hides at once, or (animated) shrinks and fades out first.</summary>
    public void HideNow(bool animate = false, int delayMilliseconds = 0)
    {
        _probeTimer.Stop();
        _graceTimer.Stop();
        _hintTimer.Stop();
        var wasRevealed = _phase is Phase.Revealed or Phase.Keyboard;
        _phase = Phase.Hidden;
        StopKeyboard();
        _tick.Stop();
        _badgeTimer?.Stop();
        _badgesOn = false;
        _dwell.Update(false, _clock.Elapsed);
        _model = null;
        _visual = null;
        _current = _pointerSlot = _lastWedge = null;
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
        _activatable = false;
        ApplyStyles();
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

    /// <summary>Moves the wheel centre so the whole disc and the name pill (and a hint above it) stay inside the monitor work area.</summary>
    private static ScreenPoint KeepOnScreen(ScreenPoint pointer, double scale, bool room)
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
        _sidePx = side;
        _centrePx = centre;
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

    // ---- Showing a wheel ----

    private void SetSlots()
    {
        _slots = WheelPaging.Slots(_model!.Items.Count, _page);
        _geometry = new WheelGeometry(Math.Max(1, _slots.Count));
    }

    /// <summary>
    /// Shows <paramref name="model"/> page <c>_page</c>. First time it blooms from the pointer; when something is already showing
    /// (a switch of wheel or a page turn) the new wedges bloom in underneath while the old ones dissolve.
    /// </summary>
    private bool Reveal(WheelModel model, bool resetPage, bool announce)
    {
        if (model.Items.Count == 0) return false;
        var old = _visual;
        _model = model;
        if (resetPage) _page = 0;
        SetSlots();
        var animated = Motion.Enabled;
        var fresh = new WheelVisual(model, _slots, _page, _hub, _backdrop, VisualTreeHelper.GetDpi(this).DpiScaleX, animateIn: animated);
        _visual = fresh;
        if (_badgesOn) fresh.SetBadges(true, animate: false);
        _probeTimer.Stop();

        if (old is not null)
        {
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
        _hintTimer.Stop();
        if (_phase == Phase.Probing) _phase = Phase.Revealed;
        _tick.Start();
        if (_phase == Phase.Revealed && !_hook.IsInstalled) StartKeyboard();
        Log.Write($"wheel revealed: {model.Name}, {model.Items.Count} items, page {_page + 1} of {WheelPaging.PageCount(model.Items.Count)}");
        if (announce) Opened?.Invoke(model, _files, _phase == Phase.Keyboard);
        return true;
    }

    private void SetCurrent(int? slot)
    {
        if (slot is { } s && (s < 0 || s >= _slots.Count || _slots[s].IsEmpty)) slot = null;
        _current = slot;
        _visual?.SetHover(slot);
    }

    private void ShowHint(string text)
    {
        if (_visual is null) return;
        _visual.SetNameText(text);
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    /// <summary>Turns to the page More or Back at <paramref name="slot"/> leads to; the highlight stays on that wedge.</summary>
    private void TurnPage(int slot)
    {
        if (_model is null || slot < 0 || slot >= _slots.Count || !_slots[slot].IsPageTurn) return;
        var page = _slots[slot].TargetPage;
        // More and Back share a wedge, so a pointer resting there is now on the other one; it has to leave and come back.
        _dwell.PageDidTurn(_pointerSlot == slot, _clock.Elapsed);
        _page = page;
        Reveal(_model, resetPage: false, announce: false);
        SetCurrent(slot);
        Log.Write($"page turned to {page + 1}");
    }

    private void CheckDwell()
    {
        var onTurn = _pointerSlot is { } p && p < _slots.Count && _slots[p].IsPageTurn;
        if (_dwell.Update(onTurn, _clock.Elapsed) && _pointerSlot is { } slot) TurnPage(slot);
    }

    private void OnTick()
    {
        try
        {
            switch (_phase)
            {
                case Phase.Revealed:
                    CheckDwell();
                    break;
                case Phase.Keyboard:
                    CheckDwell();
                    PollOutsideClick();
                    break;
                case Phase.Absorbing:
                    FollowPointer();
                    break;
            }
        }
        catch (Exception e) { Log.Error("Tick", e); }
    }

    /// <summary>After Return, the invisible window keeps to the pointer so a drop at mouse-up can't land beneath it.</summary>
    private void FollowPointer()
    {
        var p = Native.CursorPos();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, p.X - _sidePx / 2, p.Y - _sidePx / 2, _sidePx, _sidePx, Native.SWP_NOACTIVATE);
    }

    // ---- Running a wedge ----

    /// <summary>Runs the highlighted wedge (Return). On More or Back it turns the page instead.</summary>
    private void RunHighlighted(bool options)
    {
        if (_model is null || _phase is not (Phase.Revealed or Phase.Keyboard)) return;
        if (_current is not { } slot || slot >= _slots.Count || _slots[slot].IsEmpty)
        {
            ShowHint("Choose a wedge with the arrow keys");
            return;
        }
        if (_slots[slot].IsPageTurn) { TurnPage(slot); return; }
        RunSlot(slot, options);
    }

    private void RunSlot(int slot, bool options)
    {
        if (_model is null || _slots[slot].Kind != SlotKind.Item) return;
        var model = _model;
        var entry = model.Items[_slots[slot].Item];
        var files = _files;
        var at = Native.CursorPos();
        var keyboard = _phase == Phase.Keyboard;
        var source = keyboard ? _sessionSource : DragSource?.Invoke();
        Log.Write($"wedge run by keyboard/click: {entry.Title} ({files.Count} file(s), options {options})");
        _visual?.Flash(slot);
        if (keyboard) HideNow(animate: true, delayMilliseconds: 90);
        else EnterAbsorbing();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { Chosen?.Invoke(model, entry, files, at, options, keyboard, source); }
            catch (Exception ex) { Log.Error($"action {entry.Title}", ex); }
        }));
    }

    /// <summary>The wheel disappears but the window stays under the pointer until the drag ends, answering Copy and doing nothing on Drop.</summary>
    private void EnterAbsorbing()
    {
        _phase = Phase.Absorbing;
        _graceTimer.Stop();
        _hintTimer.Stop();
        StopKeyboard();
        _dwell.Update(false, _clock.Elapsed);
        _visual = null;
        _current = _pointerSlot = null;
        var generation = ++_generation;
        if (Motion.Enabled)
        {
            Motion.Animate(_scale, ScaleTransform.ScaleXProperty, 0.85, 140, Motion.EaseOut, 90);
            Motion.Animate(_scale, ScaleTransform.ScaleYProperty, 0.85, 140, Motion.EaseOut, 90);
            Motion.Animate(_host, OpacityProperty, 0, 140, Motion.EaseOut, 90,
                completed: () => { if (generation == _generation && _phase == Phase.Absorbing) _host.Children.Clear(); });
        }
        else { _host.Opacity = 0; _host.Children.Clear(); }
        _tick.Start();
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

using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

// Keys while a wheel is open (mid-drag or the keyboard wheel), via a low-level hook installed only while open.
internal sealed partial class WheelWindow
{
    private readonly KeyboardHook _hook = new();
    private readonly Dictionary<int, KeyRepeat> _repeats = [];
    private readonly HashSet<int> _swallowed = [];
    private readonly HashSet<int> _modifierKeys = [];

    private Dictionary<WheelKind, WheelModel>? _models;
    private List<WheelKind> _available = [];
    private ModifierTapTracker? _taps;
    private object? _sessionSource;
    private bool _downLast;

    private void InitKeyboard() => _hook.Handler = OnKey;

    /// <summary>Badges fade in on a keyboard wheel at the first key press or after 1.2 s, and stay for that wheel.</summary>
    private void ShowBadges()
    {
        if (_badgesOn || _phase != Phase.Keyboard) return;
        _badgesOn = true;
        _badgeTimer?.Stop();
        _visual?.SetBadges(true, animate: true);
    }

    private void ArmBadgeTimer()
    {
        _badgesOn = false;
        _badgeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _badgeTimer.Tick -= BadgeTimerTick;
        _badgeTimer.Tick += BadgeTimerTick;
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    private void BadgeTimerTick(object? sender, EventArgs e) { _badgeTimer?.Stop(); ShowBadges(); }

    private void StartKeyboard()
    {
        _swallowed.Clear();
        _repeats.Clear();
        _modifierKeys.Clear();
        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            if (Native.IsDown(vk)) _modifierKeys.Add(vk);
        _hook.Install();
    }

    private void StopKeyboard()
    {
        _hook.Uninstall();
        _swallowed.Clear();
        _repeats.Clear();
        _models = null;
        _available = [];
        _taps = null;
        _sessionSource = null;
        _wasActivated = false;
    }

    private static Modifiers ModifiersOf(HashSet<int> keys)
    {
        var m = Modifiers.None;
        if (keys.Overlaps([0x10, 0xA0, 0xA1])) m |= Modifiers.Shift;
        if (keys.Overlaps([0x11, 0xA2, 0xA3])) m |= Modifiers.Control;
        if (keys.Overlaps([0x12, 0xA4, 0xA5])) m |= Modifiers.Alt;
        return m;
    }

    private static bool IsModifier(int vk) => vk is >= 0x10 and <= 0x12 or >= 0xA0 and <= 0xA5;

    /// <summary>The hook callback: true swallows the key so it does not reach the app underneath.</summary>
    private bool OnKey(int vk, bool down)
    {
        if (_phase is not (Phase.Revealed or Phase.Keyboard)) return false;
        var keyboard = _phase == Phase.Keyboard;
        if (keyboard && down && !IsModifier(vk)) Dispatcher.BeginInvoke(new Action(ShowBadges));

        if (IsModifier(vk))
        {
            if (down) _modifierKeys.Add(vk); else _modifierKeys.Remove(vk);
            if (keyboard && _taps is { } taps && taps.FlagsChanged(ModifiersOf(_modifierKeys)) is { } tapped)
            {
                if ((TriggerSource?.Invoke() ?? TriggerMatcher.Default).WheelFor(tapped) is { } kind && _available.Contains(kind) && kind != _kind)
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => SwitchWheel(kind)));
            }
            return false;
        }

        if (!down)
        {
            if (!_swallowed.Remove(vk)) return false;
            if (_repeats.TryGetValue(vk, out var repeat)) repeat.Update(false, _clock.Elapsed);
            return true;
        }

        var shift = (ModifiersOf(_modifierKeys) & Modifiers.Shift) != 0;
        var alt = (ModifiersOf(_modifierKeys) & Modifiers.Alt) != 0;
        var command = WheelKeys.Classify(vk, shift, keyboard, out var digit);
        if (command == WheelKeyCommand.None)
        {
            _taps?.KeyPressed();
            return false;
        }

        var first = _swallowed.Add(vk);
        _taps?.KeyPressed();
        switch (command)
        {
            case WheelKeyCommand.Clockwise or WheelKeyCommand.CounterClockwise:
                // The OS repeats a held key; KeyRepeat gives it the Mac's pace.
                if (!_repeats.TryGetValue(vk, out var r)) _repeats[vk] = r = new KeyRepeat();
                if (r.Update(true, _clock.Elapsed))
                {
                    var direction = command == WheelKeyCommand.Clockwise ? StepDirection.Clockwise : StepDirection.CounterClockwise;
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Step(direction)));
                }
                break;
            case WheelKeyCommand.Run when first:
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => RunHighlighted(alt)));
                break;
            case WheelKeyCommand.Digit when first:
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => RunDigit(digit, alt)));
                break;
            case WheelKeyCommand.NextWheel or WheelKeyCommand.PreviousWheel when first:
                var backwards = command == WheelKeyCommand.PreviousWheel;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (WheelCycle.Next(_kind, _available, backwards) is { } next) SwitchWheel(next);
                }));
                break;
            case WheelKeyCommand.Close when first:
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => HideNow(animate: true)));
                break;
        }
        return true;
    }

    private void Step(StepDirection direction)
    {
        if (_phase is not (Phase.Revealed or Phase.Keyboard)) return;
        if (WheelKeyboardNavigator.Step(_current, _slots, direction) is { } next) SetCurrent(next);
    }

    private void RunDigit(int digit, bool options)
    {
        if (_phase is not (Phase.Revealed or Phase.Keyboard)) return;
        if (WheelBadges.SlotForKey(digit.ToString(), _slots) is not { } slot) return;
        SetCurrent(slot);
        if (_slots[slot].IsPageTurn) TurnPage(slot);
        else RunSlot(slot, options);
    }

    // ---- The keyboard wheel (no drag) ----

    /// <summary>Opens the wheel at the pointer for these files. False when no wheel has anything to offer.</summary>
    public bool OpenKeyboard(IReadOnlyList<string> files, ScreenPoint pointer, object? source)
    {
        var models = new Dictionary<WheelKind, WheelModel>();
        foreach (var kind in WheelCycle.Order) models[kind] = BuildModel!(kind, files);
        var available = WheelCycle.Available(k => models[k].Items.Count > 0);
        if (available.Count == 0)
        {
            Unavailable?.Invoke(models[WheelKind.Convert], pointer);
            return false;
        }

        if (_phase != Phase.Hidden) HideNow();
        _activatable = true;
        Prepare(pointer, room: true);
        _files = files;
        _hub = HubDefault(files);
        _models = models;
        _available = available;
        _kind = available[0];
        _sessionSource = source;
        _taps = new ModifierTapTracker(Native.HeldModifiers());
        _phase = Phase.Keyboard;
        _wasActivated = false;
        _downLast = Native.IsDown(Native.VK_LBUTTON);
        StartKeyboard();
        _badgesOn = false;
        Reveal(models[_kind], resetPage: true, announce: true);
        ArmBadgeTimer();
        SetCurrent(WheelKeyboardNavigator.Step(null, _slots, StepDirection.Clockwise));
        ShowHint(KeyboardWheelHint.Opening(available.Count > 1, models[_kind].HasOptions));
        Native.ForceForeground(Handle);
        Log.Write($"keyboard wheel opened: {files.Count} file(s), wheels {string.Join(",", available)}");
        return true;
    }

    /// <summary>After files were dropped on More or Back, the wheel stays open for a click or the keys to finish with.</summary>
    private void HoldOpenAfterDrop()
    {
        var files = _files;
        var models = new Dictionary<WheelKind, WheelModel>();
        foreach (var kind in WheelCycle.Order) models[kind] = kind == _kind && _model is not null ? _model : BuildModel!(kind, files);
        _models = models;
        _available = WheelCycle.Available(k => models[k].Items.Count > 0);
        _sessionSource = DragSource?.Invoke();
        _taps = new ModifierTapTracker(Native.HeldModifiers());
        _graceTimer.Stop();
        _phase = Phase.Keyboard;
        _wasActivated = false;
        _downLast = true;
        _activatable = true;
        ApplyStyles();
        StartKeyboard();
        ArmBadgeTimer();
        Native.ForceForeground(Handle);
        ShowHint("Click to run · Esc closes");
    }

    private void SwitchWheel(WheelKind kind)
    {
        if (_phase != Phase.Keyboard || _models is null || !_models.TryGetValue(kind, out var model) || model.Items.Count == 0) return;
        _kind = kind;
        Reveal(model, resetPage: true, announce: true);
        SetCurrent(WheelKeyboardNavigator.Step(null, _slots, StepDirection.Clockwise));
        if (model.HasOptions) ShowHint(KeyboardWheelHint.Options);
    }

    /// <summary>Clicking anywhere outside the wheel closes it, even when activation never reached us.</summary>
    private void PollOutsideClick()
    {
        var down = Native.IsDown(Native.VK_LBUTTON);
        var pressed = down && !_downLast;
        _downLast = down;
        if (!pressed) return;
        var p = Native.CursorPos();
        var dx = p.X - _centrePx.X;
        var dy = p.Y - _centrePx.Y;
        var radius = (WheelVisual.DiscRadius + 4) * _scalePx;
        if (dx * dx + dy * dy > radius * radius)
        {
            Log.Write("click outside the keyboard wheel");
            HideNow(animate: true);
        }
    }
}

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenShelf.Core;

namespace OpenShelf.Wheel;

// OLE drop target (mid-drag wheels) and mouse handling (keyboard wheel).
internal sealed partial class WheelWindow
{
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            if (_phase == Phase.Hidden || _phase == Phase.Keyboard) return;
            _shell.Enter(Handle, e.Data, (int)DragDropEffects.None);
            _helperIn = true;
            if (_phase == Phase.Absorbing)
            {
                e.Effects = Answer(e);
                _shell.Over((int)e.Effects);
                return;
            }
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
                _hub = HubDefault(paths);
                _shell.Show(false);
                var model = BuildModel!(_kind, paths);
                _phase = Phase.Revealed;
                var ok = Reveal(model, resetPage: true, announce: true);
                _shell.Show(true);
                if (!ok)
                {
                    Log.Write($"nothing to offer for {_kind}");
                    Unavailable?.Invoke(model, Native.CursorPos());
                    HideNow();
                    return;
                }
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
            if (_phase == Phase.Absorbing) { e.Effects = Answer(e); _shell.Over((int)e.Effects); return; }
            if (_phase != Phase.Revealed) return;
            ApplyOver(e);
            CheckDwell();
            _shell.Over((int)e.Effects);
            Describe(e);
        }
        catch (Exception ex) { Log.Error("DragOver", ex); }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_phase == Phase.Revealed) { _pointerSlot = null; _lastWedge = null; }
        EndHelper();
    }

    private int? WedgeUnder(Point p) => _geometry.WedgeAt(p.X, p.Y, Side / 2, Side / 2) is { } i && i < _slots.Count ? i : null;

    private int? WedgeUnder(DragEventArgs e) => WedgeUnder(e.GetPosition(_root));

    private static DragDropEffects Answer(DragEventArgs e) =>
        e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        : DragDropEffects.None; // never Move: the source would delete the originals

    private void ApplyOver(DragEventArgs e)
    {
        var index = WedgeUnder(e);
        if (index is { } i && _slots[i].IsEmpty) index = null;
        _pointerSlot = index;
        // Only a change of wedge moves the highlight, so a keyboard choice stays until the pointer actually moves on.
        if (index != _lastWedge)
        {
            _lastWedge = index;
            SetCurrent(index);
        }
        e.Effects = index is null ? DragDropEffects.None : Answer(e);
    }

    /// <summary>Tells the shell what a drop here would do; shown next to the drag image.</summary>
    private void Describe(DragEventArgs e)
    {
        if (_model is null) return;
        if (_pointerSlot is { } i && i < _slots.Count)
        {
            var slot = _slots[i];
            if (slot.Kind == SlotKind.Item)
                _shell.Describe(e.Data, ShellDragHelper.ImageCopy, _model.Kind == WheelKind.Convert ? "Convert to %1" : "%1", _model.Items[slot.Item].Title);
            else
                _shell.Describe(e.Data, ShellDragHelper.ImageCopy, "%1", slot.PageLabel ?? "");
        }
        else _shell.Describe(e.Data, ShellDragHelper.ImageNone, "", "");
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        var flashed = -1;
        var visual = _visual;
        var keepOpen = false;
        try
        {
            if (_phase == Phase.Absorbing)
            {
                e.Effects = Answer(e);
                _helperIn = false;
                _shell.Drop(e.Data, (int)e.Effects);
                Log.Write("drop absorbed");
                return;
            }
            if (_phase != Phase.Revealed || _model is null) { HideNow(); return; }
            var index = WedgeUnder(e);
            if (index is { } i && !_slots[i].IsEmpty)
            {
                var slot = _slots[i];
                e.Effects = Answer(e);
                _helperIn = false;
                _shell.Drop(e.Data, (int)e.Effects);
                if (slot.IsPageTurn)
                {
                    // Turn the page and keep the wheel open for a click or the keys to finish with.
                    Log.Write("dropped on a page turn");
                    TurnPage(i);
                    HoldOpenAfterDrop();
                    keepOpen = true;
                    return;
                }
                flashed = i;
                var model = _model;
                var entry = model.Items[slot.Item];
                var files = _files;
                var at = Native.CursorPos();
                var options = Native.IsDown(Native.VK_MENU);
                var source = DragSource?.Invoke();
                Log.Write($"wedge dropped: {entry.Title} ({files.Count} file(s), options {options})");
                // The drag source is still inside its modal loop; run after this handler returns.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try { Chosen?.Invoke(model, entry, files, at, options, false, source); }
                    catch (Exception ex) { Log.Error($"action {entry.Title}", ex); }
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
            if (_phase == Phase.Absorbing) HideNow();
            else if (!keepOpen)
            {
                if (flashed >= 0) visual?.Flash(flashed);
                if (_phase != Phase.Hidden) HideNow(animate: true, delayMilliseconds: flashed >= 0 ? 90 : 0);
            }
        }
    }

    // ---- Mouse (keyboard wheel) ----

    private void OnMouseMoveOverWheel(object sender, MouseEventArgs e)
    {
        if (_phase != Phase.Keyboard) return;
        var index = WedgeUnder(e.GetPosition(_root));
        if (index is { } i && _slots[i].IsEmpty) index = null;
        _pointerSlot = index;
        if (index != _lastWedge)
        {
            _lastWedge = index;
            if (index is not null) SetCurrent(index);
        }
    }

    private void OnClickWheel(object sender, MouseButtonEventArgs e)
    {
        if (_phase != Phase.Keyboard) return;
        var index = WedgeUnder(e.GetPosition(_root));
        if (index is not { } i || _slots[i].IsEmpty) return;
        e.Handled = true;
        SetCurrent(i);
        if (_slots[i].IsPageTurn) TurnPage(i);
        else RunSlot(i, Native.IsDown(Native.VK_MENU));
    }
}

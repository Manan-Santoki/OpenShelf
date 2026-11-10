using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Tools.Pdf;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>The "Pages" tool: a grid of page thumbnails to reorder by dragging, select, turn and delete, with undo and redo. Each PDF keeps its
/// own pages and history; several PDFs get a filmstrip (no Same for all). Saves "Name Edited.pdf"
/// (Sources/OpenShelf/Tools/PDF/PDFPagesEditor.swift).</summary>
public sealed class PdfPagesWindow : PdfEditorWindow
{
    public static Size ContentSizeFor(int count) => new(620, count > 1 ? 640 + FilmstripView.StripHeight + 12 : 640);

    private readonly PagesModel _model;
    private readonly PageSelection _selection = new();
    private readonly FileEdits<int> _edits;
    private readonly Dictionary<(int File, int Source), BitmapSource> _pictures = [];
    private readonly Dictionary<Guid, PageCell> _cells = [];
    private readonly HashSet<int> _wired = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WrapPanel _panel = new() { Margin = new Thickness(10) };
    private readonly Border _host = new() { Background = Brushes.Transparent };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _overlay = ToolKit.Text("", 12, "Text2", wrap: true);
    private readonly TextBlock _summary = ToolKit.Text("", 11, "Text3");
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _left;
    private readonly Button _right;
    private readonly Button _delete;
    private readonly StackPanel _toolbarButtons = new() { Orientation = Orientation.Horizontal };
    private readonly List<string> _pendingDemo = [];
    private FilmstripView? _filmstrip;
    private IReadOnlyList<PagesJob?> _snapshot = [];
    private int _version;
    private bool _busy;
    private bool _firstLoadDone;

    // dragging
    private (Guid? Id, Point At, ModifierKeys Keys)? _press;
    private HashSet<Guid>? _moving;
    private List<Guid>? _display;

    public PdfPagesWindow(EditorHost host, IReadOnlyList<PdfFileState> files) : base(host, "Pages", ContentSizeFor(files.Count))
    {
        _model = new PagesModel(files);
        _edits = new FileEdits<int>(files.Count, _ => 0, (i, _) => _model.Edited[i]);
        CanvasWell = false;
        SetCanvasPadding(0);

        _host.Child = _panel;
        _scroll.Content = _host;
        _host.MouseLeftButtonDown += OnDown;
        _host.MouseMove += OnMove;
        _host.MouseLeftButtonUp += OnUp;
        _host.LostMouseCapture += OnLostCapture;
        var well = new Border
        {
            Background = ToolKit.Brush("Well"),
            BorderBrush = ToolKit.Brush("Hairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = new Grid { Children = { _scroll, _overlay } },
        };
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        _overlay.VerticalAlignment = VerticalAlignment.Center;
        _overlay.TextAlignment = TextAlignment.Center;
        _overlay.MaxWidth = 380;
        _overlay.IsHitTestVisible = false;

        _undo = ToolKit.IconButton("", "Undo (Ctrl+Z)", (_, _) => Undo());
        _redo = ToolKit.IconButton("", "Redo (Ctrl+Shift+Z)", (_, _) => Redo());
        _left = ToolKit.IconButton("", "Rotate selected pages left", (_, _) => RotateSelection(-90));
        _left.LayoutTransform = new ScaleTransform(-1, 1);
        _left.RenderTransformOrigin = new Point(0.5, 0.5);
        _right = ToolKit.IconButton("", "Rotate selected pages right", (_, _) => RotateSelection(90));
        _delete = ToolKit.IconButton("", "Delete selected pages (Delete)", (_, _) => DeleteSelection());
        var gap = new Border { Width = 1, Height = 18, Background = ToolKit.Brush("Hairline"), Margin = new Thickness(6, 0, 6, 0) };
        foreach (var element in new UIElement[] { _undo, _redo, gap, _left, _right, _delete }) _toolbarButtons.Children.Add(element);

        var toolbar = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_toolbarButtons, 1);
        toolbar.Children.Add(_summary);
        toolbar.Children.Add(_toolbarButtons);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(well);
        layout.Children.Add(toolbar);
        Canvas = layout;

        if (_model.IsBatch)
        {
            _filmstrip = new FilmstripView(files.Select(_ => (ImageSource?)null).ToList(), files.Select(f => f.Name).ToList(),
                withSameForAll: false, itemName: "PDF", placeholderGlyph: Parts.PageGlyph);
            _filmstrip.Attach(_edits);
            Filmstrip = _filmstrip;
            _edits.Changed += () =>
            {
                if (_edits.Index != _model.Index) _ = ShowFileAsync(_edits.Index);
            };
        }

        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            _ = ShowFileAsync(0);
            if (_filmstrip is not null) Parts.LoadFirstPages(_filmstrip, files, _lifetime.Token);
        };
        Closed += (_, _) => _lifetime.Cancel();
        UpdateToolbar();
    }

    // ---------------------------------------------------------------- files and thumbnails

    private async Task ShowFileAsync(int index)
    {
        _model.Index = index;
        _selection.Clear();
        EndDrag(commit: false);
        _cells.Clear();
        Rebuild();
        await _model.LoadAsync(index, _lifetime.Token);
        if (_lifetime.IsCancellationRequested) return;
        if (_model.Document(index) is { } document && _wired.Add(index))
        {
            document.Changed += (_, _) => OnDocumentChanged(index);
            _ = RenderThumbnailsAsync(index);
        }
        if (_model.Index == index) Rebuild();
        Refresh();
        if (!_firstLoadDone && index == 0)
        {
            _firstLoadDone = true;
            foreach (var state in _pendingDemo.ToList()) ApplyDemo(state);
            _pendingDemo.Clear();
        }
    }

    private async Task RenderThumbnailsAsync(int file)
    {
        var renderer = _model.Renderer(file);
        if (renderer is null) return;
        for (var i = 0; i < renderer.SourcePageCount; i++)
        {
            try
            {
                var thumbnail = await renderer.RenderThumbnailAsync(i, 200, _lifetime.Token);
                var bitmap = Parts.ToBitmap(thumbnail);
                _pictures[(file, i)] = bitmap;
                if (_model.Index == file)
                    foreach (var cell in _cells.Values.Where(c => c.SourceIndex == i)) cell.SetPicture(bitmap);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* this page stays a blank placeholder */ }
        }
    }

    private void OnDocumentChanged(int file)
    {
        _version++;
        if (_model.IsBatch) _edits.SetOnly(file, _version);
        if (file != _model.Index) return;
        _display = null;
        _selection.Prune(_model.Current!.Pages);
        Rebuild();
        Refresh();
    }

    // ---------------------------------------------------------------- drawing the grid

    /// <summary>Lays the cells out in the shown order (the live preview while dragging, else the document's).</summary>
    private void Rebuild()
    {
        var document = _model.Current;
        _panel.Children.Clear();
        if (document is null)
        {
            _overlay.Text = _model.CurrentError ?? "Opening…";
            _overlay.Foreground = _model.CurrentError is null ? ToolKit.Brush("Text3") : ToolKit.Brush("Danger");
            UpdateToolbar();
            return;
        }
        _overlay.Text = document.Pages.Count == 0 ? "No pages left. Undo to bring them back." : "";
        _overlay.Foreground = ToolKit.Brush("Text3");

        var byId = document.Pages.ToDictionary(p => p.Id);
        var order = _display ?? document.Pages.Select(p => p.Id).ToList();
        for (var position = 0; position < order.Count; position++)
        {
            var page = byId[order[position]];
            if (!_cells.TryGetValue(page.Id, out var cell))
            {
                cell = new PageCell(page.Id, page.SourceIndex);
                if (_pictures.TryGetValue((_model.Index, page.SourceIndex), out var picture)) cell.SetPicture(picture);
                _cells[page.Id] = cell;
            }
            cell.Update(position + 1, page.Rotation, _selection.Contains(page.Id), _moving?.Contains(page.Id) == true);
            _panel.Children.Add(cell);
        }
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        var document = _model.Current;
        var any = !_selection.IsEmpty && !_busy;
        _left.IsEnabled = _right.IsEnabled = _delete.IsEnabled = any;
        _undo.IsEnabled = !_busy && document?.CanUndo == true;
        _redo.IsEnabled = !_busy && document?.CanRedo == true;
        _summary.Text = _model.Summary(_selection.Count);
        if (_model.CurrentError is not null || document is null) _summary.Text = "";
    }

    private PageCell? CellAt(Point inPanel)
    {
        var hit = _panel.InputHitTest(inPanel) as DependencyObject;
        while (hit is not null && hit is not PageCell) hit = VisualTreeHelper.GetParent(hit) ?? LogicalTreeHelper.GetParent(hit);
        return hit as PageCell;
    }

    // ---------------------------------------------------------------- editing

    private void RotateSelection(int degrees)
    {
        if (_model.Current is not { } document || _selection.IsEmpty) return;
        document.Rotate(_selection.Indices(document.Pages), degrees);
    }

    private void DeleteSelection()
    {
        if (_model.Current is not { } document || _selection.IsEmpty) return;
        var indices = _selection.Indices(document.Pages);
        _selection.Clear();
        document.Delete(indices);
    }

    private void Undo()
    {
        _model.Current?.Undo();
        Refresh();
    }

    private void Redo()
    {
        _model.Current?.Redo();
        Refresh();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_busy || e.Handled) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.Delete && !_selection.IsEmpty) { DeleteSelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A && _model.Current is { } document) { _selection.SelectAll(document.Pages); Rebuild(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { if (shift) Redo(); else Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
    }

    // ---------------------------------------------------------------- mouse: click to select, drag to reorder

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _model.Current is null) return;
        _press = (CellAt(e.GetPosition(_panel))?.Id, e.GetPosition(_host), Keyboard.Modifiers);
        _host.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_press is not { } press || e.LeftButton != MouseButtonState.Pressed) return;
        if (_moving is null)
        {
            var moved = e.GetPosition(_host) - press.At;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (press.Id is not { } id) return;
            BeginDrag(id);
        }
        UpdateDrag(e.GetPosition(_panel), e.GetPosition(_scroll));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        var press = _press;
        _press = null;
        _host.ReleaseMouseCapture();
        if (press is not { } p) return;
        if (_moving is not null)
        {
            EndDrag(commit: true);
            return;
        }
        if (_model.Current is not { } document) return;
        if (p.Id is { } id)
        {
            var mode = (p.Keys & ModifierKeys.Shift) != 0 ? SelectMode.Range : (p.Keys & ModifierKeys.Control) != 0 ? SelectMode.Toggle : SelectMode.Replace;
            _selection.Click(id, document.Pages, mode);
        }
        else _selection.Clear();
        Rebuild();
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (_press is null) return;
        _press = null;
        EndDrag(commit: false);
    }

    private void BeginDrag(Guid id)
    {
        if (_model.Current is not { } document) return;
        _moving = _selection.Contains(id) ? _selection.Ids.ToHashSet() : [id];
        _display = document.Pages.Select(p => p.Id).ToList();
        _host.Cursor = Cursors.SizeAll;
        Rebuild();
    }

    private void UpdateDrag(Point inPanel, Point inScroll)
    {
        if (_moving is null || _display is null) return;
        if (inScroll.Y < 28) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 16);
        else if (inScroll.Y > _scroll.ViewportHeight - 28) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 16);
        var insertAt = PageReorder.InsertionIndex(_display, _moving, CellAt(inPanel)?.Id);
        if (insertAt is not { } at) return;
        var order = PageReorder.Order(_display, _moving, at);
        if (order.SequenceEqual(_display)) return;
        _display = order.ToList();
        Rebuild();
    }

    /// <summary>Ends a drag: the pages go where the preview shows them (one undo step), or stay where they were.</summary>
    private void EndDrag(bool commit)
    {
        if (_moving is null) return;
        var moving = _moving;
        var display = _display;
        _moving = null;
        _display = null;
        _host.Cursor = null;
        if (commit && display is not null && _model.Current is { } document)
        {
            var insertAt = display.TakeWhile(id => !moving.Contains(id)).Count();
            var indices = document.Pages.Select((p, i) => (p, i)).Where(x => moving.Contains(x.p.Id)).Select(x => x.i).ToList();
            document.MoveMany(indices, insertAt);
        }
        Rebuild();
        Refresh();
    }

    // ---------------------------------------------------------------- the window's buttons

    protected override string PrimaryTitle => _model.PrimaryTitle;
    protected override bool CanApply => _model.CanSave;
    protected override bool HasReset => _model.Current?.IsChanged == true;

    protected override void ResetEdits()
    {
        _selection.Clear();
        _model.Current?.Reset();
    }

    protected override void BeginWork()
    {
        _busy = true;
        _snapshot = _model.Snapshot();
        _host.IsEnabled = false;
        UpdateToolbar();
    }

    protected override void EndWork()
    {
        _busy = false;
        _host.IsEnabled = true;
        UpdateToolbar();
    }

    protected override SaveOutcome SaveCore(IProgress<double> progress, CancellationToken cancel) =>
        Outcomes.From(PagesModel.Save(_snapshot, progress, cancel));

    // ---------------------------------------------------------------- demo states

    public override void ApplyDemoState(string state)
    {
        if (!_firstLoadDone) _pendingDemo.Add(state);
        else ApplyDemo(state);
    }

    private void ApplyDemo(string state)
    {
        foreach (var token in state.Split(','))
        {
            var document = _model.Current;
            if (token == "next" && _filmstrip is not null) { _filmstrip.Selection += 1; continue; }
            if (document is null) continue;
            var parts = token.Split(':');
            switch (parts[0])
            {
                case "select":
                    _selection.Clear();
                    foreach (var n in parts[1].Split('+')) if (int.Parse(n) - 1 < document.Pages.Count) _selection.Click(document.Pages[int.Parse(n) - 1].Id, document.Pages, SelectMode.Toggle);
                    Rebuild();
                    break;
                case "rotate": RotateSelection(90); break;
                case "delete": DeleteSelection(); break;
                case "undo": Undo(); break;
                case "move": document.Move(int.Parse(parts[1]) - 1, int.Parse(parts[2]) - 1); break;
                case "drag":
                    // Pretends the pointer is carrying the selected pages (or the given one) over page N.
                    BeginDrag(document.Pages[int.Parse(parts[1]) - 1].Id);
                    var hover = document.Pages[int.Parse(parts[2]) - 1].Id;
                    var at = PageReorder.InsertionIndex(_display!, _moving!, hover);
                    if (at is { } insert) { _display = PageReorder.Order(_display!, _moving!, insert).ToList(); Rebuild(); }
                    break;
            }
        }
        Refresh();
    }
}

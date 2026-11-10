using OpenShelf.Ui;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenShelf.Tools.Media;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>Joins several videos, or several audio files, into one in a chosen order: drag a row to move it, remove the ones you
/// don't want (two always stay).</summary>
internal sealed class MergeEditor : MediaEditorWindow, IMediaEditorDemo
{
    public static readonly Size ContentSize = new(470, 440);

    private const double RowHeight = 38;

    private readonly MergeState _state;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _total = ToolKit.Text("", 11, "Text2");
    private readonly TextBlock _output = ToolKit.Text("", 11, "Text2");
    private readonly TextBlock _problem = new() { FontSize = 11, Foreground = ToolKit.Brush("Danger"), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<Guid, Border> _byId = [];
    private bool _loaded;
    private Guid? _dragging;

    public MergeEditor(EditorHost host, IMediaOperations operations, IReadOnlyList<string> paths) : base(host, operations, "Merge", ContentSize)
    {
        _state = new MergeState(paths);
        CanvasWell = false;
        SetCanvasPadding(0);

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var hint = ToolKit.Text("Drag to change the order. They play one after another.", 12, "Text2");
        hint.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(hint);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows };
        var card = new Border
        {
            Child = scroll,
            BorderBrush = ToolKit.Brush("Hairline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Background = ToolKit.Brush("Well"),
        };
        Grid.SetRow(card, 1);
        stack.Children.Add(card);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _output.HorizontalAlignment = HorizontalAlignment.Right;
        _output.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_output, 1);
        footer.Children.Add(_total);
        footer.Children.Add(_output);
        var bottom = new StackPanel();
        bottom.Children.Add(footer);
        bottom.Children.Add(_problem);
        Grid.SetRow(bottom, 2);
        stack.Children.Add(bottom);
        Canvas = stack;
        Controls = Card;

        Rebuild();
        Loaded += (_, _) => Load();
    }

    protected override string PrimaryText => "Merge";
    protected override bool CanExport => _loaded && _state.CanMerge;
    protected override bool HasReset => true;

    protected override void ResetEdits()
    {
        _state.Reset();
        Rebuild();
    }

    // ---------------------------------------------------------------- rows

    private void Rebuild()
    {
        _rows.Children.Clear();
        _byId.Clear();
        foreach (var item in _state.Items)
        {
            var row = MakeRow(item);
            _byId[item.Id] = row;
            _rows.Children.Add(row);
        }
        Refresh2();
    }

    private Border MakeRow(MergeState.Item item)
    {
        var grid = new Grid { Height = RowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        var number = ToolKit.Text("", 11, "Text2", FontWeights.SemiBold);
        number.Name = "Number";
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.VerticalAlignment = VerticalAlignment.Center;
        var icon = ToolKit.Icon(_state.IsVideo ? Glyphs.Film : Glyphs.Waveform, 14, "Accent");
        icon.Margin = new Thickness(0, 0, 10, 0);
        var name = ToolKit.Text(item.Name, 12, "Text");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.ToolTip = item.Path;
        var duration = ToolKit.Text(item.Duration is { } d ? MediaTime.Format(d) : "", 11, "Text2");
        duration.Name = "Duration";
        duration.VerticalAlignment = VerticalAlignment.Center;
        duration.Margin = new Thickness(8, 0, 4, 0);
        var remove = ToolKit.IconButton(Glyphs.Remove, "Remove from the merge", (_, _) => Remove(item.Id), 24);
        remove.Name = "Remove";
        remove.IsEnabled = _state.CanRemove;
        ToolKit.Name(remove, "Remove " + item.Name);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(name, 2);
        Grid.SetColumn(duration, 3);
        Grid.SetColumn(remove, 4);
        grid.Children.Add(number);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(duration);
        grid.Children.Add(remove);

        var border = new Border
        {
            Child = grid,
            Tag = item.Id,
            Focusable = true,
            Cursor = Cursors.SizeNS,
            Background = Brushes.Transparent,
            BorderBrush = ToolKit.Brush("Hairline"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = RowHeight,
            FocusVisualStyle = null,
        };
        ToolKit.Name(border, item.Name);
        border.PreviewMouseLeftButtonDown += (s, e) => BeginDrag(item.Id, border, e);
        border.PreviewMouseMove += (s, e) => ContinueDrag(border, e);
        border.PreviewMouseLeftButtonUp += (s, e) => EndDrag(border);
        border.LostMouseCapture += (s, e) => EndDrag(border);
        border.KeyDown += (s, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Up or Key.Down)
            {
                MoveRow(IndexOf(item.Id), IndexOf(item.Id) + (key == Key.Up ? -1 : 1));
                border.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete) { Remove(item.Id); e.Handled = true; }
        };
        return border;
    }

    private int IndexOf(Guid id) => _state.Items.FindIndex(i => i.Id == id);

    private void Remove(Guid id)
    {
        _state.Remove(id);
        Rebuild();
    }

    /// <summary>Moves a row, as dragging or Alt+Up/Down does.</summary>
    internal void MoveRow(int from, int to)
    {
        to = Math.Clamp(to, 0, _state.Items.Count - 1);
        if (from < 0 || from >= _state.Items.Count || from == to) return;
        var id = _state.Items[from].Id;
        _state.Move(from, to);
        var row = _byId[id];
        _rows.Children.Remove(row);
        _rows.Children.Insert(to, row);
        Refresh2();
    }

    private void BeginDrag(Guid id, Border row, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
        _dragging = id;
        row.Background = ToolKit.Brush("FillHover");
        row.CaptureMouse();
        row.Focus();
        e.Handled = true;
    }

    private void ContinueDrag(Border row, MouseEventArgs e)
    {
        if (_dragging is not { } id || !row.IsMouseCaptured) return;
        var y = e.GetPosition(_rows).Y;
        MoveRow(IndexOf(id), ReorderMath.TargetIndex(y, RowHeight, _state.Items.Count));
    }

    private void EndDrag(Border row)
    {
        if (_dragging is null) return;
        _dragging = null;
        row.Background = Brushes.Transparent;
        if (row.IsMouseCaptured) row.ReleaseMouseCapture();
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = start; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    /// <summary>Numbers the rows, shows the total and the name of the result, and re-reads the button.</summary>
    private void Refresh2()
    {
        for (var i = 0; i < _state.Items.Count; i++)
        {
            var row = _byId[_state.Items[i].Id];
            var grid = (Grid)row.Child;
            ((TextBlock)grid.Children[0]).Text = (i + 1).ToString();
            ((Button)grid.Children[4]).IsEnabled = _state.CanRemove;
            ((IconView)grid.Children[1]).Icon = _state.IsVideo ? Glyphs.Film : Glyphs.Waveform;
            ((TextBlock)grid.Children[3]).Text = _state.Items[i].Duration is { } d ? MediaTime.Format(d) : "";
        }
        _total.Text = "Total " + MediaTime.Format(_state.TotalDuration);
        _output.Text = $"Saves as “{_state.OutputName}”";
        Refresh();
    }

    private async void Load()
    {
        try
        {
            if (!Operations.IsAvailable)
            {
                _problem.Text = Guidance.LoadError(new OpenShelf.Convert.MissingToolException("ffmpeg"), "");
                return;
            }
            var allVideo = true;
            foreach (var item in _state.Items.ToList())
            {
                try
                {
                    var info = await Operations.GetInfoAsync(item.Path, Lifetime);
                    item.Duration = info.Duration;
                    if (!info.HasVideo) allVideo = false;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { _problem.Text = Guidance.LoadError(e, $"“{item.Name}” can’t be read."); allVideo = false; }
            }
            _state.IsVideo = allVideo;
            _loaded = true;
            Refresh2();
        }
        catch (OperationCanceledException) { }
    }

    protected override async Task<SaveOutcome> ExportAsync(CancellationToken cancel)
    {
        var paths = _state.Paths;
        var outcome = new SaveOutcome { SavedLabel = "merged" };
        return await ExportRunner.RunAsync(new[] { paths }, outcome, p => p[0],
            async (p, _, progress, ct) => [await Operations.MergeAsync(p, progress, ct)],
            (fraction, _) => Report(fraction), cancel);
    }

    void IMediaEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
        {
            if (token.StartsWith("move=", StringComparison.Ordinal))
            {
                var parts = token[5..].Split('>');
                MoveRow(int.Parse(parts[0]), int.Parse(parts[1]));
            }
            else if (token.StartsWith("remove=", StringComparison.Ordinal)) Remove(_state.Items[int.Parse(token[7..])].Id);
        }
    }

    internal MergeState State => _state;
}

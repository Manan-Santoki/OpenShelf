using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OpenShelf.Tools;

/// <summary>
/// The files an editor was given, as a row of thumbnails under its canvas: click one (or use the arrows) to work on it. A dot marks
/// files with an edit waiting to be saved. With a "Same for all" switch it also offers applying one edit to every file.
/// Header: "2 of 5 · name". Port of EditorFilmstrip.swift.
/// </summary>
public sealed class FilmstripView : StackPanel
{
    /// <summary>Height the strip needs, so a window can leave room for it.</summary>
    public const double StripHeight = 96;

    private readonly IReadOnlyList<ImageSource?> _thumbnails;
    private readonly IReadOnlyList<string> _names;
    private readonly string _placeholder;
    private readonly TextBlock _count = ToolKit.Text("", 11, "Text", FontWeights.SemiBold);
    private readonly TextBlock _name = ToolKit.Text("", 11, "Text3");
    private readonly SwitchRow? _same;
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly List<(Border Frame, Ellipse Dot)> _frames = [];
    private int _selection;

    /// <param name="thumbnails">A picture of each file; null shows the placeholder glyph (sound, or a picture still loading).</param>
    /// <param name="names">File names.</param>
    /// <param name="withSameForAll">Whether to show the "Same for all" switch.</param>
    /// <param name="itemName">What the files are called in the switch's help, e.g. "image" or "video".</param>
    /// <param name="placeholderGlyph">Icon shown for a file without a picture.</param>
    public FilmstripView(IReadOnlyList<ImageSource?> thumbnails, IReadOnlyList<string> names, bool withSameForAll, string itemName = "image", string placeholderGlyph = "image")
    {
        _thumbnails = thumbnails;
        _names = names;
        _placeholder = placeholderGlyph;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _count.VerticalAlignment = VerticalAlignment.Center;
        _name.VerticalAlignment = VerticalAlignment.Center;
        _name.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(_name, 1);
        header.Children.Add(_count);
        header.Children.Add(_name);
        if (withSameForAll)
        {
            _same = new SwitchRow("Same for all", true, $"On: one edit applies to every {itemName}. Off: edit each {itemName} on its own.");
            _same.Toggled += () => SameForAllChanged?.Invoke(_same.IsChecked);
            Grid.SetColumn(_same, 2);
            header.Children.Add(_same);
        }
        Children.Add(header);

        _previous = Step("chevron-left", "Previous", -1);
        _next = Step("chevron-right", "Next", 1);
        for (var i = 0; i < thumbnails.Count; i++) _items.Children.Add(MakeThumbnail(i));
        _scroll.Content = _items;
        var strip = new Grid { Height = 56 };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_scroll, 1);
        Grid.SetColumn(_next, 2);
        strip.Children.Add(_previous);
        strip.Children.Add(_scroll);
        strip.Children.Add(_next);
        Children.Add(strip);
        Refresh();
    }

    /// <summary>The shown file.</summary>
    public int Selection
    {
        get => _selection;
        set
        {
            var clamped = Math.Clamp(value, 0, _names.Count - 1);
            if (clamped == _selection) return;
            _selection = clamped;
            Refresh();
            SelectionChanged?.Invoke(clamped);
        }
    }

    public bool SameForAll
    {
        get => _same?.IsChecked ?? false;
        set => _same?.SetSilently(value);
    }

    /// <summary>The user chose another file.</summary>
    public event Action<int>? SelectionChanged;

    /// <summary>The user turned "Same for all" on or off.</summary>
    public event Action<bool>? SameForAllChanged;

    /// <summary>Wires the strip to <paramref name="edits"/>: selection, the switch and the dots stay in step both ways.</summary>
    public void Attach<T>(FileEdits<T> edits) where T : notnull
    {
        SelectionChanged += index => edits.Index = index;
        SameForAllChanged += on => edits.SameForAll = on;
        edits.Changed += () =>
        {
            _selection = edits.Index;
            if (_same is not null) _same.SetSilently(edits.SameForAll);
            SetEdited(edits.Edited);
            Refresh();
        };
        _selection = edits.Index;
        if (_same is not null) _same.SetSilently(edits.SameForAll);
        SetEdited(edits.Edited);
        Refresh();
    }

    /// <summary>Shows a dot on each file whose edit is waiting.</summary>
    public void SetEdited(IReadOnlyList<bool> edited)
    {
        for (var i = 0; i < _frames.Count && i < edited.Count; i++)
        {
            _frames[i].Dot.Visibility = edited[i] ? Visibility.Visible : Visibility.Collapsed;
            ToolKit.Name(_frames[i].Frame, edited[i] ? _names[i] + ", edited" : _names[i]);
        }
    }

    /// <summary>Replaces one file's picture (it was still loading).</summary>
    public void SetThumbnail(int index, ImageSource? picture)
    {
        if (_frames[index].Frame.Child is not Grid grid) return;
        grid.Children.RemoveAt(0);
        grid.Children.Insert(0, Picture(picture));
    }

    private void Refresh()
    {
        _count.Text = $"{_selection + 1} of {_names.Count}";
        _name.Text = _names[_selection];
        for (var i = 0; i < _frames.Count; i++)
        {
            var selected = i == _selection;
            _frames[i].Frame.BorderBrush = selected ? ToolKit.Brush("Accent") : ToolKit.Brush("HairlineStrong");
            _frames[i].Frame.BorderThickness = new Thickness(selected ? 2 : 1);
        }
        _previous.IsEnabled = _selection > 0;
        _next.IsEnabled = _selection < _names.Count - 1;
        if (_frames.Count > _selection && _scroll.IsLoaded)
        {
            // Keep the shown file in view.
            var frame = _frames[_selection].Frame;
            var offset = frame.TranslatePoint(new Point(0, 0), _items).X + frame.ActualWidth / 2 - _scroll.ViewportWidth / 2;
            _scroll.ScrollToHorizontalOffset(Math.Max(0, offset));
        }
    }

    private Button Step(string glyph, string tip, int delta)
    {
        var button = ToolKit.IconButton(glyph, tip, (_, _) => Selection += delta, 22);
        button.Height = 56;
        button.MinHeight = 56;
        button.FontSize = 11;
        return button;
    }

    private UIElement Picture(ImageSource? source)
    {
        if (source is not null)
            return new Image { Source = source, Stretch = Stretch.UniformToFill, SnapsToDevicePixels = true };
        return new Border { Background = ToolKit.Brush("Fill"), Child = ToolKit.Icon(_placeholder, 18, "Text3") };
    }

    private UIElement MakeThumbnail(int index)
    {
        var picture = Picture(_thumbnails[index]);
        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = ToolKit.Brush("Accent"),
            Stroke = Brushes.White,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -3, 0),
            Visibility = Visibility.Collapsed,
        };
        var grid = new Grid { Width = 52, Height = 52, ClipToBounds = true };
        grid.Children.Add(picture);
        var frame = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = ToolKit.Brush("HairlineStrong"),
            Margin = new Thickness(3),
            ToolTip = _names[index],
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true,
            Focusable = false,
        };
        // The dot overhangs the frame, so it sits outside the clipped picture.
        var holder = new Grid();
        holder.Children.Add(frame);
        holder.Children.Add(dot);
        dot.Margin = new Thickness(0, 0, 0, 0);
        dot.HorizontalAlignment = HorizontalAlignment.Right;
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.RenderTransform = new TranslateTransform(-1, 1);
        frame.MouseLeftButtonUp += (_, _) => Selection = index;
        ToolKit.Name(frame, _names[index]);
        _frames.Add((frame, dot));
        return holder;
    }
}

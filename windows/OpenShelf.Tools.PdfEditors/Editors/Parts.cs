using OpenShelf.Ui;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Tools.Pdf;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>Lets the demo executable put a PDF editor into a named state for screenshots.</summary>
public interface IPdfEditorDemo
{
    void ApplyDemoState(string state);
}

/// <summary>Small pieces the PDF editors share.</summary>
internal static class Parts
{
    public const string PageGlyph = "file-text";

    /// <summary>A rendered page as a frozen picture.</summary>
    public static BitmapSource ToBitmap(PdfThumbnail thumbnail)
    {
        var bitmap = BitmapSource.Create(thumbnail.Width, thumbnail.Height, 96, 96, PixelFormats.Pbgra32, null, thumbnail.Bgra, thumbnail.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A page picture (or a placeholder while it loads) in a rounded square, with an optional count badge.</summary>
    public static Border Thumb(ImageSource? source, double size = 52, int? badge = null)
    {
        UIElement face = source is null
            ? new Border { Background = ToolKit.Brush("Fill"), Child = ToolKit.Icon(PageGlyph, size * 0.4, "Text3") }
            : new Border
            {
                Background = Brushes.White,
                Padding = new Thickness(2),
                Child = new Image { Source = source, Stretch = Stretch.Uniform, SnapsToDevicePixels = true },
            };
        var clip = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = face, BorderBrush = ToolKit.Brush("HairlineStrong"), BorderThickness = new Thickness(1) };
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(clip);
        if (badge is { } count)
        {
            var label = ToolKit.Text(count.ToString(), 10, "Text", FontWeights.Bold);
            label.Foreground = Brushes.White;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(new Border
            {
                Child = label,
                Background = ToolKit.Brush("Accent"),
                CornerRadius = new CornerRadius(9),
                MinWidth = 18,
                Height = 18,
                Padding = new Thickness(5, 1, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -5, -5),
            });
        }
        return new Border { Child = grid, Width = size, Height = size };
    }

    /// <summary>The head of Compress and Metadata: a first-page picture, the name and a detail line. Fill the picture later with <see cref="SetPicture"/>.</summary>
    public static Grid Header(string title, string subtitle, int? badge, out ContentControl picture)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        picture = new ContentControl { Content = Thumb(null, 52, badge), Focusable = false, IsTabStop = false };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        var name = ToolKit.Text(title, 13, "Text", FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = title;
        text.Children.Add(name);
        text.Children.Add(ToolKit.Text(subtitle, 11, "Text3"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(picture);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>The first page of a PDF on white, or null when it cannot be read. (PdfThumbnails.FirstPageAsync closes its stream before
    /// rewinding it and so always answers with its grey placeholder; this goes through the Pages document instead.)</summary>
    public static async Task<BitmapSource?> FirstPageAsync(string path, int size = 160, CancellationToken cancel = default)
    {
        try
        {
            var document = await PdfPagesDocument.OpenAsync(path, cancel);
            return ToBitmap(await document.RenderThumbnailAsync(0, size, cancel));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Fills a filmstrip with each PDF's first page, one after another; unreadable PDFs keep their placeholder.</summary>
    public static async void LoadFirstPages(FilmstripView strip, IReadOnlyList<PdfFileState> files, CancellationToken cancel)
    {
        for (var i = 0; i < files.Count; i++)
        {
            if (!files[i].IsUsable) continue;
            try { if (await FirstPageAsync(files[i].Path, 160, cancel) is { } picture) strip.SetThumbnail(i, picture); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
        }
    }

    public static void SetPicture(ContentControl holder, ImageSource source, int? badge) => holder.Content = Thumb(source, 52, badge);

    /// <summary>A line with an icon, in a rounded card: the summary under Split and Compress.</summary>
    public static (Border Card, TextBlock Text, TextBlock Detail, IconView Glyph) SummaryCard(string glyph)
    {
        var icon = ToolKit.Icon(glyph, 13, "Accent");
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = ToolKit.Text("", 12, "Text");
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(8, 0, 0, 0);
        var detail = ToolKit.Text("", 12, "Text2");
        detail.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(detail);
        return (ToolKit.Card(row, 10), text, detail, icon);
    }

    public static void Tint(TextBlock block, Tone tone)
    {
        block.Foreground = tone switch
        {
            Tone.Warning => ToolKit.Brush("Danger"),
            Tone.Muted => ToolKit.Brush("Text2"),
            Tone.Info => ToolKit.Brush("Text2"),
            _ => ToolKit.Brush("Text"),
        };
    }

    /// <summary>The window title for a work-in-progress state is left to the framework ("Saving…"); this is the thin bar that shows how far.</summary>
    public static SlimProgress Progress() => new();
}

/// <summary>A thin progress bar that is invisible (but keeps its room) until work starts.</summary>
internal sealed class SlimProgress : Grid
{
    private readonly Border _fill = new() { Background = ToolKit.Brush("Accent"), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
    private double _value;

    public SlimProgress()
    {
        Height = 4;
        Visibility = Visibility.Hidden;
        Children.Add(new Border { Background = ToolKit.Brush("Fill"), CornerRadius = new CornerRadius(2) });
        Children.Add(_fill);
        SizeChanged += (_, _) => Layout();
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            Layout();
        }
    }

    public bool IsActive
    {
        set
        {
            Visibility = value ? Visibility.Visible : Visibility.Hidden;
            if (value) Value = 0;
        }
    }

    private void Layout() => _fill.Width = Math.Max(4, ActualWidth * _value);
}

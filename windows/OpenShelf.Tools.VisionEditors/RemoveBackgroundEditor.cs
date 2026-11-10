using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShelf.Core;
using OpenShelf.Tools.Imaging;
using OpenShelf.Vision;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.VisionEditors;

/// <summary>
/// The "Remove BG" tool (SPEC §2.4): cuts the subject out of a photo on this PC, with a Result / Original preview on a checkerboard,
/// saved as "Name No Background.png". With several photos a filmstrip previews each one's result and saving cuts out all of them that
/// have a subject. There is no Same for all: a cutout belongs to one picture. When no clear subject is found the window says so.
/// </summary>
public sealed class RemoveBackgroundEditor : EditorWindow, IEditorDemo
{
    public static Size ContentSize(int count) => new(520, count > 1 ? 640 + FilmstripView.StripHeight : 640);

    /// <summary>Segoe MDL2 glyph over the "no clear subject" explanation.</summary>
    internal const string NoSubjectGlyph = "";

    private const int PreviewLongSide = 1600;

    private readonly IReadOnlyList<EditableImage> _images;
    private readonly RemoveBackgroundSession _session;
    private readonly ImageSurface _surface = new() { Inset = 8 };
    private readonly Border _overlay = new();
    private readonly TextBlock _overlayGlyph = ToolKit.Glyph("", 28, "Text2");
    private readonly TextBlock _overlayText = ToolKit.Text("", 13, "Text", wrap: true);
    private readonly TextBlock _overlayHint = ToolKit.Text("", 11, "Text3", wrap: true);
    private readonly ThinProgress _overlayBar = new() { Width = 220, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Segmented _preview = new(["Result", "Original"], 0, 22);
    private readonly TextBlock _note = ToolKit.Text(" ", 11, "Text3");
    private bool _showsOriginal;
    private Cutout? _bitmapOf;
    private BitmapSource? _cutoutBitmap;

    /// <param name="engine">Replaces the cutout engine (for tests and the demo).</param>
    public RemoveBackgroundEditor(EditorHost host, IReadOnlyList<EditableImage> images, IBackgroundEngine? engine = null)
        : base(host, "Remove Background", ContentSize(images.Count))
    {
        _images = images;
        _session = new RemoveBackgroundSession(images.Select(i => i.Path).ToList(), engine);

        var canvas = new Grid();
        canvas.Children.Add(new CheckerboardBackdrop());
        canvas.Children.Add(_surface);
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        _overlay.VerticalAlignment = VerticalAlignment.Center;
        _overlay.Background = ToolKit.Brush("Popup");
        _overlay.BorderBrush = ToolKit.Brush("HairlineStrong");
        _overlay.BorderThickness = new Thickness(1);
        _overlay.CornerRadius = new CornerRadius(12);
        _overlay.Padding = new Thickness(20);
        _overlay.MaxWidth = 380;
        var inner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        _overlayGlyph.Margin = new Thickness(0, 0, 0, 10);
        _overlayText.TextAlignment = TextAlignment.Center;
        _overlayHint.TextAlignment = TextAlignment.Center;
        _overlayHint.Margin = new Thickness(0, 6, 0, 0);
        inner.Children.Add(_overlayGlyph);
        inner.Children.Add(_overlayText);
        inner.Children.Add(_overlayBar);
        inner.Children.Add(_overlayHint);
        _overlay.Child = inner;
        canvas.Children.Add(_overlay);
        Canvas = canvas;
        SetCanvasPadding(0);

        if (images.Count > 1)
        {
            var strip = EditorFilmstrip(images);
            strip.Attach(_session.States);
            strip.SelectionChanged += index => _ = _session.ShowAsync(index);
            Filmstrip = strip;
        }

        _preview.SelectionChanged += index => { _showsOriginal = index == 1; Update(); };
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _note.HorizontalAlignment = HorizontalAlignment.Center;
        _note.Margin = new Thickness(0, 10, 0, 0);
        var controls = new StackPanel();
        controls.Children.Add(_preview);
        controls.Children.Add(_note);
        Controls = controls;

        _session.Changed += () => Dispatcher.InvokeAsync(Update);
        Loaded += (_, _) => _ = _session.ShowAsync(0);
        Closed += (_, _) => _session.Cancel();
        Update();
    }

    private static FilmstripView EditorFilmstrip(IReadOnlyList<EditableImage> images) =>
        new(images.Select(i => (ImageSource?)i.Thumbnail).ToList(), images.Select(i => i.Name).ToList(), withSameForAll: false);

    protected override string PrimaryTitle => _session.PrimaryTitle;
    protected override bool CanApply => _session.IsSaveEnabled;

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel) => _session.Save(progress, cancel);

    // ---------------------------------------------------------------- what is shown

    private void Update()
    {
        var image = _images[_session.Index];
        var phase = _session.Phase;
        var ready = phase == CutoutPhase.Ready;
        _preview.IsEnabled = ready;
        if (!ready && _showsOriginal) { _showsOriginal = false; _preview.SetSilently(0); }
        _note.Text = ready ? RemoveBackgroundSession.SavedNote : " ";

        // The picture: the cutout or the original; the original dimmed behind an explanation.
        _surface.ImageSize = image.PixelSize;
        _surface.Opacity = 1;
        switch (phase)
        {
            case CutoutPhase.Ready:
                _surface.Source = _showsOriginal ? image.Preview : CutoutBitmap();
                break;
            case CutoutPhase.NoSubject:
            case CutoutPhase.Failed:
                _surface.Source = image.Preview;
                _surface.Opacity = 0.35;
                break;
            default:
                _surface.Source = null;
                break;
        }

        // The card over it.
        _overlay.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        _overlayBar.Visibility = Visibility.Collapsed;
        _overlayHint.Visibility = Visibility.Collapsed;
        _overlayGlyph.Visibility = Visibility.Collapsed;
        switch (phase)
        {
            case CutoutPhase.Downloading:
                _overlayText.Text = $"Getting the background model… {(int)(_session.DownloadProgress * 100)}%";
                _overlayHint.Text = "A one-time 4.6 MB download. After this it works offline.";
                _overlayHint.Visibility = Visibility.Visible;
                _overlayBar.Indeterminate = false;
                _overlayBar.Value = _session.DownloadProgress;
                _overlayBar.Visibility = Visibility.Visible;
                break;
            case CutoutPhase.Finding:
                _overlayText.Text = RemoveBackgroundSession.FindingText;
                _overlayBar.Indeterminate = true;
                _overlayBar.Visibility = Visibility.Visible;
                break;
            case CutoutPhase.NoSubject:
            case CutoutPhase.Failed:
                _overlayText.Text = _session.Message ?? RemoveBackgroundSession.FailedText;
                _overlayGlyph.Text = phase == CutoutPhase.NoSubject ? NoSubjectGlyph : "";
                _overlayGlyph.Visibility = Visibility.Visible;
                _overlayBar.Indeterminate = false;
                break;
        }
        Refresh();
    }

    private BitmapSource? CutoutBitmap()
    {
        if (_session.Cutout is not { } cutout) return null;
        if (!ReferenceEquals(cutout, _bitmapOf))
        {
            _cutoutBitmap = ToPreviewBitmap(cutout);
            _bitmapOf = cutout;
        }
        return _cutoutBitmap;
    }

    /// <summary>The cutout as a frozen bitmap no larger than the editors' preview size, ready to draw over the checkerboard.</summary>
    internal static BitmapSource ToPreviewBitmap(Cutout cutout)
    {
        var full = BitmapSource.Create(cutout.Width, cutout.Height, 96, 96, PixelFormats.Bgra32, null, cutout.Bgra, cutout.Width * 4);
        var longest = Math.Max(cutout.Width, cutout.Height);
        if (longest <= PreviewLongSide)
        {
            full.Freeze();
            return full;
        }
        // Scale in premultiplied form so the soft edges do not pick up colour from transparent pixels.
        var premultiplied = new FormatConvertedBitmap(full, PixelFormats.Pbgra32, null, 0);
        var scale = (double)PreviewLongSide / longest;
        var scaled = new TransformedBitmap(premultiplied, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    // ---------------------------------------------------------------- the demo

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "original":
                    _preview.SetSilently(1);
                    _showsOriginal = true;
                    Update();
                    break;
                case "next":
                    if (_images.Count > 1) _ = _session.ShowAsync(1);
                    break;
                case "second":
                    if (_images.Count > 2) _ = _session.ShowAsync(2);
                    break;
            }
    }

    internal RemoveBackgroundSession Session => _session;
}

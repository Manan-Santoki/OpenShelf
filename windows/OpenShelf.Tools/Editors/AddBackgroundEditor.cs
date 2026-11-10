using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Microsoft.Win32;
using OpenShelf.Tools.Imaging;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.Editors;

/// <summary>What an image is placed on, and how: gradient, solid colour or a picture, with padding, rounded corners, a shadow and a canvas ratio.</summary>
public sealed record BackgroundSettings
{
    public enum Kind { Gradient, Solid, CustomColor, CustomImage }

    public Kind Choice { get; init; } = Kind.Gradient;
    /// <summary>Which gradient or solid swatch.</summary>
    public int Index { get; init; }
    public Color CustomColor { get; init; } = Color.FromRgb(0xF5, 0x9E, 0x73);
    public string? BackgroundImage { get; init; }
    public double Padding { get; init; } = 160;
    public double Corners { get; init; } = 24;
    public double Shadow { get; init; } = 24;
    public CanvasRatio Ratio { get; init; } = CanvasRatio.Auto;

    public BackgroundPaint ToPaint() => Choice switch
    {
        Kind.Gradient => new BackgroundPaint.Gradient(GradientStyle.All[Math.Clamp(Index, 0, GradientStyle.All.Count - 1)]),
        Kind.Solid => new BackgroundPaint.Solid(SolidColors.All[Math.Clamp(Index, 0, SolidColors.All.Count - 1)]),
        Kind.CustomColor => new BackgroundPaint.Solid(new HexColor(((uint)CustomColor.R << 16) | ((uint)CustomColor.G << 8) | CustomColor.B)),
        _ => BackgroundImage is { } path ? new BackgroundPaint.Picture(path) : new BackgroundPaint.Solid(0xFFFFFFu),
    };

    public BackgroundComposition ToComposition() => new(ToPaint(), Padding, Corners, Shadow, Ratio);
}

/// <summary>Places images on a gradient, colour or picture background with padding, rounded corners and a drop shadow, and saves PNG.
/// Given several images a filmstrip moves between them, and one look can apply to all of them or each can have its own.</summary>
public sealed class AddBackgroundEditor : EditorWindow, IEditorDemo
{
    public static Size ContentSize(int count) => new(480, count > 1 ? 700 + FilmstripView.StripHeight : 700);

    private const int PreviewSource = 900;
    private const double PreviewCanvas = 640;

    private readonly IReadOnlyList<EditableImage> _images;
    private readonly FileEdits<BackgroundSettings> _edits;
    private readonly ImageSurface _surface = new() { Inset = 10, Checkerboard = false };
    private readonly MagickImage?[] _sources;
    private readonly object _lock = new();
    private readonly List<SwatchButton> _gradients = [];
    private readonly List<SwatchButton> _solids = [];
    private readonly SwatchButton _picture;
    private readonly ColorWell _custom;
    private readonly LabeledSlider _padding;
    private readonly LabeledSlider _corners;
    private readonly LabeledSlider _shadow;
    private readonly Segmented _ratio;
    private CancellationTokenSource? _rendering;

    public AddBackgroundEditor(EditorHost host, IReadOnlyList<EditableImage> images) : base(host, "Add BG", ContentSize(images.Count))
    {
        _images = images;
        _sources = new MagickImage?[images.Count];
        _edits = new FileEdits<BackgroundSettings>(images.Count, _ => new BackgroundSettings(),
            (i, s) => s.ToComposition().ChangesImage(images[i].PixelSize, images[i].IsTransparent), sameForAll: true);

        Canvas = _surface;
        SetCanvasPadding(0);
        _surface.Height = 290;

        // ---- swatches: two rows, 1 pt apart, so both fit the window
        var gradientRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < GradientStyle.All.Count; i++)
        {
            var index = i;
            using var swatch = BackgroundComposition.Swatch(new BackgroundPaint.Gradient(GradientStyle.All[i]));
            var button = new SwatchButton(new ImageBrush(MagickWpf.ToBitmap(swatch)), $"Gradient {i + 1}");
            button.Click += (_, _) => _edits.Current = _edits.Current with { Choice = BackgroundSettings.Kind.Gradient, Index = index };
            _gradients.Add(button);
            gradientRow.Children.Add(button);
        }
        _picture = new SwatchButton(ToolKit.Brush("Fill"), "Choose a background image…");
        _picture.Face = ToolKit.Glyph("", 12, "Text2");
        _picture.Click += (_, _) => ChoosePicture();
        gradientRow.Children.Add(_picture);

        var solidRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < SolidColors.All.Count; i++)
        {
            var index = i;
            var c = SolidColors.All[i];
            var button = new SwatchButton(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), SolidColors.Names[i]);
            button.Click += (_, _) => _edits.Current = _edits.Current with { Choice = BackgroundSettings.Kind.Solid, Index = index };
            _solids.Add(button);
            solidRow.Children.Add(button);
        }
        _custom = new ColorWell(new BackgroundSettings().CustomColor, "Custom colour");
        _custom.ColorChosen += c => _edits.Current = _edits.Current with { Choice = BackgroundSettings.Kind.CustomColor, CustomColor = c };
        _custom.Margin = new Thickness(0);
        solidRow.Children.Add(_custom);

        var swatches = new StackPanel();
        swatches.Children.Add(gradientRow);
        solidRow.Margin = new Thickness(0, 2, 0, 0);
        swatches.Children.Add(solidRow);

        _padding = new LabeledSlider("Padding", 0, 1000, 160, "px");
        _corners = new LabeledSlider("Corners", 0, 400, 24, "px");
        _shadow = new LabeledSlider("Shadow", 0, 200, 24, "px");
        _padding.ValueChanged += v => _edits.Current = _edits.Current with { Padding = Math.Round(v) };
        _corners.ValueChanged += v => _edits.Current = _edits.Current with { Corners = Math.Round(v) };
        _shadow.ValueChanged += v => _edits.Current = _edits.Current with { Shadow = Math.Round(v) };
        _ratio = new Segmented(CanvasRatios.All.Select(r => r.Title()).ToList(), 0, 12);
        _ratio.SelectionChanged += i => _edits.Current = _edits.Current with { Ratio = CanvasRatios.All[i] };

        var controls = new StackPanel();
        controls.Children.Add(ToolKit.Row("Background", swatches));
        foreach (var slider in new[] { _padding, _corners, _shadow })
        {
            slider.Margin = new Thickness(0, 8, 0, 0);
            controls.Children.Add(slider);
        }
        var ratioRow = ToolKit.Row("Ratio", _ratio);
        ratioRow.Margin = new Thickness(0, 8, 0, 0);
        controls.Children.Add(ratioRow);
        Controls = controls;

        if (images.Count > 1)
        {
            var strip = EditorParts.Filmstrip(images, sameForAll: true);
            strip.Attach(_edits);
            Filmstrip = strip;
        }
        _edits.Changed += OnChanged;
        Loaded += (_, _) => OnChanged();
        Refresh();
    }

    private void ChoosePicture()
    {
        var dialog = new OpenFileDialog { Title = "Choose a background image", Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.avif|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _edits.Current = _edits.Current with { Choice = BackgroundSettings.Kind.CustomImage, BackgroundImage = dialog.FileName };
    }

    private void OnChanged()
    {
        var s = _edits.Current;
        for (var i = 0; i < _gradients.Count; i++) _gradients[i].IsSelected = s.Choice == BackgroundSettings.Kind.Gradient && s.Index == i;
        for (var i = 0; i < _solids.Count; i++) _solids[i].IsSelected = s.Choice == BackgroundSettings.Kind.Solid && s.Index == i;
        _custom.IsSelected = s.Choice == BackgroundSettings.Kind.CustomColor;
        _custom.Color = s.CustomColor;
        _picture.IsSelected = s.Choice == BackgroundSettings.Kind.CustomImage;
        if (s.BackgroundImage is { } path)
        {
            try { _picture.Fill = new ImageBrush(new BitmapImage(new Uri(path))) { Stretch = Stretch.UniformToFill }; _picture.Face = null; }
            catch (Exception) { }
        }
        _padding.SetSilently(s.Padding);
        _corners.SetSilently(s.Corners);
        _shadow.SetSilently(s.Shadow);
        _ratio.SetSilently(CanvasRatios.All.ToList().IndexOf(s.Ratio));
        RenderPreview();
        Refresh();
    }

    private void RenderPreview()
    {
        _rendering?.Cancel();
        var cancel = _rendering = new CancellationTokenSource();
        var index = _edits.Index;
        var image = _images[index];
        var composition = _edits.Current.ToComposition();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(25, cancel.Token);
                MagickImage source;
                lock (_lock)
                {
                    _sources[index] ??= EditableImage.Scaled(ImageFiles.ReadUpright(image.Path), PreviewSource);
                    source = (MagickImage)_sources[index]!.Clone();
                }
                using (source)
                {
                    var canvas = composition.CanvasSize(image.PixelSize);
                    var scale = Math.Min(1, PreviewCanvas / Math.Max(canvas.Width, canvas.Height));
                    using var rendered = composition.Render(source, image.PixelSize, scale);
                    cancel.Token.ThrowIfCancellationRequested();
                    var bitmap = MagickWpf.ToBitmap(rendered);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (cancel.IsCancellationRequested) return;
                        _surface.ImageSize = new Size(bitmap.PixelWidth, bitmap.PixelHeight);
                        _surface.Source = bitmap;
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        });
    }

    protected override string PrimaryTitle => _edits.IsBatch ? _edits.PrimaryTitle("Save") : "Save with Background";
    protected override bool CanApply => _edits.EditedCount > 0;
    protected override bool HasReset => true;

    /// <summary>Resets the shown image, or every image while they share one look. The custom colour and picture stay, ready to pick again.</summary>
    protected override void ResetEdits()
    {
        var keep = _edits.Current;
        BackgroundSettings Fresh(BackgroundSettings _) => new() { CustomColor = keep.CustomColor, BackgroundImage = keep.BackgroundImage };
        _edits.Current = Fresh(keep);
        if (_edits.SameForAll) return;
    }

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel) =>
        BackgroundOperation.Run(_images.Select((image, i) => (image.Path, image.PixelSize, image.IsTransparent, _edits[i].ToComposition())).ToList(), progress, cancel);

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "dark": _edits.Current = _edits.Current with { Choice = BackgroundSettings.Kind.Gradient, Index = 5, Padding = 120, Corners = 40, Shadow = 60 }; break;
                case "solid": _edits.Current = _edits.Current with { Choice = BackgroundSettings.Kind.Solid, Index = 5, Ratio = CanvasRatio.Square }; break;
                case "second": _edits.Index = Math.Min(1, _images.Count - 1); break;
            }
    }
}

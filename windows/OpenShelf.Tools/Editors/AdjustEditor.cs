using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ImageMagick;
using OpenShelf.Tools.Imaging;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenShelf.Tools.Editors;

/// <summary>The "Edit" tool: exposure, colour and orientation adjustments with a live preview on a downscaled copy. Given several images
/// a filmstrip moves between them, and one set of adjustments can apply to all of them or each can have its own.</summary>
public sealed class AdjustEditor : EditorWindow, IEditorDemo
{
    public static Size ContentSize(int count) => new(480, count > 1 ? 720 + FilmstripView.StripHeight : 720);

    private const int PreviewLongSide = 1000;

    private readonly IReadOnlyList<EditableImage> _images;
    private readonly FileEdits<AdjustSettings> _edits;
    private readonly ImageSurface _surface = new() { Inset = 6 };
    private readonly MagickImage?[] _working;
    private readonly object _workingLock = new();
    private readonly Dictionary<string, LabeledSlider> _sliders = [];
    private CancellationTokenSource? _rendering;

    public AdjustEditor(EditorHost host, IReadOnlyList<EditableImage> images) : base(host, images.Count > 1 ? "Edit Images" : "Edit Image", ContentSize(images.Count))
    {
        _images = images;
        _working = new MagickImage?[images.Count];
        _edits = new FileEdits<AdjustSettings>(images.Count, _ => new AdjustSettings(), (_, s) => s.ChangesImage, sameForAll: true);

        Canvas = _surface;
        SetCanvasPadding(0);
        _surface.Height = 300;

        var controls = new StackPanel();
        controls.Children.Add(Transforms());
        void Add(string key, string label, double min, double max, string unit, Func<AdjustSettings, double> get, Func<AdjustSettings, double, AdjustSettings> set)
        {
            var slider = new LabeledSlider(label, min, max, 0, unit);
            slider.Margin = new Thickness(0, 4, 0, 0);
            slider.ValueChanged += v => { _edits.Current = set(_edits.Current, Math.Round(v)); };
            _sliders[key] = slider;
            controls.Children.Add(slider);
        }
        Add("exposure", "Exposure", -100, 100, "", s => s.Exposure, (s, v) => s with { Exposure = v });
        Add("brightness", "Brightness", -100, 100, "", s => s.Brightness, (s, v) => s with { Brightness = v });
        Add("contrast", "Contrast", -100, 100, "", s => s.Contrast, (s, v) => s with { Contrast = v });
        Add("saturation", "Saturation", -100, 100, "", s => s.Saturation, (s, v) => s with { Saturation = v });
        Add("warmth", "Warmth", -100, 100, "", s => s.Warmth, (s, v) => s with { Warmth = v });
        Add("vibrance", "Vibrance", -100, 100, "", s => s.Vibrance, (s, v) => s with { Vibrance = v });
        Add("sharpness", "Sharpness", 0, 100, "%", s => s.Sharpness, (s, v) => s with { Sharpness = v });
        Add("vignette", "Vignette", 0, 100, "%", s => s.Vignette, (s, v) => s with { Vignette = v });
        _getters = new Dictionary<string, Func<AdjustSettings, double>>
        {
            ["exposure"] = s => s.Exposure, ["brightness"] = s => s.Brightness, ["contrast"] = s => s.Contrast, ["saturation"] = s => s.Saturation,
            ["warmth"] = s => s.Warmth, ["vibrance"] = s => s.Vibrance, ["sharpness"] = s => s.Sharpness, ["vignette"] = s => s.Vignette,
        };
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

    private readonly Dictionary<string, Func<AdjustSettings, double>> _getters;

    private UIElement Transforms()
    {
        var row = new UniformGrid { Rows = 1 };
        Button Make(string glyph, string tip, Func<AdjustSettings, AdjustSettings> change)
        {
            var button = ToolKit.Button(glyph, (_, _) => _edits.Current = change(_edits.Current));
            button.FontFamily = new FontFamily("Segoe UI Symbol");
            button.FontSize = 15;
            button.ToolTip = tip;
            button.Margin = new Thickness(0, 0, 8, 0);
            ToolKit.Name(button, tip);
            return button;
        }
        row.Children.Add(Make("↺", "Rotate left", s => s with { QuarterTurns = s.QuarterTurns - 1 }));
        row.Children.Add(Make("↻", "Rotate right", s => s with { QuarterTurns = s.QuarterTurns + 1 }));
        row.Children.Add(Make("⇆", "Flip horizontal", s => s with { FlipsHorizontally = !s.FlipsHorizontally }));
        var last = Make("⇅", "Flip vertical", s => s with { FlipsVertically = !s.FlipsVertically });
        last.Margin = new Thickness(0);
        row.Children.Add(last);
        row.Margin = new Thickness(0, 0, 0, 6);
        return row;
    }

    private void OnChanged()
    {
        var current = _edits.Current;
        foreach (var (key, slider) in _sliders) slider.SetSilently(_getters[key](current));
        RenderPreview(TimeSpan.FromMilliseconds(30));
        Refresh();
    }

    /// <summary>Renders a downscaled preview off the UI thread. Rapid slider changes cancel the previous render.</summary>
    private void RenderPreview(TimeSpan delay)
    {
        _rendering?.Cancel();
        var cancel = _rendering = new CancellationTokenSource();
        var index = _edits.Index;
        var settings = _edits.Current;
        var image = _images[index];
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cancel.Token);
                MagickImage copy;
                lock (_workingLock)
                {
                    _working[index] ??= EditableImage.Scaled(ImageFiles.ReadUpright(image.Path), PreviewLongSide);
                    copy = (MagickImage)_working[index]!.Clone();
                }
                using (copy)
                {
                    AdjustOperation.Apply(copy, settings);
                    cancel.Token.ThrowIfCancellationRequested();
                    var bitmap = MagickWpf.ToBitmap(copy);
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

    protected override string PrimaryTitle => _edits.IsBatch ? _edits.PrimaryTitle("Save") : "Save";
    protected override bool CanApply => _edits.EditedCount > 0;
    protected override bool HasReset => true;
    protected override void ResetEdits() => _edits.Reset();

    protected override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel) =>
        AdjustOperation.Run(_images.Select((image, i) => (image.Path, _edits[i])).ToList(), progress, cancel);

    void IEditorDemo.ApplyDemoState(string state)
    {
        foreach (var token in state.Split(','))
            switch (token)
            {
                case "tuned":
                    _edits.Current = new AdjustSettings { Exposure = 24, Contrast = 30, Saturation = 38, Warmth = 22, Vignette = 40, Sharpness = 25 };
                    break;
                case "rotated":
                    _edits.Current = _edits.Current with { QuarterTurns = 1, FlipsHorizontally = true };
                    break;
                case "second":
                    _edits.Index = Math.Min(1, _images.Count - 1);
                    break;
            }
    }
}

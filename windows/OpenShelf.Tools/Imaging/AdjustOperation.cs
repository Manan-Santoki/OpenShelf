using ImageMagick;

namespace OpenShelf.Tools.Imaging;

/// <summary>Colour and orientation adjustments. Slider values are percentages where 0 means "no change".</summary>
public sealed record AdjustSettings
{
    public double Exposure { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Saturation { get; init; }
    public double Warmth { get; init; }
    public double Vibrance { get; init; }
    public double Sharpness { get; init; }
    public double Vignette { get; init; }
    /// <summary>Clockwise quarter turns.</summary>
    public int QuarterTurns { get; init; }
    public bool FlipsHorizontally { get; init; }
    public bool FlipsVertically { get; init; }

    /// <summary>Whether these settings change the image at all. A slider that reads 0 is no change, and turns and flips that
    /// cancel out leave the image as it was.</summary>
    public bool ChangesImage =>
        new[] { Exposure, Brightness, Contrast, Saturation, Warmth, Vibrance, Sharpness, Vignette }
            .Any(v => Math.Round(v, MidpointRounding.AwayFromZero) != 0) || !KeepsOrientation;

    /// <summary>Whether the turns and flips together leave the image the way up it was.</summary>
    public bool KeepsOrientation
    {
        get
        {
            // One flip on its own always mirrors the image.
            if (FlipsHorizontally != FlipsVertically) return false;
            // Flipping both ways is the same as a half turn.
            var turns = QuarterTurns + (FlipsHorizontally ? 2 : 0);
            return ((turns % 4) + 4) % 4 == 0;
        }
    }
}

/// <summary>Applies <see cref="AdjustSettings"/> with Magick.NET and saves "Name Edited.jpg".</summary>
public static class AdjustOperation
{
    public const string Suffix = "Edited";

    /// <summary>Applies <paramref name="settings"/> to <paramref name="image"/> in place.</summary>
    public static void Apply(IMagickImage<byte> image, AdjustSettings settings)
    {
        MagickWpf.ToSrgb(image);
        if (Rounded(settings.Exposure) != 0)
        {
            // EV in linear light, applied to display values: x * 2^(ev / 2.2).
            var ev = settings.Exposure / 100 * 2;
            image.Evaluate(Channels.RGB, EvaluateOperator.Multiply, Math.Pow(2, ev / 2.2));
        }
        if (Rounded(settings.Brightness) != 0 || Rounded(settings.Contrast) != 0)
            image.BrightnessContrast(new Percentage(settings.Brightness * 0.4), new Percentage(settings.Contrast * 0.5));
        if (Rounded(settings.Saturation) != 0)
            image.Modulate(new Percentage(100), new Percentage(100 + settings.Saturation), new Percentage(100));
        if (Rounded(settings.Warmth) != 0)
        {
            // Warmer pushes red up and blue down.
            var w = settings.Warmth / 100 * 0.18;
            image.Evaluate(Channels.Red, EvaluateOperator.Multiply, 1 + w);
            image.Evaluate(Channels.Blue, EvaluateOperator.Multiply, 1 - w);
        }
        if (Rounded(settings.Vibrance) != 0) Vibrance(image, settings.Vibrance / 100);
        if (Rounded(settings.Sharpness) != 0)
        {
            // Grows with the picture, but capped: a wide kernel on a 24 MP photo takes seconds for no visible gain.
            var sigma = Math.Min(2.0, Math.Max(0.8, Math.Max(image.Width, image.Height) / 1500.0));
            image.UnsharpMask(Math.Ceiling(sigma * 1.5), sigma, settings.Sharpness / 100 * 1.5, 0.02);
        }
        if (Rounded(settings.Vignette) != 0) Vignette(image, settings.Vignette / 100);

        if (settings.FlipsHorizontally) image.Flop();
        if (settings.FlipsVertically) image.Flip();
        var turns = ((settings.QuarterTurns % 4) + 4) % 4;
        if (turns > 0) image.Rotate(90 * turns);
        image.ResetPage();
        ImageFiles.UpdateExifSize(image);
    }

    private static double Rounded(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Boosts muted colours more than saturated ones.</summary>
    private static void Vibrance(IMagickImage<byte> image, double amount)
    {
        MapPixels(image, (data, channels, width, height) =>
        {
            for (var i = 0; i < data.Length; i += channels)
            {
                double r = data[i], g = data[i + 1], b = data[i + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var sat = max <= 0 ? 0 : (max - min) / max;
                var gain = 1 + amount * (1 - sat) * 1.2;
                var grey = (r + g + b) / 3;
                data[i] = Clamp(grey + (r - grey) * gain);
                data[i + 1] = Clamp(grey + (g - grey) * gain);
                data[i + 2] = Clamp(grey + (b - grey) * gain);
            }
        });
    }

    /// <summary>Darkens towards the corners.</summary>
    private static void Vignette(IMagickImage<byte> image, double amount)
    {
        MapPixels(image, (data, channels, width, height) =>
        {
            double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
            var radius = Math.Max(width, height) * 0.7;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / radius;
                var t = Math.Clamp((d - 0.35) / 0.65, 0, 1);
                var factor = 1 - amount * t * t * (3 - 2 * t);
                var i = (y * width + x) * channels;
                data[i] = Clamp(data[i] * factor);
                data[i + 1] = Clamp(data[i + 1] * factor);
                data[i + 2] = Clamp(data[i + 2] * factor);
            }
        });
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>Gives <paramref name="map"/> the image's pixels (R, G, B[, A] per pixel) to change in place.</summary>
    internal static void MapPixels(IMagickImage<byte> image, Action<byte[], int, int, int> map)
    {
        using var pixels = image.GetPixels();
        var channels = (int)pixels.Channels;
        if (channels < 3) return;
        var width = (int)image.Width;
        var height = (int)image.Height;
        var data = pixels.GetArea(0, 0, image.Width, image.Height);
        if (data is null) return;
        map(data, channels, width, height);
        pixels.SetArea(0, 0, image.Width, image.Height, data);
    }

    /// <summary>Saves <paramref name="path"/> with <paramref name="settings"/> applied as "Name Edited".</summary>
    public static string Save(string path, AdjustSettings settings)
    {
        using var image = ImageFiles.ReadUpright(path);
        Apply(image, settings);
        return ImageFiles.SaveNext(image, path, Suffix);
    }

    /// <summary>Renders a downscaled preview (long side at most <paramref name="maxDimension"/>) of an already-upright image.</summary>
    public static MagickImage Preview(IMagickImage<byte> upright, AdjustSettings settings, int maxDimension = 1000)
    {
        var copy = EditableImage.Scaled(upright, maxDimension);
        Apply(copy, settings);
        return copy;
    }

    /// <summary>Adjusts and saves each image; images whose settings change nothing are left alone and counted.</summary>
    public static SaveOutcome Run(IReadOnlyList<(string Path, AdjustSettings Settings)> jobs, IProgress<double>? progress = null, CancellationToken cancellation = default) =>
        Batch.Run(jobs, new SaveOutcome { SavedLabel = "edited", UnchangedLabel = "not edited" },
            job => job.Settings.ChangesImage ? Save(job.Path, job.Settings) : null, progress, cancellation);
}

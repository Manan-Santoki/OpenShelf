namespace OpenShelf.Tools.Media;

/// <summary>A turn or mirror applied to a whole video.</summary>
public enum VideoRotation
{
    /// <summary>90 degrees anticlockwise.</summary>
    Left,
    /// <summary>90 degrees clockwise.</summary>
    Right,
    /// <summary>180 degrees.</summary>
    UpsideDown,
    /// <summary>A mirror, left to right. Rewrites every frame.</summary>
    FlipHorizontal,
}

public static class VideoRotationExtensions
{
    /// <summary>The label the editor shows.</summary>
    public static string Title(this VideoRotation rotation) => rotation switch
    {
        VideoRotation.Left => "Left",
        VideoRotation.Right => "Right",
        VideoRotation.UpsideDown => "180°",
        _ => "Flip",
    };

    /// <summary>Degrees clockwise on screen; 0 for a mirror.</summary>
    public static int Degrees(this VideoRotation rotation) => rotation switch
    {
        VideoRotation.Left => -90,
        VideoRotation.Right => 90,
        VideoRotation.UpsideDown => 180,
        _ => 0,
    };

    /// <summary>Turns only change the angle players show the frames at, so they need no re-encoding; a mirror rewrites every frame.</summary>
    public static bool IsLossless(this VideoRotation rotation) => rotation != VideoRotation.FlipHorizontal;
}

/// <summary>A rectangle in pixels of the upright frame (as players show it), origin top left.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>A crop as fractions of the frame (0 to 1, origin top left), so "the top half" stays the top half on videos of any size.</summary>
public readonly record struct CropFraction(double X, double Y, double Width, double Height)
{
    /// <summary>The whole frame: cropping to it would only re-encode the video.</summary>
    public static CropFraction Full { get; } = new(0, 0, 1, 1);

    /// <summary>True when (to within a thousandth) nothing is cut away.</summary>
    public bool IsFullFrame => X <= 0.001 && Y <= 0.001 && Width >= 0.998 && Height >= 0.998;

    /// <summary>The crop for a rectangle of a frame <paramref name="width"/> by <paramref name="height"/> pixels.</summary>
    public static CropFraction FromPixels(PixelRect rect, int width, int height) =>
        width <= 0 || height <= 0 ? Full : new((double)rect.X / width, (double)rect.Y / height, (double)rect.Width / width, (double)rect.Height / height);

    /// <summary>The pixel rectangle in a frame <paramref name="width"/> by <paramref name="height"/>: rounded, inside the frame, with even
    /// sides as video encoders need.</summary>
    public PixelRect ToPixels(int width, int height) =>
        CropMath.Even(new PixelRect(
            (int)Math.Round(X * width), (int)Math.Round(Y * height),
            (int)Math.Round(Width * width), (int)Math.Round(Height * height)), width, height);
}

public static class CropMath
{
    /// <summary>Keeps <paramref name="rect"/> inside a frame of <paramref name="width"/> by <paramref name="height"/> and makes its sides
    /// even (at least 2), as video encoders need.</summary>
    public static PixelRect Even(PixelRect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(width - 2, 0));
        var y = Math.Clamp(rect.Y, 0, Math.Max(height - 2, 0));
        var w = Math.Max(2, Math.Min(rect.Width, width - x) / 2 * 2);
        var h = Math.Max(2, Math.Min(rect.Height, height - y) / 2 * 2);
        return new PixelRect(x, y, w, h);
    }

    /// <summary>Video encoders need even dimensions.</summary>
    public static (int Width, int Height) EvenSize(int width, int height) => (Math.Max(2, width / 2 * 2), Math.Max(2, height / 2 * 2));
}

/// <summary>Containers and extensions, as the Mac tools choose them.</summary>
public static class MediaFormats
{
    /// <summary>Speed is limited to 0.25x-4x.</summary>
    public const double MinimumSpeed = 0.25;
    public const double MaximumSpeed = 4;

    /// <summary>Audio extensions whose streams can be cut and joined without re-encoding. (The Mac keeps only m4a, wav, aiff and caf,
    /// because AVFoundation writes nothing else; ffmpeg copies the rest as well.)</summary>
    public static readonly IReadOnlySet<string> CopyableAudio = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "m4a", "aac", "mp3", "wav", "wave", "aif", "aiff", "caf", "flac", "ogg", "oga", "opus", "mka" };

    /// <summary>Keeps MP4, M4V and MOV; anything else becomes MOV (as on the Mac).</summary>
    public static string VideoContainerExtension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
    {
        "mp4" => "mp4",
        "m4v" => "m4v",
        _ => "mov",
    };

    /// <summary>The extension a cut of <paramref name="path"/> is saved with: the video container, or for sound the file's own
    /// kind when it can be copied, else m4a.</summary>
    public static string OutputExtension(string path, bool hasVideo)
    {
        if (hasVideo) return VideoContainerExtension(path);
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return CopyableAudio.Contains(extension) ? extension : "m4a";
    }

    /// <summary>"2x", "0.5x", "1.25x".</summary>
    public static string SpeedSuffix(double rate) =>
        (Math.Round(rate * 100, MidpointRounding.AwayFromZero) / 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";
}

using ImageMagick;

namespace OpenShelf.Tools.Imaging;

/// <summary>Image operations shared by several tools.</summary>
public static class ImageOps
{
    /// <summary>A Gaussian blur that stays fast at large radii: soft blurs are done on a shrunken copy and scaled back, which looks the same
    /// (a Gaussian of sigma 40 has no detail finer than a few pixels) and takes a fraction of the time.</summary>
    public static void SoftBlur(IMagickImage<byte> image, double sigma)
    {
        if (sigma <= 0) return;
        var factor = (int)Math.Floor(sigma / 3);
        if (factor < 2)
        {
            image.GaussianBlur(0, sigma);
            return;
        }
        var width = image.Width;
        var height = image.Height;
        var smallWidth = (uint)Math.Max(1, (width + factor - 1) / factor);
        var smallHeight = (uint)Math.Max(1, (height + factor - 1) / factor);
        image.FilterType = FilterType.Triangle;
        image.Resize(new MagickGeometry(smallWidth, smallHeight) { IgnoreAspectRatio = true });
        image.GaussianBlur(0, sigma / factor);
        image.FilterType = FilterType.Cubic;
        image.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
    }
}

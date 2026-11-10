using ImageMagick;

namespace OpenShelf.Convert;

/// <summary>Image conversions with the bundled ImageMagick (Magick.NET): PNG, JPG, WEBP, HEIC, TIFF, AVIF, BMP and GIF in and
/// out, SVG and ICO and friends in, plus PDF and DOCX out. Port of ImageConversion.swift and EditableImage.
/// <para>Rules from the Mac: pictures are turned upright (EXIF orientation applied, then reset); formats without
/// transparency (JPG, BMP, PDF) get the picture flattened onto white; colour profiles are always kept; with "keep
/// metadata" off, everything else (EXIF, XMP, comments) is stripped; quality is automatic (JPEG, HEIC, AVIF 0.9, WebP 88)
/// unless the options say otherwise, and PNG, TIFF, BMP and GIF ignore it.</para></summary>
public sealed class ImageEngine : IConversionEngine
{
    /// <summary>Formats written from images.</summary>
    public static readonly IReadOnlyList<OutputFormat> Outputs =
        [OutputFormat.Png, OutputFormat.Jpg, OutputFormat.Webp, OutputFormat.Heic, OutputFormat.Tiff, OutputFormat.Avif,
         OutputFormat.Bmp, OutputFormat.Gif, OutputFormat.Pdf, OutputFormat.Docx];

    public const double AutomaticQuality = 0.9;
    public const double AutomaticWebPQuality = 0.88;
    public const double AutomaticPdfQuality = 0.92;

    /// <summary>Whether the bundled ImageMagick build can write <paramref name="format"/> (asked once; no file is touched).</summary>
    public static bool CanWrite(OutputFormat format)
    {
        var magick = FormatOf(format);
        return MagickNET.SupportedFormats.Any(info => info.Format == magick && info.SupportsWriting);
    }

    public bool CanConvert(FileKind kind, OutputFormat format) => kind == FileKind.Image && Outputs.Contains(format);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (request.Format.FileExtension() is not { } extension)
            throw ConversionException.Unsupported($"image to {request.Format.Title()}");
        var destination = request.OutputPath($"{request.SourceStem}.{extension}");
        progress?.Report(0.05);
        try
        {
            await Task.Run(() => Write(request, destination, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
        progress?.Report(1);
        return [destination];
    }

    private static void Write(ConversionRequest request, string destination, CancellationToken cancellationToken)
    {
        var source = request.SourcePath;
        var options = request.Options;
        var format = request.Format;
        try
        {
            using var frames = new MagickImageCollection();
            frames.Read(source);
            if (frames.Count == 0) throw ConversionException.Unreadable(source);
            cancellationToken.ThrowIfCancellationRequested();

            // Animations survive between the animated formats; everything else converts the first picture.
            var animated = frames.Count > 1 && format is OutputFormat.Gif or OutputFormat.Webp
                && frames[0].Format is MagickFormat.Gif or MagickFormat.Gif87 or MagickFormat.WebP;
            if (animated)
            {
                frames.Coalesce();
                foreach (var frame in frames)
                {
                    Prepare(frame, format, options);
                    Apply(frame, format, options);
                }
                frames.Write(destination, FormatOf(format));
                return;
            }

            using var image = (MagickImage)frames[0].Clone();
            Prepare(image, format, options);
            if (format == OutputFormat.Docx)
            {
                var (png, width, height) = ToPng(image);
                DocxWriter.Write([new DocxImage(png, width, height)], destination);
                return;
            }
            Apply(image, format, options);
            image.Write(destination, FormatOf(format));
        }
        catch (MagickMissingDelegateErrorException)
        {
            throw ConversionException.Failed($"This version of OpenShelf can’t write {format.Title()} files.");
        }
        catch (MagickException e) when (e is not MagickMissingDelegateErrorException && e.Message.Contains("delegate", StringComparison.OrdinalIgnoreCase))
        {
            throw ConversionException.Failed($"“{Path.GetFileName(source)}” is in a format OpenShelf can’t read.");
        }
        catch (MagickCorruptImageErrorException)
        {
            throw ConversionException.Failed($"“{Path.GetFileName(source)}” is damaged or in a format OpenShelf can’t read.");
        }
        catch (MagickException e)
        {
            throw ConversionException.Failed($"“{Path.GetFileName(source)}” couldn’t be converted: {e.Message}");
        }
    }

    /// <summary>Upright pixels, flattened where the format has no transparency, metadata per the options.</summary>
    private static void Prepare(IMagickImage<byte> image, OutputFormat format, ConversionOptions options)
    {
        image.AutoOrient();
        if (format is OutputFormat.Jpg or OutputFormat.Bmp or OutputFormat.Pdf && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
            image.Alpha(AlphaAction.Off);
        }
        if (!options.KeepMetadata)
        {
            // Strip removes colour profiles too; the picture would look different without its own.
            var colour = image.GetColorProfile();
            image.Strip();
            if (colour is not null) image.SetProfile(colour);
        }
    }

    /// <summary>Format-specific encoder settings.</summary>
    private static void Apply(IMagickImage<byte> image, OutputFormat format, ConversionOptions options)
    {
        switch (format)
        {
            case OutputFormat.Jpg or OutputFormat.Heic or OutputFormat.Avif:
                image.Quality = Percent(options.Quality(AutomaticQuality));
                break;
            case OutputFormat.Webp:
                image.Quality = Percent(options.Quality(AutomaticWebPQuality));
                break;
            case OutputFormat.Pdf:
                // A page the size of the picture at its stated resolution.
                var density = image.Density;
                var dpi = density.X;
                if (density.Units == DensityUnit.PixelsPerCentimeter) dpi *= 2.54;
                if (density.Units == DensityUnit.Undefined || dpi < 1) dpi = 72;
                image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
                image.Quality = Percent(options.Quality(AutomaticPdfQuality));
                break;
        }
    }

    private static uint Percent(double quality) => (uint)Math.Clamp((int)Math.Round(quality * 100), 1, 100);

    private static MagickFormat FormatOf(OutputFormat format) => format switch
    {
        OutputFormat.Png => MagickFormat.Png,
        OutputFormat.Jpg => MagickFormat.Jpeg,
        OutputFormat.Webp => MagickFormat.WebP,
        OutputFormat.Heic => MagickFormat.Heic,
        OutputFormat.Tiff => MagickFormat.Tiff,
        OutputFormat.Avif => MagickFormat.Avif,
        OutputFormat.Bmp => MagickFormat.Bmp3,
        OutputFormat.Gif => MagickFormat.Gif,
        OutputFormat.Pdf => MagickFormat.Pdf,
        _ => throw ConversionException.Unsupported($"image to {format.Title()}"),
    };

    private static (byte[] Png, int Width, int Height) ToPng(IMagickImage<byte> image)
    {
        using var stream = new MemoryStream();
        image.Write(stream, MagickFormat.Png);
        return (stream.ToArray(), (int)image.Width, (int)image.Height);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

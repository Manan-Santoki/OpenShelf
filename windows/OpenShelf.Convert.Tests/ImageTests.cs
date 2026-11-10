using System.IO.Compression;
using ImageMagick;
using OpenShelf.Convert;
using static OpenShelf.Convert.OutputFormat;

namespace OpenShelf.Convert.Tests;

public class ImageTests
{
    /// <summary>A 64x48 picture: blue top-left quarter on a red field, optionally see-through.</summary>
    private static string MakeImage(TempFolder temp, string name, bool transparent = false, Action<MagickImage>? tweak = null)
    {
        using var image = new MagickImage(transparent ? MagickColors.Transparent : MagickColors.Red, 64, 48);
        using (var blue = new MagickImage(MagickColors.Blue, 32, 24)) image.Composite(blue, 0, 0, CompositeOperator.Over);
        tweak?.Invoke(image);
        var path = temp.Combine(name);
        image.Write(path);
        return path;
    }

    private static async Task<string> Convert(string source, OutputFormat format, ConversionOptions? options = null) =>
        (await Converter.Default.ConvertAsync(new ConversionRequest(source, format, options ?? ConversionOptions.Automatic)))[0];

    public static TheoryData<OutputFormat> RasterFormats => new() { Png, Jpg, Webp, Heic, Tiff, Avif, Bmp, Gif };

    /// <summary>Every image format writes a file this machine can read back at the same size, and converts back to PNG.</summary>
    [Theory]
    [MemberData(nameof(RasterFormats))]
    public async Task EveryFormatRoundTrips(OutputFormat format)
    {
        using var temp = new TempFolder();
        var source = MakeImage(temp, "Source.png");
        if (!ImageEngine.CanWrite(format))
        {
            // This ImageMagick build has no encoder for the format: the wheel hides it, and asking anyway says so plainly.
            Assert.False(ExternalTools.Detect(ToolLocations.Standard).CanEncodeHeic && format == Heic);
            var refusal = await Assert.ThrowsAsync<ConversionException>(() => Convert(source, format));
            Assert.Contains("can’t write", refusal.Message);
            return;
        }
        var output = await Convert(source, format);
        Assert.Equal(format == Png ? "Source 2.png" : "Source." + format.FileExtension(), Path.GetFileName(output)); // never over the original
        using (var written = new MagickImage(output))
        {
            Assert.Equal(64u, written.Width);
            Assert.Equal(48u, written.Height);
        }
        var back = await Convert(output, Png);
        using var png = new MagickImage(back);
        Assert.Equal(64u, png.Width);
        Assert.Equal(48u, png.Height);
        // The blue corner survives even the lossy formats.
        var pixel = png.GetPixels().GetPixel(8, 8).ToColor()!;
        Assert.True(pixel.B > 150 && pixel.R < 100, $"{format}: corner is {pixel}");
        var far = png.GetPixels().GetPixel(56, 40).ToColor()!;
        Assert.True(far.R > 150 && far.B < 100, $"{format}: far corner is {far}");
    }

    [Theory]
    [InlineData("svg")]
    public async Task SvgIsReadAndRasterised(string extension)
    {
        using var temp = new TempFolder();
        var svg = temp.File("Logo." + extension,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"40\"><rect width=\"80\" height=\"40\" fill=\"#00ff00\"/></svg>");
        using var png = new MagickImage(await Convert(svg, Png));
        Assert.Equal(80u, png.Width);
        Assert.Equal(40u, png.Height);
        Assert.True(png.GetPixels().GetPixel(40, 20).ToColor()!.G > 200);
    }

    [Fact]
    public async Task WritesAPdfTheSizeOfThePicture()
    {
        using var temp = new TempFolder();
        var pdf = await Convert(MakeImage(temp, "Page.png"), Pdf);
        var bytes = File.ReadAllBytes(pdf);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Matches(@"/MediaBox\s*\[\s*0\s+0\s+64\s+48\s*\]", text);
    }

    [Fact]
    public async Task PdfHonoursTheStatedResolution()
    {
        using var temp = new TempFolder();
        var source = MakeImage(temp, "Scan.png", tweak: i => i.Density = new Density(144, 144, DensityUnit.PixelsPerInch));
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(await Convert(source, Pdf)));
        var box = System.Text.RegularExpressions.Regex.Match(text, @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)").Groups;
        Assert.InRange(double.Parse(box[1].Value, System.Globalization.CultureInfo.InvariantCulture), 31.9, 32.1);
        Assert.InRange(double.Parse(box[2].Value, System.Globalization.CultureInfo.InvariantCulture), 23.9, 24.1);
    }

    [Fact]
    public async Task WritesADocxWithThePictureOnThePage()
    {
        using var temp = new TempFolder();
        var docx = await Convert(MakeImage(temp, "Doc.png"), Docx);
        using var zip = ZipFile.OpenRead(docx);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("word/media/image1.png"));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        // 64x48 px is below the page, so it keeps its 96-dpi size: 9525 EMU a pixel.
        Assert.Contains("cx=\"609600\" cy=\"457200\"", xml);
        System.Xml.Linq.XDocument.Parse(xml);
        using var embedded = new MagickImage(zip.GetEntry("word/media/image1.png")!.Open());
        Assert.Equal(64u, embedded.Width);
    }

    [Fact]
    public async Task BigPicturesAreScaledToThePage()
    {
        using var temp = new TempFolder();
        using var big = new MagickImage(MagickColors.Green, 3000, 3000);
        big.Write(temp.Combine("Big.png"));
        using var zip = ZipFile.OpenRead(await Convert(temp.Combine("Big.png"), Docx));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        // Limited by the 9.5 in height: 8686800 EMU square.
        Assert.Contains("cx=\"6400800\" cy=\"6400800\"", reader.ReadToEnd());
    }

    [Fact]
    public async Task TransparencyIsFlattenedOntoWhiteWhereTheFormatHasNone()
    {
        using var temp = new TempFolder();
        var source = MakeImage(temp, "Clear.png", transparent: true);
        foreach (var format in new[] { Jpg, Bmp })
        {
            using var flat = new MagickImage(await Convert(source, format));
            var pixel = flat.GetPixels().GetPixel(60, 44).ToColor()!;
            Assert.True(pixel.R > 240 && pixel.G > 240 && pixel.B > 240, $"{format}: {pixel}");
        }
        using var kept = new MagickImage(await Convert(source, Webp));
        Assert.True(kept.HasAlpha);
        using var png = new MagickImage(await Convert(source, Png));
        Assert.Equal(0, png.GetPixels().GetPixel(60, 44).ToColor()!.A);
    }

    [Fact]
    public async Task OrientationIsAppliedAndReset()
    {
        using var temp = new TempFolder();
        var source = MakeImage(temp, "Sideways.jpg", tweak: i => { var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)6); i.SetProfile(exif); i.Orientation = Orientation.RightTop; });
        using (var check = new MagickImage(source)) Assert.Equal(Orientation.RightTop, check.Orientation);
        foreach (var format in new[] { Png, Jpg })
        {
            using var upright = new MagickImage(await Convert(source, format));
            Assert.Equal(48u, upright.Width);
            Assert.Equal(64u, upright.Height);
            Assert.True(upright.Orientation is Orientation.TopLeft or Orientation.Undefined);
        }
    }

    [Fact]
    public async Task QualityMakesSmallerJpegsAndAutomaticIsNinety()
    {
        using var temp = new TempFolder();
        using var noisy = new MagickImage("plasma:fractal", 400, 300);
        noisy.Write(temp.Combine("Noise.png"));
        var source = temp.Combine("Noise.png");
        var automatic = await Convert(source, Jpg);
        var low = await Convert(source, Jpg, new ConversionOptions { ImageQuality = 0.4 });
        Assert.True(new FileInfo(low).Length < new FileInfo(automatic).Length);
        using var written = new MagickImage(automatic);
        Assert.InRange(written.Quality, 88u, 92u);
        using var lowImage = new MagickImage(low);
        Assert.InRange(lowImage.Quality, 38u, 42u);
        // Lossless formats ignore quality.
        Assert.True(File.Exists(await Convert(source, Png, new ConversionOptions { ImageQuality = 0.4 })));
    }

    [Fact]
    public async Task MetadataIsKeptOrStrippedButColourProfilesStay()
    {
        using var temp = new TempFolder();
        var source = MakeImage(temp, "Camera.jpg", tweak: image =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "OpenShelf Cam");
            image.SetProfile(exif);
            image.SetProfile(ColorProfiles.SRGB);
        });
        using (var check = new MagickImage(source)) Assert.NotNull(check.GetExifProfile());

        using var kept = new MagickImage(await Convert(source, Jpg));
        Assert.Equal("OpenShelf Cam", kept.GetExifProfile()!.GetValue(ExifTag.Make)!.Value);
        Assert.NotNull(kept.GetColorProfile());

        using var stripped = new MagickImage(await Convert(source, Jpg, new ConversionOptions { KeepMetadata = false }));
        Assert.Null(stripped.GetExifProfile());
        Assert.NotNull(stripped.GetColorProfile());
    }

    [Fact]
    public async Task AnimatedGifStaysAnimatedAndOtherFormatsTakeTheFirstFrame()
    {
        using var temp = new TempFolder();
        using (var frames = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
            {
                var frame = new MagickImage(color, 16, 16) { AnimationDelay = 5 };
                frames.Add(frame);
            }
            frames.Write(temp.Combine("Anim.gif"));
        }
        using var gif = new MagickImageCollection(await Convert(temp.Combine("Anim.gif"), Gif));
        Assert.Equal(3, gif.Count);
        using var png = new MagickImageCollection(await Convert(temp.Combine("Anim.gif"), Png));
        Assert.Single(png);
    }

    [Fact]
    public async Task UnreadableImagesGetAPlainMessage()
    {
        using var temp = new TempFolder();
        var error = await Assert.ThrowsAsync<ConversionException>(() => Convert(temp.File("Broken.png", "this is not a png"), Jpg));
        Assert.Contains("Broken.png", error.Message);
        Assert.Equal(["Broken.png"], temp.Names());
    }

    [Fact]
    public void BundledCodecsAreWhatWeShip()
    {
        static bool Has(MagickFormat f, bool write) => MagickNET.SupportedFormats.Any(i => i.Format == f && (write ? i.SupportsWriting : i.SupportsReading));
        Assert.True(Has(MagickFormat.Heic, write: false), "HEIC must at least be readable");
        Assert.True(Has(MagickFormat.Avif, write: true));
        Assert.True(Has(MagickFormat.WebP, write: true));
        // Magick.NET-Q8-AnyCPU 15 reads HEIC but has no HEIC encoder, so the wheel must not offer it.
        var tools = ExternalTools.Detect(ToolLocations.Standard);
        Assert.Equal(Has(MagickFormat.Heic, write: true), tools.CanEncodeHeic);
        Assert.Equal(tools.CanEncodeHeic, ConversionCatalog.Formats([@"C:\x\a.png"], tools).Contains(Heic));
        Assert.DoesNotContain(Heic, ConversionCatalog.Formats([@"C:\x\a.png"], ExternalTools.None with { CanEncodeHeic = false }));
    }

    [Fact]
    public void ImageEngineClaimsOnlyImages()
    {
        var engine = new ImageEngine();
        Assert.True(engine.CanConvert(FileKind.Image, Heic));
        Assert.False(engine.CanConvert(FileKind.Pdf, Png));
        Assert.False(engine.CanConvert(FileKind.Image, Mp4));
    }
}

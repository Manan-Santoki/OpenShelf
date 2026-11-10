using ImageMagick;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Tests;

public class ImageFilesTests
{
    [Fact]
    public void Loading_gives_upright_size_a_preview_and_a_thumbnail()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Big.jpg", 3200, 1600);
        var image = EditableImage.Load(path);
        Assert.Equal(3200, image.Width);
        Assert.Equal(1600, image.Height);
        Assert.Equal("Big.jpg", image.Name);
        Assert.Equal(1600, image.Preview.PixelWidth);
        Assert.Equal(800, image.Preview.PixelHeight);
        Assert.Equal(160, image.Thumbnail.PixelWidth);
        Assert.True(image.Preview.IsFrozen);
        Assert.False(image.IsTransparent);
        Assert.Equal(MagickFormat.Jpeg, image.Format);
        Assert.True(image.FileBytes > 0);
    }

    [Fact]
    public void A_picture_stored_sideways_loads_upright()
    {
        using var temp = new TempFolder();
        var image = EditableImage.Load(temp.Image("Turned.jpg", 200, 100, tweak: TestImages.Turned));
        Assert.Equal(100, image.Width);
        Assert.Equal(200, image.Height);
    }

    [Fact]
    public void Transparency_is_judged_from_the_pixels_not_the_channel()
    {
        using var temp = new TempFolder();
        var opaqueWithAlpha = temp.Image("Opaque.png", 40, 40, tweak: i => i.Alpha(AlphaAction.Set));
        var clear = temp.Image("Clear.png", 40, 40, tweak: TestImages.HalfClear);
        var a = EditableImage.Load(opaqueWithAlpha);
        var b = EditableImage.Load(clear);
        Assert.False(a.IsTransparent);
        Assert.True(b.IsTransparent);
    }

    [Fact]
    public void Animated_gifs_are_noticed_and_only_the_first_frame_is_edited()
    {
        using var temp = new TempFolder();
        var path = temp.File("Anim.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 40, 30));
            frames.Add(new MagickImage(MagickColors.Blue, 40, 30));
            frames.Write(path, MagickFormat.Gif);
        }
        var image = EditableImage.Load(path);
        Assert.True(image.IsAnimated);
        Assert.Equal(40, image.Width);
        using var upright = ImageFiles.ReadUpright(path);
        Assert.Equal(40u, upright.Width);
    }

    [Fact]
    public void Unreadable_files_say_so()
    {
        using var temp = new TempFolder();
        var path = temp.File("broken.png");
        File.WriteAllText(path, "nope");
        var error = Assert.Throws<IOException>(() => EditableImage.Load(path));
        Assert.Contains("couldn’t be opened", error.Message);
    }

    [Theory]
    [InlineData(MagickFormat.Jpg, MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Pjpeg, MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Png32, MagickFormat.Png)]
    [InlineData(MagickFormat.Tif, MagickFormat.Tiff)]
    [InlineData(MagickFormat.Bmp3, MagickFormat.Bmp)]
    [InlineData(MagickFormat.Gif87, MagickFormat.Gif)]
    public void Format_variants_collapse_to_their_family(MagickFormat input, MagickFormat family) =>
        Assert.Equal(family, ImageFiles.Family(input));

    [Fact]
    public void Copies_keep_the_original_format_when_it_can_be_written_else_png()
    {
        Assert.Equal(MagickFormat.Jpeg, ImageFiles.OutputFormat(MagickFormat.Jpg));
        Assert.Equal(MagickFormat.Png, ImageFiles.OutputFormat(MagickFormat.Png));
        Assert.Equal(MagickFormat.Png, ImageFiles.OutputFormat(MagickFormat.Svg));
        Assert.Equal(MagickFormat.Png, ImageFiles.OutputFormat(MagickFormat.Unknown));
    }

    [Fact]
    public void Extensions_keep_the_originals_spelling_when_the_format_is_unchanged()
    {
        Assert.Equal("jpeg", ImageFiles.Extension("a.jpeg", MagickFormat.Jpeg, MagickFormat.Jpeg));
        Assert.Equal("JPG", ImageFiles.Extension("a.JPG", MagickFormat.Jpeg, MagickFormat.Jpeg));
        Assert.Equal("jpg", ImageFiles.Extension("a.png", MagickFormat.Png, MagickFormat.Jpeg));
        Assert.Equal("png", ImageFiles.Extension("a.svg", MagickFormat.Svg, MagickFormat.Png));
    }

    [Fact]
    public void Saving_a_transparent_image_as_jpeg_flattens_it_on_white()
    {
        using var temp = new TempFolder();
        var path = temp.File("Clear.png");
        using (var clear = new MagickImage(MagickColors.Transparent, 20, 20)) clear.Write(path, MagickFormat.Png);
        using var image = ImageFiles.ReadUpright(path);
        var saved = ImageFiles.SaveNext(image, path, "Flat", MagickFormat.Jpeg);
        Assert.Equal(temp.File("Clear Flat.jpg"), saved);
        Assert.True(Pixels.IsNear(Pixels.At(saved, 10, 10), 255, 255, 255, 4));
    }

    [Fact]
    public void An_existing_name_is_never_reused()
    {
        using var temp = new TempFolder();
        var path = temp.Image("a.png", 10, 10);
        using var image = ImageFiles.ReadUpright(path);
        var first = ImageFiles.SaveNext(image, path, "Copy");
        File.WriteAllText(temp.File("a Copy 2.png"), "taken");
        var second = ImageFiles.SaveNext(image, path, "Copy");
        Assert.Equal(temp.File("a Copy.png"), first);
        Assert.Equal(temp.File("a Copy 3.png"), second);
        Assert.Equal("taken", File.ReadAllText(temp.File("a Copy 2.png")));
    }

    [Fact]
    public void A_missing_folder_is_reported_not_thrown_as_a_raw_error()
    {
        using var temp = new TempFolder();
        var path = temp.Image("a.png", 10, 10);
        using var image = ImageFiles.ReadUpright(path);
        var gone = Path.Combine(temp.Path, "gone", "a.png");
        var error = Assert.Throws<SaveException>(() => ImageFiles.SaveNext(image, gone, "Copy", MagickFormat.Png, sourceFormat: MagickFormat.Png));
        Assert.Equal(SaveException.Reason.FolderMissing, error.Kind);
    }

    [Fact]
    public void An_svg_is_drawn_at_a_useful_size()
    {
        using var temp = new TempFolder();
        var path = temp.File("Logo.svg");
        File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"><rect width=\"100\" height=\"50\" fill=\"#ff0000\"/></svg>");
        using var image = ImageFiles.ReadUpright(path);
        Assert.True(image.Width >= 1000, $"{image.Width}");
    }

    [Fact]
    public void Bitmaps_round_trip_between_magick_and_wpf()
    {
        using var image = new MagickImage(new MagickColor("#336699"), 12, 8);
        var bitmap = MagickWpf.ToBitmap(image);
        Assert.Equal(12, bitmap.PixelWidth);
        using var back = MagickWpf.FromBitmap(bitmap);
        Assert.Equal(12u, back.Width);
        using var pixels = back.GetPixels();
        Assert.True(Pixels.IsNear(pixels.GetPixel(3, 3).ToColor()!, 0x33, 0x66, 0x99, 2));
    }
}

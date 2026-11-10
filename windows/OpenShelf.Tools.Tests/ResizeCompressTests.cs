using System.Windows;
using ImageMagick;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Tests;

public class ResizeTests
{
    [Fact]
    public void Percent_scales_both_sides()
    {
        var rule = new ResizeRule { Mode = ResizeBy.Percent, Percent = 50 };
        Assert.Equal(new Size(500, 250), rule.TargetSize(new Size(1000, 500)));
    }

    [Fact]
    public void Long_side_scales_to_the_given_edge()
    {
        var rule = new ResizeRule { Mode = ResizeBy.LongEdge, LongEdge = 1080 };
        Assert.Equal(new Size(1080, 720), rule.TargetSize(new Size(4000, 2667)));
        Assert.Equal(new Size(607, 1080), rule.TargetSize(new Size(2667, 4743)));
    }

    [Fact]
    public void Exact_with_one_side_follows_the_proportion()
    {
        Assert.Equal(new Size(400, 200), new ResizeRule { Mode = ResizeBy.Exact, Width = 400 }.TargetSize(new Size(800, 400)));
        Assert.Equal(new Size(300, 150), new ResizeRule { Mode = ResizeBy.Exact, Height = 150 }.TargetSize(new Size(600, 300)));
        Assert.Equal(new Size(100, 100), new ResizeRule { Mode = ResizeBy.Exact, Width = 100, Height = 100 }.TargetSize(new Size(800, 400)));
    }

    [Fact]
    public void It_never_enlarges_unless_allowed()
    {
        var rule = new ResizeRule { Mode = ResizeBy.LongEdge, LongEdge = 3840 };
        Assert.Equal(new Size(800, 400), rule.TargetSize(new Size(800, 400)));
        Assert.False(rule.Changes(new Size(800, 400)));
        var allowed = rule with { AllowsEnlarging = true };
        Assert.Equal(new Size(3840, 1920), allowed.TargetSize(new Size(800, 400)));
        Assert.Equal("already smaller", rule.UnchangedLabel);
        Assert.Equal("already that size", allowed.UnchangedLabel);
    }

    [Fact]
    public void Exact_larger_in_one_side_is_scaled_back_in_proportion()
    {
        var rule = new ResizeRule { Mode = ResizeBy.Exact, Width = 2000, Height = 100 };
        var target = rule.TargetSize(new Size(1000, 500));
        Assert.True(target.Width <= 1000 && target.Height <= 500);
    }

    [Fact]
    public void Resize_saves_a_resized_copy_in_the_same_format()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Photo.jpg", 400, 200);
        var outcome = ResizeOperation.Run([(path, new Size(400, 200))], new ResizeRule { Mode = ResizeBy.Percent, Percent = 50 });
        Assert.Single(outcome.Saved);
        Assert.Equal(temp.File("Photo Resized.jpg"), outcome.Saved[0]);
        Assert.Equal((200u, 100u), Pixels.SizeOf(outcome.Saved[0]));
        Assert.Equal(MagickFormat.Jpeg, ImageFiles.FormatOf(outcome.Saved[0]));
    }

    [Fact]
    public void Resize_keeps_transparency()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Logo.png", 100, 100, tweak: TestImages.HalfClear);
        var saved = ResizeOperation.Save(path, new Size(50, 50));
        using var back = new MagickImage(saved);
        Assert.True(back.HasAlpha);
        Assert.True(ImageFiles.IsTransparent(back));
    }

    [Fact]
    public void Images_that_would_not_change_are_counted()
    {
        using var temp = new TempFolder();
        var small = temp.Image("small.png", 100, 50);
        var big = temp.Image("big.png", 1000, 500);
        var rule = new ResizeRule { Mode = ResizeBy.LongEdge, LongEdge = 500 };
        var outcome = ResizeOperation.Run([(small, new Size(100, 50)), (big, new Size(1000, 500))], rule);
        Assert.Equal("1 resized · 1 already smaller", outcome.Title);
        Assert.Equal((500u, 250u), Pixels.SizeOf(outcome.Saved[0]));
    }

    [Fact]
    public void Resize_updates_the_pixel_size_exif_records()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Photo.jpg", 400, 200, tweak: image =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.PixelXDimension, new Number(400u));
            exif.SetValue(ExifTag.PixelYDimension, new Number(200u));
            exif.SetValue(ExifTag.Make, "TestCam");
            image.SetProfile(exif);
        });
        var saved = ResizeOperation.Save(path, new Size(100, 50));
        using var back = new MagickImage(saved);
        var exifBack = back.GetExifProfile();
        Assert.NotNull(exifBack);
        Assert.Equal("TestCam", exifBack!.GetValue(ExifTag.Make)?.Value);
        Assert.Equal(100.0, MetadataReader.Number(exifBack.GetValue(ExifTag.PixelXDimension)?.GetValue()));
        Assert.Equal(50.0, MetadataReader.Number(exifBack.GetValue(ExifTag.PixelYDimension)?.GetValue()));
    }
}

public class CompressTests
{
    private static string Noisy(TempFolder temp, string name, int size, MagickFormat format)
    {
        var path = temp.File(name);
        using var image = new MagickImage(MagickColors.Gray, (uint)size, (uint)size);
        image.AddNoise(NoiseType.Gaussian);
        image.Write(path, format);
        return path;
    }

    [Fact]
    public void Lower_quality_makes_a_smaller_jpeg()
    {
        using var temp = new TempFolder();
        var path = Noisy(temp, "Noise.jpg", 600, MagickFormat.Jpeg);
        var high = CompressOperation.Compress(path, new CompressOptions { Quality = 95 });
        var low = CompressOperation.Compress(path, new CompressOptions { Quality = 20 });
        Assert.True(low.Data.Length < high.Data.Length);
        Assert.Equal(MagickFormat.Jpeg, low.Format);
    }

    [Fact]
    public void Target_size_gets_under_the_percentage_of_the_original()
    {
        using var temp = new TempFolder();
        var path = Noisy(temp, "Noise.jpg", 800, MagickFormat.Jpeg);
        var original = new FileInfo(path).Length;
        var (data, _) = CompressOperation.Compress(path, new CompressOptions { Mode = CompressMode.TargetSize, TargetPercent = 30 });
        Assert.True(data.Length <= original * 0.30, $"{data.Length} vs {original * 0.30}");
    }

    [Fact]
    public void Max_dimension_shrinks_the_pixels_but_never_enlarges()
    {
        using var temp = new TempFolder();
        var path = Noisy(temp, "Noise.jpg", 800, MagickFormat.Jpeg);
        var (data, _) = CompressOperation.Compress(path, new CompressOptions { Quality = 80, MaxDimension = 400 });
        using var shrunk = new MagickImage(data);
        Assert.Equal(400u, shrunk.Width);
        var (bigger, _) = CompressOperation.Compress(path, new CompressOptions { Quality = 80, MaxDimension = 4096 });
        using var same = new MagickImage(bigger);
        Assert.Equal(800u, same.Width);
    }

    [Fact]
    public void Formats_that_cannot_be_compressed_in_place_become_jpeg_or_png()
    {
        Assert.Equal(MagickFormat.Jpeg, CompressOperation.OutputFormat(MagickFormat.Bmp, false, CompressFormat.KeepOriginal));
        Assert.Equal(MagickFormat.Png, CompressOperation.OutputFormat(MagickFormat.Tiff, true, CompressFormat.KeepOriginal));
        Assert.Equal(MagickFormat.Png, CompressOperation.OutputFormat(MagickFormat.Png, false, CompressFormat.KeepOriginal));
        Assert.Equal(MagickFormat.Jpeg, CompressOperation.OutputFormat(MagickFormat.Png, false, CompressFormat.Jpeg));
    }

    [Fact]
    public void A_copy_is_saved_only_when_it_is_smaller()
    {
        using var temp = new TempFolder();
        var path = Noisy(temp, "Noise.jpg", 500, MagickFormat.Jpeg);
        var saved = CompressOperation.Save(path, new CompressOptions { Quality = 30 });
        Assert.Equal(temp.File("Noise Compressed.jpg"), saved);
        Assert.True(new FileInfo(saved).Length < new FileInfo(path).Length);

        // Compressing that copy harder than it was made cannot beat it by much, and compressing it at higher quality is bigger.
        var again = CompressOperation.Save(saved!, new CompressOptions { Quality = 100 });
        Assert.Null(again);
    }

    [Fact]
    public void Batch_counts_the_images_that_cannot_get_smaller()
    {
        using var temp = new TempFolder();
        var noisy = Noisy(temp, "Noise.jpg", 500, MagickFormat.Jpeg);
        var tiny = temp.Image("Tiny.jpg", 8, 8);
        var outcome = CompressOperation.Run([noisy, tiny], new CompressOptions { Quality = 100 });
        Assert.Equal(0, outcome.Saved.Count(p => p.Contains("Tiny")));
        Assert.True(outcome.Unchanged >= 1);
    }

    [Fact]
    public void Transparent_pngs_keep_their_alpha_and_opaque_ones_drop_the_channel()
    {
        using var temp = new TempFolder();
        var see = temp.Image("See.png", 64, 64, tweak: TestImages.HalfClear);
        var (seeData, seeFormat) = CompressOperation.Compress(see, new CompressOptions());
        Assert.Equal(MagickFormat.Png, seeFormat);
        using (var back = new MagickImage(seeData)) Assert.True(ImageFiles.IsTransparent(back));
        var solid = temp.Image("Solid.png", 64, 64);
        var (solidData, _) = CompressOperation.Compress(solid, new CompressOptions());
        using (var back = new MagickImage(solidData)) Assert.False(ImageFiles.IsTransparent(back));
    }

    [Fact]
    public void Format_notes_explain_surprises()
    {
        using var temp = new TempFolder();
        var bmp = EditableImage.Load(temp.Image("Pic.bmp", 20, 20));
        Assert.Contains("can’t be compressed in place", CompressOperation.FormatNote([bmp], CompressFormat.KeepOriginal));
        Assert.Null(CompressOperation.FormatNote([EditableImage.Load(temp.Image("Pic.jpg", 20, 20))], CompressFormat.KeepOriginal));
        var png = EditableImage.Load(temp.Image("Alpha.png", 20, 20, tweak: TestImages.HalfClear));
        Assert.Contains("see-through", CompressOperation.FormatNote([png], CompressFormat.Jpeg));
    }

    [Fact]
    public void The_not_smaller_message_counts_files()
    {
        Assert.Contains("Already as small", CompressOperation.NotSmallerMessage(1));
        Assert.Contains("These 3 files", CompressOperation.NotSmallerMessage(3));
    }
}

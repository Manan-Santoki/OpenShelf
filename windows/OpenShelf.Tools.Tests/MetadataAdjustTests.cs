using ImageMagick;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Tests;

public class MetadataTests
{
    private static void WithCameraAndLocation(MagickImage image)
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Canon");
        exif.SetValue(ExifTag.Model, "EOS R5");
        exif.SetValue(ExifTag.DateTimeOriginal, "2024:05:17 14:03:09");
        exif.SetValue(ExifTag.FNumber, new Rational(28u, 10u, false));
        exif.SetValue(ExifTag.ExposureTime, new Rational(1u, 250u, false));
        exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(37u, 1u, false), new Rational(46u, 1u, false), new Rational(30u, 1u, false) });
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLongitude, new[] { new Rational(122u, 1u, false), new Rational(25u, 1u, false), new Rational(0u, 1u, false) });
        exif.SetValue(ExifTag.GPSLongitudeRef, "W");
        image.SetProfile(exif);
    }

    [Fact]
    public void Sections_group_camera_exposure_dates_and_location()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Photo.jpg", 64, 48, tweak: WithCameraAndLocation);
        var sections = MetadataReader.Sections(path);
        var titles = sections.Select(s => s.Title).ToList();
        Assert.Contains("Camera", titles);
        Assert.Contains("Exposure", titles);
        Assert.Contains("Dates", titles);
        Assert.Contains("Location", titles);
        Assert.Contains("Image", titles);
        var camera = sections.Single(s => s.Title == "Camera").Fields;
        Assert.Contains(new MetadataField("Make", "Canon"), camera);
        var exposure = sections.Single(s => s.Title == "Exposure").Fields;
        Assert.Contains(new MetadataField("Aperture", "f/2.8"), exposure);
        Assert.Contains(new MetadataField("Shutter", "1/250 s"), exposure);
        var location = sections.Single(s => s.Title == "Location").Fields;
        Assert.Contains(location, f => f.Label == "Latitude" && f.Value.StartsWith("37.775000") && f.Value.EndsWith("N"));
        Assert.Contains(location, f => f.Label == "Longitude" && f.Value.EndsWith("W"));
        Assert.Contains(sections.Single(s => s.Title == "Image").Fields, f => f.Label == "Dimensions" && f.Value == "64 × 48");
    }

    [Fact]
    public void Location_and_camera_are_detected()
    {
        using var temp = new TempFolder();
        Assert.True(MetadataReader.HasLocation(temp.Image("With.jpg", 32, 32, tweak: WithCameraAndLocation)));
        Assert.True(MetadataReader.HasCameraInfo(temp.Image("With2.jpg", 32, 32, tweak: WithCameraAndLocation)));
        var plain = temp.Image("Plain.png", 32, 32);
        Assert.False(MetadataReader.HasLocation(plain));
        Assert.False(MetadataReader.HasCameraInfo(plain));
    }

    [Fact]
    public void Plain_text_lists_every_section()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Photo.jpg", 32, 32, tweak: WithCameraAndLocation);
        var text = MetadataReader.PlainText(MetadataReader.Sections(path), "Photo.jpg");
        Assert.StartsWith("Photo.jpg", text);
        Assert.Contains("Camera", text);
        Assert.Contains("  Make: Canon", text);
    }

    [Fact]
    public void A_clean_copy_has_no_identifying_metadata_and_keeps_the_pixels()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Photo.jpg", 64, 48, tweak: WithCameraAndLocation);
        Assert.True(MetadataStripper.HasIdentifyingMetadata(path));
        var clean = MetadataStripper.Strip(path, keepsColorProfile: true);
        Assert.Equal(temp.File("Photo Clean.jpg"), clean);
        Assert.Empty(MetadataStripper.LeftoverMetadata(clean));
        Assert.False(MetadataReader.HasLocation(clean));
        Assert.False(MetadataReader.HasCameraInfo(clean));
        Assert.Equal((64u, 48u), Pixels.SizeOf(clean));
        Assert.True(Pixels.IsNear(Pixels.At(clean, 5, 5), 255, 0, 0, 40));
        // The original is untouched.
        Assert.True(MetadataStripper.HasIdentifyingMetadata(path));
    }

    [Fact]
    public void The_batch_leaves_already_clean_files_alone()
    {
        using var temp = new TempFolder();
        var dirty = temp.Image("Dirty.jpg", 32, 32, tweak: WithCameraAndLocation);
        var plain = temp.Image("Plain.png", 32, 32);
        var outcome = MetadataStripper.Run([dirty, plain], keepsColorProfile: true);
        Assert.Single(outcome.Saved);
        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal("1 cleaned · 1 already clean", outcome.Title);
        Assert.False(File.Exists(temp.File("Plain Clean.png")));
    }

    [Fact]
    public void Humanize_splits_camel_case()
    {
        Assert.Equal("Date Time Original", MetadataReader.Humanize("DateTimeOriginal"));
        Assert.Equal("ISO Speed Ratings", MetadataReader.Humanize("ISOSpeedRatings"));
    }
}

public class AdjustTests
{
    private static MagickImage Quadrants()
    {
        var image = new MagickImage(MagickColors.Blue, 200, 100);
        using var red = new MagickImage(MagickColors.Red, 100, 50);
        image.Composite(red, 0, 0, CompositeOperator.Over);
        return image;
    }

    private static IMagickColor<byte> At(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).ToColor()!;
    }

    [Fact]
    public void No_change_is_detected()
    {
        Assert.False(new AdjustSettings().ChangesImage);
        Assert.False((new AdjustSettings { Exposure = 0.2 }).ChangesImage);
        Assert.True((new AdjustSettings { Contrast = 12 }).ChangesImage);
    }

    [Fact]
    public void Turns_and_flips_that_cancel_leave_the_image_as_it_was()
    {
        Assert.False(new AdjustSettings { QuarterTurns = 4 }.ChangesImage);
        Assert.False(new AdjustSettings { QuarterTurns = 2, FlipsHorizontally = true, FlipsVertically = true }.ChangesImage);
        Assert.True(new AdjustSettings { FlipsHorizontally = true }.ChangesImage);
        Assert.True(new AdjustSettings { QuarterTurns = -1 }.ChangesImage);
        Assert.False(new AdjustSettings { FlipsHorizontally = true, FlipsVertically = true, QuarterTurns = 2 }.ChangesImage);
    }

    [Fact]
    public void Rotating_clockwise_moves_the_top_left_to_the_top_right()
    {
        using var image = Quadrants();
        AdjustOperation.Apply(image, new AdjustSettings { QuarterTurns = 1 });
        Assert.Equal(100u, image.Width);
        Assert.Equal(200u, image.Height);
        Assert.True(Pixels.IsNear(At(image, 90, 10), 255, 0, 0));
        Assert.True(Pixels.IsNear(At(image, 10, 10), 0, 0, 255));
    }

    [Fact]
    public void Rotating_back_and_forth_restores_the_image()
    {
        using var image = Quadrants();
        AdjustOperation.Apply(image, new AdjustSettings { QuarterTurns = -1 });
        Assert.Equal(100u, image.Width);
        Assert.True(Pixels.IsNear(At(image, 10, 190), 255, 0, 0));
    }

    [Fact]
    public void Flips_move_the_red_corner()
    {
        using var horizontal = Quadrants();
        AdjustOperation.Apply(horizontal, new AdjustSettings { FlipsHorizontally = true });
        Assert.True(Pixels.IsNear(At(horizontal, 190, 10), 255, 0, 0));
        using var vertical = Quadrants();
        AdjustOperation.Apply(vertical, new AdjustSettings { FlipsVertically = true });
        Assert.True(Pixels.IsNear(At(vertical, 10, 90), 255, 0, 0));
    }

    [Fact]
    public void Brightness_and_exposure_lighten_and_darken()
    {
        using var grey = new MagickImage(new MagickColor("#808080"), 20, 20);
        using var brighter = (MagickImage)grey.Clone();
        AdjustOperation.Apply(brighter, new AdjustSettings { Brightness = 60 });
        Assert.True(At(brighter, 5, 5).R > 0x90);
        using var darker = (MagickImage)grey.Clone();
        AdjustOperation.Apply(darker, new AdjustSettings { Exposure = -60 });
        Assert.True(At(darker, 5, 5).R < 0x70);
        using var lighter = (MagickImage)grey.Clone();
        AdjustOperation.Apply(lighter, new AdjustSettings { Exposure = 60 });
        Assert.True(At(lighter, 5, 5).R > 0x90);
    }

    [Fact]
    public void Contrast_pushes_tones_apart()
    {
        using var image = new MagickImage(new MagickColor("#a0a0a0"), 20, 20);
        AdjustOperation.Apply(image, new AdjustSettings { Contrast = 80 });
        Assert.True(At(image, 5, 5).R > 0xa0);
    }

    [Fact]
    public void Zero_saturation_makes_grey_and_warmth_shifts_red_over_blue()
    {
        using var colour = new MagickImage(new MagickColor("#cc6633"), 20, 20);
        using var grey = (MagickImage)colour.Clone();
        AdjustOperation.Apply(grey, new AdjustSettings { Saturation = -100 });
        var g = At(grey, 5, 5);
        Assert.True(Math.Abs(g.R - g.G) < 6 && Math.Abs(g.G - g.B) < 6);

        using var neutral = new MagickImage(new MagickColor("#808080"), 20, 20);
        AdjustOperation.Apply(neutral, new AdjustSettings { Warmth = 80 });
        var warm = At(neutral, 5, 5);
        Assert.True(warm.R > warm.B + 10);
    }

    [Fact]
    public void Vibrance_lifts_muted_colours_more_than_vivid_ones()
    {
        using var muted = new MagickImage(new MagickColor("#907878"), 20, 20);
        var before = At(muted, 1, 1);
        AdjustOperation.Apply(muted, new AdjustSettings { Vibrance = 100 });
        var after = At(muted, 1, 1);
        Assert.True(after.R - after.G > before.R - before.G);

        using var vivid = new MagickImage(new MagickColor("#ff0000"), 20, 20);
        AdjustOperation.Apply(vivid, new AdjustSettings { Vibrance = 100 });
        Assert.True(Pixels.IsNear(At(vivid, 1, 1), 255, 0, 0, 40));
    }

    [Fact]
    public void Vignette_darkens_the_corners_but_not_the_centre()
    {
        using var image = new MagickImage(new MagickColor("#c0c0c0"), 101, 101);
        AdjustOperation.Apply(image, new AdjustSettings { Vignette = 100 });
        Assert.True(At(image, 0, 0).R < 0x80);
        Assert.True(At(image, 50, 50).R > 0xb8);
    }

    [Fact]
    public void Sharpness_runs_on_a_photo_sized_image()
    {
        using var image = Quadrants();
        AdjustOperation.Apply(image, new AdjustSettings { Sharpness = 80 });
        Assert.Equal(200u, image.Width);
    }

    [Fact]
    public void Save_writes_an_edited_copy_and_batch_skips_untouched_files()
    {
        using var temp = new TempFolder();
        var a = temp.Image("a.png", 60, 40);
        var b = temp.Image("b.png", 60, 40);
        var outcome = AdjustOperation.Run([(a, new AdjustSettings { QuarterTurns = 1 }), (b, new AdjustSettings())]);
        Assert.Equal(temp.File("a Edited.png"), Assert.Single(outcome.Saved));
        Assert.Equal((40u, 60u), Pixels.SizeOf(outcome.Saved[0]));
        Assert.Equal("1 edited · 1 not edited", outcome.Title);
    }

    [Fact]
    public void The_preview_is_a_downscaled_copy()
    {
        using var big = new MagickImage(MagickColors.Gray, 3000, 2000);
        using var preview = AdjustOperation.Preview(big, new AdjustSettings { Brightness = 20 }, 1000);
        Assert.Equal(1000u, preview.Width);
        Assert.Equal(667u, preview.Height);
        Assert.Equal(3000u, big.Width);
    }
}

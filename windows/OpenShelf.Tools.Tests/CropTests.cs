using System.Windows;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Tests;

public class CropTests
{
    private static readonly Rect Bounds = new(0, 0, 1000, 500);

    [Fact]
    public void Snapped_keeps_whole_pixels_inside_bounds()
    {
        var r = CropMath.Snapped(new Rect(10.4, 20.6, 100.2, 50.3), Bounds);
        Assert.Equal(new Rect(10, 21, 101, 50), r);
        var outside = CropMath.Snapped(new Rect(-30, -30, 5000, 5000), Bounds);
        Assert.Equal(Bounds, outside);
    }

    [Fact]
    public void Moving_keeps_the_size_and_stops_at_the_edge()
    {
        var crop = new Rect(100, 100, 200, 100);
        var moved = CropMath.Adjusted(crop, null, new Vector(5000, 5000), null, Bounds, 16);
        Assert.Equal(200, moved.Width);
        Assert.Equal(100, moved.Height);
        Assert.Equal(1000, moved.Right);
        Assert.Equal(500, moved.Bottom);
    }

    [Fact]
    public void Handle_resizes_from_the_opposite_side()
    {
        var crop = new Rect(100, 100, 200, 100);
        var wider = CropMath.Adjusted(crop, CropHandle.Right, new Vector(50, 0), null, Bounds, 16);
        Assert.Equal(new Rect(100, 100, 250, 100), wider);
        var leftIn = CropMath.Adjusted(crop, CropHandle.Left, new Vector(30, 0), null, Bounds, 16);
        Assert.Equal(new Rect(130, 100, 170, 100), leftIn);
    }

    [Fact]
    public void Free_resize_never_goes_below_the_minimum()
    {
        var crop = new Rect(100, 100, 200, 100);
        var tiny = CropMath.Adjusted(crop, CropHandle.BottomRight, new Vector(-5000, -5000), null, Bounds, 16);
        Assert.Equal(16, tiny.Width);
        Assert.Equal(16, tiny.Height);
    }

    [Fact]
    public void Corner_drag_with_a_ratio_keeps_the_ratio()
    {
        var crop = new Rect(100, 100, 160, 90);
        var r = CropMath.Adjusted(crop, CropHandle.BottomRight, new Vector(80, 10), 16.0 / 9, Bounds, 16);
        Assert.InRange(r.Width / r.Height, 16.0 / 9 - 0.03, 16.0 / 9 + 0.03);
        Assert.Equal(crop.Left, r.Left);
        Assert.Equal(crop.Top, r.Top);
    }

    [Fact]
    public void Typed_width_with_a_ratio_sets_the_height()
    {
        var r = CropMath.Resized(new Rect(0, 0, 400, 400), 320, null, 4.0 / 3, Bounds, 16);
        Assert.Equal(320, r.Width);
        Assert.Equal(240, r.Height);
    }

    [Fact]
    public void Typed_size_larger_than_the_image_is_scaled_back()
    {
        var r = CropMath.Resized(new Rect(0, 0, 400, 400), 5000, null, null, Bounds, 16);
        Assert.Equal(1000, r.Width);
    }

    [Fact]
    public void Largest_finds_the_biggest_centred_rectangle_of_a_ratio()
    {
        var r = CropMath.Largest(1, new Point(500, 250), Bounds);
        Assert.Equal(500, r.Width);
        Assert.Equal(500, r.Height);
        Assert.Equal(250, r.Left);
        var wide = CropMath.Largest(16.0 / 9, new Point(500, 250), Bounds);
        Assert.Equal(889, wide.Width);
        Assert.Equal(500, wide.Height);
    }

    [Fact]
    public void A_crop_is_kept_as_fractions_so_the_top_half_stays_the_top_half()
    {
        var top = new Rect(0, 0, 1000, 250);
        var unit = CropMath.Unit(top, Bounds);
        Assert.Equal(new Rect(0, 0, 1, 0.5), unit);
        var other = new Rect(0, 0, 400, 800);
        var mapped = CropMath.Mapped(unit, other, null);
        Assert.Equal(new Rect(0, 0, 400, 400), mapped);
    }

    [Fact]
    public void Mapping_with_a_ratio_keeps_the_shape_on_another_shaped_image()
    {
        var unit = new Rect(0.25, 0.25, 0.5, 0.5);
        var mapped = CropMath.Mapped(unit, new Rect(0, 0, 1000, 200), 1);
        Assert.Equal(mapped.Width, mapped.Height);
        Assert.True(mapped.Bottom <= 200);
    }

    [Fact]
    public void Cropping_saves_a_copy_with_the_chosen_pixels()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Photo.png", 200, 100);
        var saved = CropOperation.Save(path, new Rect(0, 0, 100, 50));
        Assert.Equal(temp.File("Photo Cropped.png"), saved);
        Assert.Equal((100u, 50u), Pixels.SizeOf(saved));
        // The red square is the top-left quarter: the whole crop is red.
        Assert.True(Pixels.IsNear(Pixels.At(saved, 99, 49), 255, 0, 0));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Cropping_never_overwrites()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Photo.jpg", 100, 100);
        var first = CropOperation.Save(path, new Rect(0, 0, 50, 50));
        var second = CropOperation.Save(path, new Rect(0, 0, 40, 40));
        Assert.NotEqual(first, second);
        Assert.Equal(temp.File("Photo Cropped 2.jpg"), second);
        Assert.Equal((50u, 50u), Pixels.SizeOf(first));
        Assert.Equal((40u, 40u), Pixels.SizeOf(second));
    }

    [Fact]
    public void Batch_crop_counts_untouched_files()
    {
        using var temp = new TempFolder();
        var a = temp.Image("a.png", 100, 100);
        var b = temp.Image("b.png", 100, 100);
        var c = temp.Image("c.png", 100, 100);
        var outcome = CropOperation.Run([
            (a, new Rect(0, 0, 50, 50), new Size(100, 100)),
            (b, new Rect(0, 0, 100, 100), new Size(100, 100)),
            (c, new Rect(10, 10, 20, 20), new Size(100, 100)),
        ]);
        Assert.Equal(2, outcome.Saved.Count);
        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal("2 cropped · 1 not cropped", outcome.Title);
        Assert.Equal("2 cropped · 1 not cropped", outcome.Message);
        Assert.False(File.Exists(temp.File("b Cropped.png")));
    }

    [Fact]
    public void The_crop_follows_the_exif_turn()
    {
        using var temp = new TempFolder();
        // 200 x 100 pixels that a viewer shows turned 90 degrees: 100 wide, 200 tall.
        var path = temp.Image("Turned.jpg", 200, 100, tweak: TestImages.Turned);
        using (var read = ImageFiles.ReadUpright(path))
        {
            Assert.Equal(100u, read.Width);
            Assert.Equal(200u, read.Height);
        }
        var saved = CropOperation.Save(path, new Rect(0, 0, 100, 100));
        var (w, h) = Pixels.SizeOf(saved);
        Assert.Equal((100u, 100u), (w, h));
        using var back = new ImageMagick.MagickImage(saved);
        Assert.True(back.Orientation is ImageMagick.Orientation.TopLeft or ImageMagick.Orientation.Undefined);
    }
}

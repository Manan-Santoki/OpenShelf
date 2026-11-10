using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ImageMagick;
using OpenShelf.Tools.Imaging;

namespace OpenShelf.Tools.Tests;

public class RedactTests
{
    private static IMagickColor<byte> At(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).ToColor()!;
    }

    /// <summary>Left half red, right half blue.</summary>
    private static MagickImage Halves(int width = 200, int height = 100)
    {
        var image = new MagickImage(MagickColors.Blue, (uint)width, (uint)height);
        using var red = new MagickImage(MagickColors.Red, (uint)(width / 2), (uint)height);
        image.Composite(red, 0, 0, CompositeOperator.Over);
        return image;
    }

    [Fact]
    public void A_black_box_covers_only_its_area()
    {
        using var image = Halves();
        RedactOperation.Apply(image, [(new Rect(20, 20, 40, 30), new RedactionLook(RedactionStyle.Box, 0))]);
        Assert.True(Pixels.IsNear(At(image, 30, 30), 0, 0, 0, 2));
        Assert.True(Pixels.IsNear(At(image, 59, 49), 0, 0, 0, 2));
        Assert.True(Pixels.IsNear(At(image, 10, 10), 255, 0, 0, 2));
        Assert.True(Pixels.IsNear(At(image, 61, 51), 255, 0, 0, 2));
        Assert.True(Pixels.IsNear(At(image, 150, 50), 0, 0, 255, 2));
    }

    [Fact]
    public void Blur_softens_the_edge_inside_the_area()
    {
        using var image = Halves();
        RedactOperation.Apply(image, [(new Rect(60, 20, 80, 60), new RedactionLook(RedactionStyle.Blur, 100))]);
        // Right at the red/blue boundary the colours mix.
        var middle = At(image, 100, 50);
        Assert.InRange(middle.R, 60, 200);
        Assert.InRange(middle.B, 60, 200);
        // Outside the area nothing changed.
        Assert.True(Pixels.IsNear(At(image, 20, 50), 255, 0, 0, 2));
        Assert.True(Pixels.IsNear(At(image, 180, 50), 0, 0, 255, 2));
    }

    [Fact]
    public void Pixelate_turns_the_area_into_flat_blocks()
    {
        using var image = new MagickImage(MagickColors.White, 200, 100);
        image.AddNoise(NoiseType.Uniform);
        using var before = (MagickImage)image.Clone();
        RedactOperation.Apply(image, [(new Rect(40, 20, 100, 60), new RedactionLook(RedactionStyle.Pixelate, 80))]);
        // Neighbouring pixels inside one block are identical; in noise they never were.
        var a = At(image, 80, 50);
        var b = At(image, 81, 50);
        var c = At(image, 80, 51);
        Assert.Equal(a.R, b.R);
        Assert.Equal(a.G, c.G);
        // Outside the area the noise is untouched.
        Assert.Equal(At(before, 10, 10).R, At(image, 10, 10).R);
        Assert.Equal(At(before, 170, 90).G, At(image, 170, 90).G);
    }

    [Fact]
    public void Each_area_keeps_its_own_look()
    {
        using var image = Halves();
        RedactOperation.Apply(image,
        [
            (new Rect(0, 0, 30, 30), new RedactionLook(RedactionStyle.Box, 0)),
            (new Rect(100, 60, 50, 30), new RedactionLook(RedactionStyle.Blur, 60)),
        ]);
        Assert.True(Pixels.IsNear(At(image, 10, 10), 0, 0, 0, 2));
        Assert.False(Pixels.IsNear(At(image, 100, 70), 0, 0, 0, 10));
    }

    [Fact]
    public void Areas_outside_the_image_are_kept_inside()
    {
        using var image = Halves();
        RedactOperation.Apply(image, [(new Rect(150, 60, 500, 500), new RedactionLook(RedactionStyle.Box, 0))]);
        Assert.True(Pixels.IsNear(At(image, 199, 99), 0, 0, 0, 2));
        Assert.Equal(200u, image.Width);
    }

    [Fact]
    public void A_redacted_copy_is_saved_without_metadata_and_empty_images_are_skipped()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Shot.jpg", 120, 80, tweak: image =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Pixel");
            image.SetProfile(exif);
        });
        var untouched = temp.Image("Other.png", 50, 50);
        var region = RedactRegion.Create(new Rect(10, 10, 40, 30), new RedactionLook(RedactionStyle.Box, 0));
        var outcome = RedactOperation.Run([(path, new[] { region }), (untouched, Array.Empty<RedactRegion>())]);
        var saved = Assert.Single(outcome.Saved);
        Assert.Equal(temp.File("Shot Redacted.jpg"), saved);
        Assert.Equal("1 redacted · 1 not redacted", outcome.Title);
        Assert.False(MetadataReader.HasCameraInfo(saved));
        Assert.True(Pixels.IsNear(Pixels.At(saved, 20, 20), 0, 0, 0, 30));
    }
}

public class AnnotateTests
{
    private static Annotation Make(Figure figure, Color? color = null, double width = 6) =>
        Annotation.Create(figure, color ?? Color.FromRgb(0xFF, 0x00, 0x00), width);

    private static MagickImage White(int w = 200, int h = 120) => new(MagickColors.White, (uint)w, (uint)h);

    private static IMagickColor<byte> At(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).ToColor()!;
    }

    [Fact]
    public void A_box_draws_its_outline_but_not_its_inside()
    {
        Sta.Run(() =>
        {
            using var image = White();
            AnnotationRenderer.Render(image, [Make(new BoxFigure(new Rect(40, 30, 100, 60)))]);
            Assert.True(Pixels.IsNear(At(image, 40, 60), 255, 0, 0, 40));
            Assert.True(Pixels.IsNear(At(image, 90, 30), 255, 0, 0, 40));
            Assert.True(Pixels.IsNear(At(image, 90, 60), 255, 255, 255, 5));
            Assert.True(Pixels.IsNear(At(image, 5, 5), 255, 255, 255, 5));
        });
    }

    [Fact]
    public void A_stroke_follows_its_points()
    {
        Sta.Run(() =>
        {
            using var image = White();
            AnnotationRenderer.Render(image, [Make(new StrokeFigure([new Point(10, 10), new Point(100, 10), new Point(100, 90)]))]);
            Assert.True(Pixels.IsNear(At(image, 60, 10), 255, 0, 0, 40));
            Assert.True(Pixels.IsNear(At(image, 100, 60), 255, 0, 0, 40));
            Assert.True(Pixels.IsNear(At(image, 30, 60), 255, 255, 255, 5));
        });
    }

    [Fact]
    public void A_highlight_tints_without_hiding()
    {
        Sta.Run(() =>
        {
            using var image = new MagickImage(MagickColors.Blue, 100, 60);
            AnnotationRenderer.Render(image, [Make(new HighlightFigure(new Rect(10, 10, 50, 30)), Color.FromRgb(255, 255, 0))]);
            var inside = At(image, 30, 25);
            Assert.True(inside.R > 60 && inside.B > 100, $"{inside.R},{inside.G},{inside.B}");
            Assert.True(Pixels.IsNear(At(image, 80, 50), 0, 0, 255, 3));
        });
    }

    [Fact]
    public void An_arrow_has_a_shaft_and_a_filled_head()
    {
        Sta.Run(() =>
        {
            using var image = White();
            AnnotationRenderer.Render(image, [Make(new ArrowFigure(new Point(20, 60), new Point(150, 60)), width: 6)]);
            Assert.True(Pixels.IsNear(At(image, 60, 60), 255, 0, 0, 40));
            // The head is wider than the shaft.
            Assert.True(Pixels.IsNear(At(image, 132, 66), 255, 0, 0, 40));
            Assert.True(Pixels.IsNear(At(image, 60, 70), 255, 255, 255, 5));
        });
    }

    [Fact]
    public void A_callout_is_a_filled_circle_with_its_number()
    {
        Sta.Run(() =>
        {
            using var image = White();
            AnnotationRenderer.Render(image, [Make(new CalloutFigure(1, new Point(100, 60), 60), Color.FromRgb(0, 0, 255))]);
            Assert.True(Pixels.IsNear(At(image, 100 - 22, 60), 0, 0, 255, 40));
            Assert.True(Pixels.IsNear(At(image, 5, 5), 255, 255, 255, 5));
            // White digit in the middle: some near-white pixels sit inside the blue disc.
            var white = 0;
            for (var x = 90; x < 110; x++)
            for (var y = 45; y < 75; y++)
                if (At(image, x, y).B > 200 && At(image, x, y).R > 200) white++;
            Assert.True(white > 10);
        });
    }

    [Fact]
    public void Text_puts_coloured_pixels_at_its_origin()
    {
        Sta.Run(() =>
        {
            using var image = White(300, 100);
            AnnotationRenderer.Render(image, [Make(new TextFigure("Hello", new Point(20, 20)), Color.FromRgb(0, 128, 0), 4)]);
            var green = 0;
            for (var x = 20; x < 120; x++)
            for (var y = 20; y < 60; y++)
                if (At(image, x, y).G > At(image, x, y).R + 60) green++;
            Assert.True(green > 40);
            Assert.True(Pixels.IsNear(At(image, 280, 90), 255, 255, 255, 5));
        });
    }

    [Fact]
    public void Text_size_follows_the_width_control()
    {
        Assert.Equal(14, AnnotationRenderer.FontSize(1));
        Assert.Equal(14, AnnotationRenderer.FontSize(2));
        Assert.Equal(70, AnnotationRenderer.FontSize(10));
    }

    [Fact]
    public void Bounds_and_hit_testing_find_marks()
    {
        Sta.Run(() =>
        {
            var box = Make(new BoxFigure(new Rect(10, 10, 100, 50)), width: 4);
            Assert.True(AnnotationRenderer.Contains(box, new Point(50, 30), 2));
            Assert.False(AnnotationRenderer.Contains(box, new Point(200, 200), 2));
            Assert.True(AnnotationRenderer.Bounds(box).Contains(new Point(8.5, 8.5)));

            var stroke = Make(new StrokeFigure([new Point(0, 0), new Point(100, 0)]), width: 4);
            Assert.True(AnnotationRenderer.Contains(stroke, new Point(50, 3), 2));
            Assert.False(AnnotationRenderer.Contains(stroke, new Point(50, 30), 2));

            var callout = Make(new CalloutFigure(2, new Point(50, 50), 40));
            Assert.True(AnnotationRenderer.Contains(callout, new Point(65, 50), 2));
            Assert.False(AnnotationRenderer.Contains(callout, new Point(90, 50), 2));

            var text = Make(new TextFigure("Some words", new Point(10, 10)), width: 4);
            var bounds = AnnotationRenderer.Bounds(text);
            Assert.True(bounds.Width > 50 && bounds.Height > 10);
            Assert.True(AnnotationRenderer.Contains(text, new Point(20, 20), 2));
        });
    }

    [Fact]
    public void Moving_a_mark_offsets_every_point()
    {
        var moved = Make(new ArrowFigure(new Point(0, 0), new Point(10, 10))).OffsetBy(new Vector(5, 7));
        var arrow = Assert.IsType<ArrowFigure>(moved.Figure);
        Assert.Equal(new Point(5, 7), arrow.From);
        Assert.Equal(new Point(15, 17), arrow.To);
        var callout = Assert.IsType<CalloutFigure>(Make(new CalloutFigure(3, new Point(1, 2), 30)).OffsetBy(new Vector(10, 10)).Figure);
        Assert.Equal(new Point(11, 12), callout.Center);
        Assert.Equal(3, callout.Number);
    }

    [Fact]
    public void History_undoes_and_redoes_whole_states()
    {
        var history = new EditHistory<IReadOnlyList<int>>();
        IReadOnlyList<int> state = [];
        Assert.False(history.CanUndo);

        history.Record(state);
        state = [1];
        history.Record(state);
        state = [1, 2];
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        state = history.Undo(state)!;
        Assert.Equal([1], state);
        state = history.Undo(state)!;
        Assert.Empty(state);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);

        state = history.Redo(state)!;
        Assert.Equal([1], state);
        // A new change drops what could be redone.
        history.Record(state);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void History_merges_changes_with_the_same_key_into_one_step()
    {
        var history = new EditHistory<int>();
        history.Record(0, "width");
        history.Record(1, "width");
        history.Record(2, "width");
        Assert.Equal(0, history.Undo(3));
        Assert.False(history.CanUndo);

        history.Record(5, "width");
        history.EndCoalescing();
        history.Record(6, "width");
        Assert.Equal(6, history.Undo(7));
        Assert.Equal(5, history.Undo(6));
    }

    [Fact]
    public void History_is_limited()
    {
        var history = new EditHistory<int>(3);
        for (var i = 0; i < 10; i++) history.Record(i);
        var steps = 0;
        var current = 10;
        while (history.CanUndo) { current = history.Undo(current); steps++; }
        Assert.Equal(3, steps);
        Assert.Equal(7, current);
    }

    [Fact]
    public void Annotated_copies_are_saved_and_unmarked_images_skipped()
    {
        Sta.Run(() =>
        {
            using var temp = new TempFolder();
            var a = temp.Image("a.jpg", 160, 100);
            var b = temp.Image("b.jpg", 160, 100);
            var marks = new[] { Make(new BoxFigure(new Rect(20, 20, 80, 50))) };
            var outcome = AnnotateOperation.Run([(a, marks), (b, Array.Empty<Annotation>())]);
            Assert.Equal("1 annotated · 1 not annotated", outcome.Title);
            Assert.Equal(temp.File("a Annotated.jpg"), outcome.Saved[0]);
            Assert.True(Pixels.IsNear(Pixels.At(outcome.Saved[0], 20, 45), 255, 0, 0, 60));
            Assert.Equal((160u, 100u), Pixels.SizeOf(outcome.Saved[0]));
        });
    }
}

public class BackgroundTests
{
    private static BackgroundComposition Plain(double padding = 40, double corners = 0, double shadow = 0, CanvasRatio ratio = CanvasRatio.Auto,
        BackgroundPaint? paint = null) =>
        new(paint ?? new BackgroundPaint.Solid(0xF5C542u), padding, corners, shadow, ratio);

    private static IMagickColor<byte> At(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).ToColor()!;
    }

    [Fact]
    public void Canvas_grows_by_the_padding_on_every_side()
    {
        Assert.Equal(new Size(180, 120), Plain(40).CanvasSize(new Size(100, 40)));
    }

    [Fact]
    public void Ratios_widen_or_heighten_the_canvas()
    {
        var square = Plain(10, ratio: CanvasRatio.Square).CanvasSize(new Size(200, 100));
        Assert.Equal(square.Width, square.Height);
        Assert.Equal(220, square.Width);
        var wide = Plain(0, ratio: CanvasRatio.Landscape).CanvasSize(new Size(100, 100));
        Assert.Equal(16.0 / 9, wide.Width / wide.Height, 3);
        var portrait = Plain(0, ratio: CanvasRatio.Portrait).CanvasSize(new Size(400, 100));
        Assert.Equal(0.8, portrait.Width / portrait.Height, 3);
        Assert.Equal(400, portrait.Width);
    }

    [Fact]
    public void Only_a_visible_change_counts_as_an_edit()
    {
        Assert.False(Plain(0).ChangesImage(new Size(100, 100), false));
        Assert.False(Plain(0, shadow: 30).ChangesImage(new Size(100, 100), false));
        Assert.True(Plain(10).ChangesImage(new Size(100, 100), false));
        Assert.True(Plain(0, corners: 12).ChangesImage(new Size(100, 100), false));
        Assert.True(Plain(0).ChangesImage(new Size(100, 100), true));
        Assert.True(Plain(0, ratio: CanvasRatio.Landscape).ChangesImage(new Size(100, 100), false));
    }

    [Fact]
    public void The_image_sits_centred_on_the_background()
    {
        using var source = new MagickImage(MagickColors.Red, 100, 60);
        using var result = Plain(20).Render(source, new Size(100, 60), 1);
        Assert.Equal(140u, result.Width);
        Assert.Equal(100u, result.Height);
        Assert.True(Pixels.IsNear(At(result, 3, 3), 0xF5, 0xC5, 0x42, 3));
        Assert.True(Pixels.IsNear(At(result, 70, 50), 255, 0, 0, 3));
        Assert.True(Pixels.IsNear(At(result, 136, 96), 0xF5, 0xC5, 0x42, 3));
    }

    [Fact]
    public void Rounded_corners_let_the_background_show()
    {
        using var source = new MagickImage(MagickColors.Red, 100, 100);
        using var result = Plain(20, corners: 30).Render(source, new Size(100, 100), 1);
        // The very corner of the picture is cut away.
        Assert.True(Pixels.IsNear(At(result, 21, 21), 0xF5, 0xC5, 0x42, 8));
        Assert.True(Pixels.IsNear(At(result, 70, 70), 255, 0, 0, 3));
    }

    [Fact]
    public void A_shadow_darkens_the_background_below_the_image()
    {
        using var source = new MagickImage(MagickColors.White, 100, 100);
        var paint = new BackgroundPaint.Solid(0xFFFFFFu);
        using var withShadow = Plain(60, shadow: 40, paint: paint).Render(source, new Size(100, 100), 1);
        using var without = Plain(60, shadow: 0, paint: paint).Render(source, new Size(100, 100), 1);
        // Just under the picture's bottom edge.
        Assert.True(At(withShadow, 110, 168).R < At(without, 110, 168).R - 15);
        // Far corner stays clear.
        Assert.True(At(withShadow, 2, 2).R > 245);
    }

    [Fact]
    public void Gradients_vary_across_the_canvas()
    {
        using var source = new MagickImage(MagickColors.Red, 50, 50);
        var composition = Plain(80, paint: new BackgroundPaint.Gradient(GradientStyle.All[5]));
        using var result = composition.Render(source, new Size(50, 50), 1);
        var topLeft = At(result, 2, 2);
        var bottomRight = At(result, (int)result.Width - 3, (int)result.Height - 3);
        Assert.True(Math.Abs(topLeft.R - bottomRight.R) + Math.Abs(topLeft.G - bottomRight.G) + Math.Abs(topLeft.B - bottomRight.B) > 40);
    }

    [Fact]
    public void Every_gradient_makes_a_swatch()
    {
        foreach (var style in GradientStyle.All)
        {
            using var swatch = BackgroundComposition.Swatch(new BackgroundPaint.Gradient(style));
            Assert.Equal(56u, swatch.Width);
        }
        Assert.Equal(9, SolidColors.All.Count);
        Assert.Equal(SolidColors.All.Count, SolidColors.Names.Count);
    }

    [Fact]
    public void A_picture_background_fills_the_canvas()
    {
        using var temp = new TempFolder();
        var backdrop = temp.Image("Backdrop.png", 80, 40, background: "#00AA00");
        using var source = new MagickImage(MagickColors.Red, 20, 20);
        using var result = Plain(40, paint: new BackgroundPaint.Picture(backdrop)).Render(source, new Size(20, 20), 1);
        Assert.Equal(100u, result.Width);
        Assert.True(Pixels.IsNear(At(result, 95, 95), 0, 0xAA, 0, 6));
    }

    [Fact]
    public void A_downscaled_preview_matches_the_full_layout()
    {
        using var source = new MagickImage(MagickColors.Red, 50, 30);
        using var result = Plain(20).Render(source, new Size(100, 60), 0.5);
        Assert.Equal(70u, result.Width);
        Assert.Equal(50u, result.Height);
    }

    [Fact]
    public void Saved_copies_are_png_with_the_suffix()
    {
        using var temp = new TempFolder();
        var path = temp.Image("Shot.jpg", 80, 60);
        var composition = Plain(20, corners: 10);
        var skipped = temp.Image("Flat.png", 40, 40);
        var outcome = BackgroundOperation.Run(
        [
            (path, new Size(80, 60), false, composition),
            (skipped, new Size(40, 40), false, Plain(0)),
        ]);
        Assert.Equal(temp.File("Shot With Background.png"), Assert.Single(outcome.Saved));
        Assert.Equal((120u, 100u), Pixels.SizeOf(outcome.Saved[0]));
        Assert.Equal(ImageMagick.MagickFormat.Png, ImageFiles.FormatOf(outcome.Saved[0]));
        Assert.Equal(1, outcome.Unchanged);
    }
}

public class CombineTests
{
    [Fact]
    public void Combine_applies_to_several_images_only()
    {
        using var temp = new TempFolder();
        var a = temp.Image("a.png", 10, 10);
        var b = temp.Image("b.jpg", 10, 10);
        Assert.True(CombineOperation.Applies([a, b]));
        Assert.False(CombineOperation.Applies([a]));
        var text = temp.File("t.txt");
        File.WriteAllText(text, "x");
        Assert.False(CombineOperation.Applies([a, text]));
    }

    [Fact]
    public void Several_images_become_one_pdf_with_a_page_each()
    {
        using var temp = new TempFolder();
        var files = new[] { temp.Image("1.png", 100, 50), temp.Image("2.jpg", 60, 90), temp.Image("3.png", 30, 30) };
        var pdf = CombineOperation.Run(files);
        Assert.Equal(temp.File("Combined.pdf"), pdf);
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        Assert.StartsWith("%PDF", text);
        Assert.Equal(3, Regex.Matches(text, @"/Type\s*/Page(?!s)").Count);
    }

    [Fact]
    public void Combining_twice_does_not_overwrite()
    {
        using var temp = new TempFolder();
        var files = new[] { temp.Image("1.png", 20, 20), temp.Image("2.png", 20, 20) };
        var first = CombineOperation.Run(files);
        var second = CombineOperation.Run(files);
        Assert.NotEqual(first, second);
        Assert.Equal(temp.File("Combined 2.pdf"), second);
    }

    [Fact]
    public void Progress_reaches_the_end()
    {
        using var temp = new TempFolder();
        var files = new[] { temp.Image("1.png", 20, 20), temp.Image("2.png", 20, 20) };
        var seen = new List<double>();
        CombineOperation.Run(files, new Progress<double>(seen.Add));
        Thread.Sleep(100);
        Assert.Contains(1.0, seen);
    }
}

public class OutcomeTests
{
    [Fact]
    public void Titles_follow_the_mac_wording()
    {
        Assert.Equal("Nothing saved", new SaveOutcome().Title);
        Assert.Equal("Saved", new SaveOutcome(["a.png"]).Title);
        Assert.Equal("Saved 3 files", new SaveOutcome(["a", "b", "c"]).Title);
        var mixed = new SaveOutcome(["a", "b"]) { SavedLabel = "cropped", UnchangedLabel = "not cropped", Unchanged = 1 };
        Assert.Equal("2 cropped · 1 not cropped", mixed.Title);
        Assert.Null(mixed.Subtitle);
    }

    [Fact]
    public void Subtitles_name_the_file_or_the_reason()
    {
        Assert.Equal("a.png", new SaveOutcome([@"C:\x\a.png"]).Subtitle);
        Assert.Equal("Saved · a.png", new SaveOutcome([@"C:\x\a.png"]).Message);
        var none = new SaveOutcome { Unchanged = 1, UnchangedLabel = "already smaller" };
        Assert.Equal("Already smaller", none.Subtitle);
        Assert.Equal("All 3 already smaller", new SaveOutcome { Unchanged = 3, UnchangedLabel = "already smaller" }.Subtitle);
        Assert.Equal("as PNG", new SaveOutcome(["a", "b"]) { Detail = "as PNG" }.Subtitle);
        Assert.False(none.SavedAnything);
    }

    [Fact]
    public void Save_errors_say_what_to_do()
    {
        Assert.Contains("permission", SaveException.NoPermission("Docs").Message);
        Assert.Contains("no longer exists", SaveException.FolderMissing("Docs").Message);
        Assert.Contains("free space", SaveException.DiskFull().Message);
        Assert.Contains("too long", SaveException.NameTooLong("x").Message);
        Assert.Contains("disk that can’t be written", SaveException.ReadOnlyVolume("D").Message);
    }

    [Fact]
    public void A_missing_folder_or_long_name_is_caught_before_writing()
    {
        using var temp = new TempFolder();
        var missing = Assert.Throws<SaveException>(() => SaveException.CheckWritable(Path.Combine(temp.Path, "gone", "x.png")));
        Assert.Equal(SaveException.Reason.FolderMissing, missing.Kind);
        var longName = Assert.Throws<SaveException>(() => SaveException.CheckWritable(temp.File(new string('x', 250) + ".png")));
        Assert.Equal(SaveException.Reason.NameTooLong, longName.Kind);
        SaveException.CheckWritable(temp.File("fine.png"));
    }

    [Fact]
    public void Write_failures_map_to_messages()
    {
        using var temp = new TempFolder();
        var path = temp.File("x.png");
        Assert.Equal(SaveException.Reason.NoPermission, SaveException.From(new UnauthorizedAccessException(), path).Kind);
        Assert.Equal(SaveException.Reason.FolderMissing, SaveException.From(new DirectoryNotFoundException(), path).Kind);
        Assert.Equal(SaveException.Reason.DiskFull, SaveException.From(new IOException("full", unchecked((int)0x80070070)), path).Kind);
        Assert.Equal(SaveException.Reason.Failed, SaveException.From(new IOException("odd"), path).Kind);
    }

    [Fact]
    public void A_failed_batch_reports_what_was_already_saved()
    {
        using var temp = new TempFolder();
        var good = temp.Image("good.png", 40, 40);
        var gone = temp.File("missing.png");
        var failure = Assert.Throws<SaveException>(() => CropOperation.Run([
            (good, new Rect(0, 0, 10, 10), new Size(40, 40)),
            (gone, new Rect(0, 0, 10, 10), new Size(40, 40)),
        ]));
        Assert.Single(failure.SavedBefore);
        Assert.EndsWith("good Cropped.png", failure.SavedBefore[0]);
    }

    [Fact]
    public void A_cancelled_batch_stops()
    {
        using var temp = new TempFolder();
        var a = temp.Image("a.png", 40, 40);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => CropOperation.Run([(a, new Rect(0, 0, 10, 10), new Size(40, 40))], null, cancel.Token));
        Assert.False(File.Exists(temp.File("a Cropped.png")));
    }
}

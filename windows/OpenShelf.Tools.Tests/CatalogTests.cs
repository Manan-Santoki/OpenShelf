using OpenShelf.Convert;
using OpenShelf.Core;

namespace OpenShelf.Tools.Tests;

public class CatalogTests
{
    private static ToolsCatalog Make(out FakeToasts toasts, out FakeProgress progress, out FakeShelves shelves, bool builtIns = true)
    {
        toasts = new FakeToasts();
        progress = new FakeProgress();
        shelves = new FakeShelves();
        return new ToolsCatalog(toasts, progress, shelves, builtIns);
    }

    private static IReadOnlyList<string> Titles(WheelContent content) => content.Entries.Select(e => e.Title).ToList();

    private static WheelRun RunOn(params string[] paths) => new(paths, new ScreenPoint(100, 100), false, "shelf-token");

    [Fact]
    public void A_single_image_gets_the_image_tools_in_the_mac_order()
    {
        var catalog = Make(out _, out _, out _);
        using var temp = new TempFolder();
        var content = catalog.For([temp.Image("a.png", 20, 20)]);
        // Compress, Crop, Resize, Annotate, (Remove BG), Edit, Metadata, Redact, Add BG, (Read QR, OCR): registered later by their own projects.
        Assert.Equal(["Compress", "Crop", "Resize", "Annotate", "Edit", "Metadata", "Redact", "Add BG"], Titles(content));
        Assert.Equal(["tool.compress", "tool.crop", "tool.resize", "tool.annotate", "tool.edit", "tool.metadata", "tool.redact", "tool.addbg"],
            content.Entries.Select(e => e.Id).ToList());
        Assert.All(content.Entries, e => Assert.NotNull(OpenShelf.Ui.Icons.Find(e.Glyph)));
        Assert.Null(content.EmptyMessage);
    }

    [Fact]
    public void Several_images_also_get_combine_last()
    {
        var catalog = Make(out _, out _, out _);
        using var temp = new TempFolder();
        var content = catalog.For([temp.Image("a.png", 20, 20), temp.Image("b.jpg", 20, 20)]);
        Assert.Equal("Combine", Titles(content)[^1]);
        Assert.Equal("tool.combine", content.Entries[^1].Id);
        Assert.Equal(9, content.Entries.Count);
    }

    [Fact]
    public void Image_tools_need_every_file_to_be_an_image()
    {
        var catalog = Make(out _, out _, out _);
        using var temp = new TempFolder();
        var text = temp.File("notes.txt");
        File.WriteAllText(text, "hello");
        var content = catalog.For([temp.Image("a.png", 20, 20), text]);
        Assert.Empty(content.Entries);
        Assert.Equal("No tools for these files", content.EmptyMessage);
        Assert.Empty(catalog.For([]).Entries);
    }

    [Fact]
    public void Media_that_only_converts_says_to_convert_first()
    {
        var catalog = Make(out _, out _, out _);
        using var temp = new TempFolder();
        var mkv = temp.File("clip.mkv");
        File.WriteAllBytes(mkv, [0]);
        var content = catalog.For([mkv]);
        Assert.Empty(content.Entries);
        Assert.Equal("Convert to MP4 first", content.EmptyMessage);
        var wma = temp.File("song.wma");
        File.WriteAllBytes(wma, [0]);
        Assert.Equal("Convert to M4A first", catalog.For([wma]).EmptyMessage);
        Assert.Equal("Convert to MP4 or M4A first", catalog.For([mkv, wma]).EmptyMessage);
        var mp4 = temp.File("clip.mp4");
        File.WriteAllBytes(mp4, [0]);
        Assert.False(ToolsCatalog.NeedsConversionForTools([mp4]));
        Assert.True(ToolsCatalog.AllEditableMedia([mp4], FileKind.Video));
        Assert.False(ToolsCatalog.AllEditableMedia([mp4, mkv], FileKind.Video));
    }

    [Fact]
    public void Another_project_registers_a_tool_and_it_is_ordered_among_the_rest()
    {
        var catalog = Make(out _, out _, out _);
        var ran = new List<string>();
        catalog.Register(new ToolDefinition("tool.ocr", "OCR", "scan-text", p => ToolsCatalog.AllOfKind(p, FileKind.Image), 110, run => { ran.Add("ocr"); return Task.CompletedTask; }));
        catalog.Register(new ToolDefinition("tool.removebg", "Remove BG", "square-user-round", p => ToolsCatalog.AllOfKind(p, FileKind.Image), 50, _ => Task.CompletedTask));
        using var temp = new TempFolder();
        var one = catalog.For([temp.Image("a.png", 20, 20)]);
        Assert.Equal(["Compress", "Crop", "Resize", "Annotate", "Remove BG", "Edit", "Metadata", "Redact", "Add BG", "OCR"], Titles(one));
        one.Entries.Single(e => e.Id == "tool.ocr").Run(RunOn()).Wait();
        Assert.Equal(["ocr"], ran);
    }

    [Fact]
    public void Registering_the_same_id_replaces_the_tool()
    {
        var catalog = Make(out _, out _, out _);
        var before = catalog.Tools.Count;
        catalog.Register(new ToolDefinition("tool.combine", "Combine", "x", p => p.Count > 1 && ToolsCatalog.AllOfKinds(p, FileKind.Image, FileKind.Pdf), 120, _ => Task.CompletedTask));
        Assert.Equal(before, catalog.Tools.Count);
        using var temp = new TempFolder();
        var pdf = temp.File("a.pdf");
        File.WriteAllBytes(pdf, "%PDF-1.4"u8.ToArray());
        Assert.Contains("Combine", Titles(catalog.For([temp.Image("a.png", 20, 20), pdf])));
        Assert.True(catalog.Unregister("tool.combine"));
        Assert.False(catalog.Unregister("tool.combine"));
        Assert.DoesNotContain("Combine", Titles(catalog.For([temp.Image("b.png", 20, 20), temp.Image("c.png", 20, 20)])));
    }

    [Fact]
    public void Ties_keep_registration_order_and_a_broken_predicate_hides_only_that_tool()
    {
        var catalog = Make(out _, out _, out _, builtIns: false);
        catalog.Register(new ToolDefinition("a", "A", "x", _ => true, 5, _ => Task.CompletedTask));
        catalog.Register(new ToolDefinition("b", "B", "x", _ => throw new InvalidOperationException("boom"), 1, _ => Task.CompletedTask));
        catalog.Register(new ToolDefinition("c", "C", "x", _ => true, 5, _ => Task.CompletedTask));
        catalog.Register(new ToolDefinition("d", "D", "x", _ => true, 2, _ => Task.CompletedTask));
        Assert.Equal(["D", "A", "C"], Titles(catalog.For(["anything.txt"])));
    }

    [Fact]
    public void The_wheel_entries_carry_the_option_flag_and_run_delegate()
    {
        var catalog = Make(out _, out _, out _, builtIns: false);
        catalog.Register(new ToolDefinition("opt", "Opt", "x", _ => true, 1, _ => Task.CompletedTask, HasOptions: true));
        var entry = catalog.For(["x.txt"]).Entries.Single();
        Assert.True(entry.HasOptions);
        Assert.Equal("opt", entry.Id);
        Assert.Equal("x", entry.Glyph);
    }

    [Fact]
    public void Combine_runs_with_the_progress_card_and_hands_the_result_to_the_shelf()
    {
        var catalog = Make(out var toasts, out var progress, out var shelves);
        using var temp = new TempFolder();
        var a = temp.Image("a.png", 30, 30);
        var b = temp.Image("b.png", 30, 30);
        var entry = catalog.For([a, b]).Entries.Single(e => e.Id == "tool.combine");
        entry.Run(RunOn(a, b)).Wait();
        Assert.Equal(["Combining into PDF"], progress.Titles);
        Assert.Equal(["Saved · Combined.pdf"], progress.Finished);
        var added = Assert.Single(shelves.Added);
        Assert.Equal([temp.File("Combined.pdf")], added.Paths);
        Assert.Equal("shelf-token", added.Shelf);
        Assert.True(File.Exists(temp.File("Combined.pdf")));
    }

    [Fact]
    public void Combine_failures_are_reported_in_the_card()
    {
        var catalog = Make(out _, out var progress, out var shelves);
        using var temp = new TempFolder();
        var a = temp.Image("a.png", 30, 30);
        var gone = temp.File("gone.png");
        var entry = catalog.For([a, a]).Entries.Single(e => e.Id == "tool.combine");
        entry.Run(RunOn(a, gone)).Wait();
        Assert.Single(progress.Finished);
        Assert.StartsWith("Couldn’t combine", progress.Finished[0]);
        Assert.Empty(shelves.Added);
    }

    [Fact]
    public void An_editor_for_an_unreadable_image_explains_instead_of_opening()
    {
        var catalog = Make(out var toasts, out _, out _);
        using var temp = new TempFolder();
        var broken = temp.File("broken.png");
        File.WriteAllText(broken, "this is not a picture");
        var entry = catalog.For([broken]).Entries.Single(e => e.Id == "tool.crop");
        entry.Run(RunOn(broken)).Wait();
        Assert.Single(toasts.Messages);
        Assert.StartsWith("Couldn’t open image", toasts.Messages[0]);
    }

    [Fact]
    public void The_services_are_available_to_registered_tools()
    {
        var catalog = Make(out var toasts, out var progress, out var shelves);
        Assert.Same(toasts, catalog.Services.Toasts);
        Assert.Same(progress, catalog.Services.Progress);
        Assert.Same(shelves, catalog.Services.Shelves);
    }

    [Fact]
    public void The_empty_catalog_offers_nothing()
    {
        var catalog = Make(out _, out _, out _, builtIns: false);
        Assert.Empty(catalog.Tools);
        Assert.Empty(catalog.For(["a.png"]).Entries);
    }
}

public class FileEditsTests
{
    private static FileEdits<int> Counter(int count, bool same = false) =>
        new(count, _ => 0, (_, v) => v != 0, sameForAll: same);

    [Fact]
    public void Each_file_keeps_its_own_edit_with_the_switch_off()
    {
        var edits = Counter(3);
        edits.Current = 5;
        edits.Index = 1;
        Assert.Equal(0, edits.Current);
        edits.Current = 7;
        Assert.Equal([5, 7, 0], edits.All);
        Assert.Equal([true, true, false], edits.Edited);
        Assert.Equal(2, edits.EditedCount);
    }

    [Fact]
    public void With_the_switch_on_one_edit_applies_to_every_file()
    {
        var edits = Counter(3, same: true);
        edits.Index = 2;
        edits.Current = 9;
        Assert.Equal([9, 9, 9], edits.All);
        Assert.Equal(3, edits.EditedCount);
    }

    [Fact]
    public void Turning_the_switch_on_copies_the_shown_files_edit_and_off_lets_each_keep_its_own()
    {
        var edits = Counter(3);
        edits.Current = 4;
        edits.Index = 1;
        edits.Current = 8;
        edits.SameForAll = true;
        Assert.Equal([8, 8, 8], edits.All);
        edits.SameForAll = false;
        edits.Index = 0;
        edits.Current = 1;
        Assert.Equal([1, 8, 8], edits.All);
    }

    [Fact]
    public void Carry_maps_the_edit_to_each_file_so_a_crop_stays_the_top_half()
    {
        var sizes = new[] { 1000, 500, 200 };
        // The edit is a fraction of the file: 0.5 means half of this file's size.
        var edits = new FileEdits<double>(3, _ => 1.0, (_, v) => v < 1,
            carry: (value, from, to) => value, sameForAll: true);
        edits.Current = 0.5;
        Assert.Equal([0.5, 0.5, 0.5], edits.All);
        // Pixel edits are mapped through fractions of each file's size.
        var pixels = new FileEdits<int>(3, i => sizes[i], (i, v) => v != sizes[i],
            carry: (value, from, to) => (int)Math.Round((double)value / sizes[from] * sizes[to]), sameForAll: true);
        pixels.Current = 500;
        Assert.Equal([500, 250, 100], pixels.All);
        Assert.Equal([true, true, true], pixels.Edited);
    }

    [Fact]
    public void Reset_clears_the_shown_file_or_every_file_while_shared()
    {
        var own = Counter(2);
        own.Current = 3;
        own.Index = 1;
        own.Current = 4;
        own.Reset();
        Assert.Equal([3, 0], own.All);

        var shared = Counter(2, same: true);
        shared.Current = 6;
        shared.Reset();
        Assert.Equal([0, 0], shared.All);
        Assert.Equal(0, shared.EditedCount);
    }

    [Fact]
    public void The_switch_means_nothing_with_one_file()
    {
        var edits = Counter(1, same: true);
        Assert.False(edits.SameForAll);
        Assert.False(edits.IsBatch);
        edits.SameForAll = true;
        Assert.False(edits.SameForAll);
        edits.Current = 2;
        Assert.Equal(1, edits.EditedCount);
    }

    [Fact]
    public void The_button_counts_the_files_that_would_change()
    {
        var edits = Counter(5);
        Assert.Equal("Crop Images", edits.PrimaryTitle("Crop"));
        edits.Current = 1;
        Assert.Equal("Crop 1 Image", edits.PrimaryTitle("Crop"));
        edits.Index = 1;
        edits.Current = 1;
        edits.Index = 2;
        edits.Current = 1;
        Assert.Equal("Crop 3 Images", edits.PrimaryTitle("Crop"));
        Assert.Equal("Save 3 Images", edits.PrimaryTitle("Save"));
        Assert.Equal("Apply", Counter(1).PrimaryTitle("Crop"));
        Assert.Equal("Save with Background", Counter(1).PrimaryTitle("Save", "Save with Background"));
    }

    [Fact]
    public void The_index_is_kept_in_range_and_changes_are_announced()
    {
        var edits = Counter(3);
        var changes = 0;
        edits.Changed += () => changes++;
        edits.Index = 99;
        Assert.Equal(2, edits.Index);
        edits.Index = 2;
        Assert.Equal(1, changes);
        edits.Index = -4;
        Assert.Equal(0, edits.Index);
        edits.Current = 3;
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Loading_one_files_state_does_not_spread_it()
    {
        var edits = Counter(3, same: true);
        edits.SetOnly(1, 11);
        Assert.Equal([0, 11, 0], edits.All);
    }
}

public class PlacementTests
{
    private static readonly System.Windows.Rect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void The_window_is_centred_on_the_drop_point()
    {
        var frame = WindowPlacement.Frame(new ScreenPoint(960, 520), 400, 300, Work);
        Assert.Equal(new System.Windows.Rect(760, 370, 400, 300), frame);
    }

    [Fact]
    public void It_is_kept_inside_the_work_area()
    {
        Assert.Equal(new System.Windows.Rect(0, 0, 400, 300), WindowPlacement.Frame(new ScreenPoint(10, 10), 400, 300, Work));
        Assert.Equal(new System.Windows.Rect(1520, 740, 400, 300), WindowPlacement.Frame(new ScreenPoint(1900, 1030), 400, 300, Work));
    }

    [Fact]
    public void A_window_larger_than_the_screen_is_shrunk_to_it()
    {
        var frame = WindowPlacement.Frame(new ScreenPoint(500, 500), 3000, 2000, Work);
        Assert.Equal(Work, frame);
    }

    [Fact]
    public void A_second_monitor_with_a_negative_origin_works()
    {
        var left = new System.Windows.Rect(-1920, 0, 1920, 1080);
        var frame = WindowPlacement.Frame(new ScreenPoint(-1000, 500), 600, 400, left);
        Assert.Equal(new System.Windows.Rect(-1300, 300, 600, 400), frame);
    }

    [Fact]
    public void The_monitor_lookup_returns_a_work_area_and_dpi()
    {
        var (work, dpi) = WindowPlacement.MonitorAt(new ScreenPoint(100, 100));
        Assert.True(work.Width > 0 && work.Height > 0);
        Assert.InRange(dpi, 0.9, 4.1);
    }
}

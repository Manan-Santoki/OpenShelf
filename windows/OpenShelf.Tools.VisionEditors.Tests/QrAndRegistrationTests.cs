using System.Runtime.InteropServices;
using OpenShelf.Core;
using OpenShelf.Convert;
using OpenShelf.Vision;

namespace OpenShelf.Tools.VisionEditors.Tests;

public class QrToolTests
{
    private static readonly ScreenPoint Here = new(100, 100);

    private static Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>> Finds(params string[] codes) =>
        _ => Task.FromResult<IReadOnlyList<string>>(codes);

    [Fact]
    public async Task One_code_is_copied_and_shown_in_the_toast()
    {
        var toasts = new FakeToasts();
        string? copied = null;
        await QrTool.RunAsync(["a.png"], Here, toasts, Finds("https://example.com/hello"), text => copied = text);
        Assert.Equal("https://example.com/hello", copied);
        Assert.Equal(["Copied QR code · https://example.com/hello"], toasts.Messages);
    }

    [Fact]
    public async Task Several_codes_are_copied_one_per_line_and_counted()
    {
        var toasts = new FakeToasts();
        string? copied = null;
        await QrTool.RunAsync(["a.png", "b.png"], Here, toasts, Finds("first", "second", "third"), text => copied = text);
        Assert.Equal("first\nsecond\nthird", copied);
        Assert.Equal(["Copied 3 QR codes · first"], toasts.Messages);
    }

    [Fact]
    public async Task No_code_found_says_so_and_leaves_the_clipboard_alone()
    {
        var toasts = new FakeToasts();
        var copies = 0;
        await QrTool.RunAsync(["a.png"], Here, toasts, Finds(), _ => copies++);
        Assert.Equal(["No QR code found"], toasts.Messages);
        Assert.Equal(0, copies);
    }

    [Fact]
    public async Task An_unreadable_image_gives_the_failure_message_with_the_reason()
    {
        var toasts = new FakeToasts();
        await QrTool.RunAsync(["a.png"], Here, toasts, _ => throw ConversionException.Unreadable("a.png"), _ => { });
        Assert.StartsWith("Couldn’t read QR code · ", toasts.Messages.Single());
        Assert.Contains("a.png", toasts.Messages.Single());
    }

    [Fact]
    public async Task A_clipboard_in_use_by_another_app_is_reported_not_thrown()
    {
        var toasts = new FakeToasts();
        await QrTool.RunAsync(["a.png"], Here, toasts, Finds("x"), _ => throw new COMException("CLIPBRD_E_CANTOPEN"));
        Assert.StartsWith("Couldn’t read QR code · the clipboard is in use", toasts.Messages.Single());
    }

    [Fact]
    public void A_long_payload_is_cut_for_the_toast_only()
    {
        var url = "https://example.com/" + new string('a', 200);
        var shown = QrTool.Shorten(url);
        Assert.Equal(QrTool.ToastPayloadLimit, shown.Length);
        Assert.EndsWith("…", shown);
        string? copied = null;
        var message = QrTool.Describe([url], text => copied = text);
        Assert.Equal(url, copied);
        Assert.Equal("Copied QR code · " + shown, message);
    }

    [Fact]
    public void A_multi_line_payload_shows_its_first_line()
    {
        Assert.Equal("BEGIN:VCARD", QrTool.Shorten("BEGIN:VCARD\nFN:Ada\nEND:VCARD"));
    }

    [Fact]
    public async Task Reads_a_real_qr_picture_and_a_picture_without_one()
    {
        using var dir = new TempFolder();
        var qr = Pictures.Qr(dir.File("Code.png"), "https://openshelf.example/welcome?from=test");
        var plain = Pictures.Plain(dir.File("Plain.png"));
        var toasts = new FakeToasts();
        string? copied = null;

        await QrTool.RunAsync([qr], Here, toasts, copy: text => copied = text);
        await QrTool.RunAsync([plain], Here, toasts, copy: text => copied = "overwritten");
        await QrTool.RunAsync([dir.File("Missing.png")], Here, toasts, copy: _ => { });

        Assert.Equal("https://openshelf.example/welcome?from=test", copied);
        Assert.Equal("Copied QR code · https://openshelf.example/welcome?from=test", toasts.Messages[0]);
        Assert.Equal("No QR code found", toasts.Messages[1]);
        Assert.StartsWith("Couldn’t read QR code · ", toasts.Messages[2]);
    }
}

public class VisionToolsTests
{
    private static ToolsCatalog Catalog(bool builtIns)
    {
        var catalog = new ToolsCatalog(new FakeToasts(), new FakeProgress(), new FakeShelves(), registerBuiltIns: builtIns);
        VisionTools.Register(catalog);
        return catalog;
    }

    [Fact]
    public void Registers_remove_bg_read_qr_and_ocr_under_the_agreed_ids()
    {
        var tools = Catalog(false).Tools.ToDictionary(t => t.Id);
        Assert.Equal(["tool.ocr", "tool.readqr", "tool.removebg"], tools.Keys.Order());
        Assert.Equal("Remove BG", tools["tool.removebg"].Title);
        Assert.Equal("Read QR", tools["tool.readqr"].Title);
        Assert.Equal("OCR", tools["tool.ocr"].Title);
        Assert.All(tools.Values, t => Assert.NotNull(OpenShelf.Ui.Icons.Find(t.Glyph)));
        Assert.All(tools.Values, t => Assert.False(t.HasOptions));
    }

    [Fact]
    public void Single_image_wheel_follows_the_macs_order()
    {
        var wheel = Catalog(true).For([@"C:\x\a.png"]);
        Assert.Equal(
            ["Compress", "Crop", "Resize", "Annotate", "Remove BG", "Edit", "Metadata", "Redact", "Add BG", "Read QR", "OCR"],
            wheel.Entries.Select(e => e.Title));
    }

    [Fact]
    public void Several_images_add_combine_after_ocr()
    {
        var wheel = Catalog(true).For([@"C:\x\a.png", @"C:\x\b.jpg"]);
        Assert.Equal(["Read QR", "OCR", "Combine"], wheel.Entries.Select(e => e.Title).Skip(9));
    }

    [Fact]
    public void Pdfs_and_mixed_selections_get_ocr_only_from_this_project()
    {
        var catalog = Catalog(true);
        Assert.Equal(["OCR"], catalog.For([@"C:\x\scan.pdf"]).Entries.Select(e => e.Title));
        Assert.Contains("OCR", catalog.For([@"C:\x\scan.pdf", @"C:\x\a.png"]).Entries.Select(e => e.Title));
        Assert.DoesNotContain("Remove BG", catalog.For([@"C:\x\scan.pdf", @"C:\x\a.png"]).Entries.Select(e => e.Title));
        Assert.DoesNotContain("Read QR", catalog.For([@"C:\x\scan.pdf"]).Entries.Select(e => e.Title));
    }

    [Fact]
    public void Other_files_get_none_of_them()
    {
        var wheel = Catalog(false).For([@"C:\x\notes.txt"]);
        Assert.Empty(wheel.Entries);
        Assert.Empty(Catalog(false).For([@"C:\x\a.png", @"C:\x\notes.txt"]).Entries);
        Assert.Empty(Catalog(false).For([@"C:\x\clip.mp4"]).Entries);
    }

    [Fact]
    public void Registering_again_replaces_rather_than_duplicates()
    {
        var catalog = Catalog(false);
        VisionTools.Register(catalog);
        Assert.Equal(3, catalog.Tools.Count);
    }

    [Fact]
    public async Task Read_qr_runs_straight_away_without_opening_a_window()
    {
        using var dir = new TempFolder();
        var qr = Pictures.Qr(dir.File("Code.png"), "hello");
        var toasts = new FakeToasts();
        var catalog = new ToolsCatalog(toasts, new FakeProgress(), new FakeShelves(), registerBuiltIns: false);
        VisionTools.Register(catalog);
        // Not copying to the real clipboard: only an image with no code, so nothing is copied.
        var plain = Pictures.Plain(dir.File("Plain.png"));
        var entry = catalog.For([plain]).Entries.Single(e => e.Id == "tool.readqr");
        await entry.Run(new WheelRun([plain], new ScreenPoint(1, 1), false, null));
        Assert.Equal(["No QR code found"], toasts.Messages);
        Assert.True(File.Exists(qr));
    }
}

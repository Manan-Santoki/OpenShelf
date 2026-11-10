using OpenShelf.Convert;

namespace OpenShelf.Vision.Tests;

public class QrReaderTests
{
    [Fact]
    public async Task Reads_one_code()
    {
        using var dir = new TempDir();
        var path = dir.File("code.png");
        using (var bitmap = Generate.QrBitmap("https://openshelf.app/hello?x=1")) Generate.Save(bitmap, path);
        Assert.Equal(["https://openshelf.app/hello?x=1"], await QrReader.ReadAsync([path]));
    }

    [Fact]
    public async Task Reads_unicode_text()
    {
        using var dir = new TempDir();
        var path = dir.File("code.png");
        using (var bitmap = Generate.QrBitmap("Grüße – 你好")) Generate.Save(bitmap, path);
        Assert.Equal(["Grüße – 你好"], await QrReader.ReadAsync([path]));
    }

    [Fact]
    public async Task Reads_a_light_on_dark_code()
    {
        using var dir = new TempDir();
        var path = dir.File("inverted.png");
        using (var bitmap = Generate.QrBitmap("inverted-code", inverted: true)) Generate.Save(bitmap, path);
        Assert.Equal(["inverted-code"], await QrReader.ReadAsync([path]));
    }

    [Fact]
    public async Task Reads_several_codes_in_reading_order()
    {
        using var dir = new TempDir();
        // Top row: left "first", right "second" (a little lower, still the same row); below: "third".
        var path = Generate.QrSheet(dir.File("sheet.png"), 1400, 900,
            ("second", 760, 40), ("first", 40, 20), ("third", 400, 480));
        Assert.Equal(["first", "second", "third"], await QrReader.ReadAsync([path]));
    }

    [Fact]
    public async Task Joins_codes_from_several_images_without_duplicates()
    {
        using var dir = new TempDir();
        var a = dir.File("a.png"); var b = dir.File("b.png"); var c = dir.File("c.png");
        using (var bitmap = Generate.QrBitmap("one")) Generate.Save(bitmap, a);
        using (var bitmap = Generate.QrBitmap("two")) Generate.Save(bitmap, b);
        using (var bitmap = Generate.QrBitmap("one", module: 5)) Generate.Save(bitmap, c);
        Assert.Equal(["one", "two"], await QrReader.ReadAsync([a, b, c]));
    }

    [Fact]
    public async Task Finds_nothing_in_a_picture_without_a_code()
    {
        using var dir = new TempDir();
        var path = Generate.TextImage(dir.File("text.png"), ["No codes here", "just words"]);
        Assert.Empty(await QrReader.ReadAsync([path]));
    }

    [Fact]
    public async Task Reads_a_small_code_in_a_large_picture()
    {
        using var dir = new TempDir();
        var path = Generate.QrSheet(dir.File("big.png"), 3000, 2000, ("tiny", 2000, 1200));
        Assert.Equal(["tiny"], await QrReader.ReadAsync([path]));
    }

    [Fact]
    public async Task An_unreadable_file_throws_a_conversion_failure()
    {
        using var dir = new TempDir();
        var path = dir.File("broken.png");
        File.WriteAllText(path, "not an image");
        await Assert.ThrowsAsync<ConversionException>(() => QrReader.ReadAsync([path]));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var dir = new TempDir();
        var path = dir.File("code.png");
        using (var bitmap = Generate.QrBitmap("x")) Generate.Save(bitmap, path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QrReader.ReadAsync([path], cts.Token));
    }

    [Fact]
    public void Messages_match_the_mac()
    {
        Assert.Equal("No QR code found", QrReader.NothingFoundMessage);
        Assert.Equal("Copied QR code", QrReader.CopiedMessage(1));
        Assert.Equal("Copied 3 QR codes", QrReader.CopiedMessage(3));
        Assert.Equal("a\nb", QrReader.ClipboardText(["a", "b"]));
    }
}

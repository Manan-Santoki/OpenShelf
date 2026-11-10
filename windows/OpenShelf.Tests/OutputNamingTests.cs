using System.Text;
using OpenShelf.Core;

namespace OpenShelf.Tests;

public sealed class OutputNamingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openshelf-naming-" + Guid.NewGuid().ToString("N"));

    public OutputNamingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);
    private void Touch(string name) => File.WriteAllBytes(P(name), []);
    private static string Name(string path) => Path.GetFileName(path);

    [Fact] public void SavesBesideOriginalWithNewExtension() =>
        Assert.Equal("Page 001.jpg", Name(OutputNaming.Destination(P("Page 001.pdf"), null, "jpg")));

    [Fact] public void AppendsSuffixAndAvoidsOverwriting()
    {
        Touch("Photo Cropped.jpg");
        var d = OutputNaming.Destination(P("Photo.jpg"), "Cropped", "jpg");
        Assert.Equal("Photo Cropped 2.jpg", Name(d));
        Assert.Equal(_dir, Path.GetDirectoryName(d));
    }

    [Fact] public void CounterKeepsCounting()
    {
        Touch("Photo Cropped.jpg"); Touch("Photo Cropped 2.jpg");
        Assert.Equal("Photo Cropped 3.jpg", Name(OutputNaming.Destination(P("Photo.jpg"), "Cropped", "jpg")));
    }

    [Fact] public void FoldersHaveNoExtension() =>
        Assert.Equal("Backup", Name(OutputNaming.Destination(P("Backup.zip"), null, null)));

    [Fact] public void ExistingDirectoryIsACollision()
    {
        Directory.CreateDirectory(P("Backup"));
        Assert.Equal("Backup 2", Name(OutputNaming.Destination(P("Backup.zip"), null, null)));
    }

    [Fact] public void ExistingDirectoryWithExtensionIsACollision()
    {
        Directory.CreateDirectory(P("Photo.png"));
        Assert.Equal("Photo 2.png", Name(OutputNaming.Destination(P("Photo.jpg"), null, "png")));
    }

    [Fact] public void DoesNotDoubleTheSuffix()
    {
        Touch("Photo Compressed.jpg");
        Assert.Equal("Photo Compressed 2.jpg", Name(OutputNaming.Destination(P("Photo Compressed.jpg"), "Compressed", "jpg")));
        // A new format has no clash, so no counter either.
        Assert.Equal("Photo Compressed.heic", Name(OutputNaming.Destination(P("Photo Compressed.jpg"), "Compressed", "heic")));
    }

    [Fact] public void AppendsOtherSuffixes()
    {
        Assert.Equal("Photo Compressed.jpg", Name(OutputNaming.Destination(P("Photo.jpg"), "Compressed", "jpg")));
        Assert.Equal("Photo Cropped Compressed.jpg", Name(OutputNaming.Destination(P("Photo Cropped.jpg"), "Compressed", "jpg")));
    }

    [Fact] public void MatchesTheSuffixAsAWholeWordInAnyCase()
    {
        Assert.Equal(("photo", " compressed"), OutputNaming.SplitSuffix("Compressed", "photo compressed"));
        Assert.Equal(("Decompressed", " Compressed"), OutputNaming.SplitSuffix("Compressed", "Decompressed"));
        Assert.Equal(("Compressed", " Compressed"), OutputNaming.SplitSuffix("Compressed", "Compressed"));
        Assert.Equal(("Photo", ""), OutputNaming.SplitSuffix(null, "Photo"));
        Assert.Equal(("Photo", ""), OutputNaming.SplitSuffix("", "Photo"));
    }

    [Fact] public void TruncatesOnCharacterBoundaries()
    {
        Assert.Equal("abcd", OutputNaming.Truncated("abcdef", 4));
        Assert.Equal("\U0001F600", OutputNaming.Truncated("\U0001F600\U0001F600\U0001F600", 6));
        Assert.Equal("short", OutputNaming.Truncated("short", 100));
    }

    [Fact] public void LongNamesKeepTheirSuffixAndFit()
    {
        var d = OutputNaming.Destination(P(new string('L', 236) + ".jpg"), "Redacted", "jpg");
        var name = Name(d);
        Assert.EndsWith(" Redacted.jpg", name);
        Assert.True(Encoding.UTF8.GetByteCount(name) <= OutputNaming.MaxNameBytes);
    }

    [Fact] public void DotFilesProduceVisibleOutputs()
    {
        Assert.Equal("hidden.png", Name(OutputNaming.Destination(P(".hidden.jpg"), null, "png")));
        Assert.Equal("hidden Compressed.jpg", Name(OutputNaming.Destination(P(".hidden.jpg"), "Compressed", "jpg")));
        Assert.Equal("Untitled", OutputNaming.VisibleName("..."));
    }

    [Fact] public void TrailingDotsAndSpacesAreStripped()
    {
        Assert.Equal("Report.pdf", Name(OutputNaming.UniquePath(_dir, "Report. . ", "pdf")));
        Assert.Equal("Untitled.pdf", Name(OutputNaming.UniquePath(_dir, " . ", "pdf")));
        Assert.Equal("Notes", Name(OutputNaming.UniquePath(_dir, "Notes...", null)));
    }

    [Fact] public void UniquePathCountsFromTwo()
    {
        Touch("A.txt");
        Assert.Equal("A 2.txt", Name(OutputNaming.UniquePath(_dir, "A", "txt")));
    }
}

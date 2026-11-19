using OpenShelf.Vision;
using Xunit;

namespace OpenShelf.Vision.Tests;

public class OcrLanguagePackTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans-CN")]
    public void TheScriptNamesTheLanguageAndFallsBackToItsFamily(string tag)
    {
        var script = OcrLanguagePack.Script(tag);
        Assert.Contains($"Language.OCR~~~{tag}~*", script);
        Assert.Contains($"Language.OCR~~~{tag.Split('-')[0]}-*", script);
    }

    [Theory]
    [InlineData("en-US'; Remove-Item C: -Recurse; '")]
    [InlineData("")]
    [InlineData("e")]
    public void OnlyRealLanguageTagsReachPowerShell(string tag) =>
        Assert.Throws<ArgumentException>(() => OcrLanguagePack.Script(tag));

    [Fact]
    public void PreferredTagIsALanguageTag() =>
        Assert.Matches("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$", OcrLanguagePack.PreferredTag());
}

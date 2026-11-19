using OpenShelf.Settings;
using Xunit;

namespace OpenShelf.Settings.Tests;

public class DocumentSettingsTests
{
    [Fact]
    public void DefaultsKeepTextAsTypedAndUseOffice()
    {
        Assert.True(DocumentSettings.Default.UseMicrosoftOffice);
        Assert.False(DocumentSettings.Default.SmartPunctuation);
        Assert.Equal(DocumentSettings.Default, DocumentSettings.Decode(null));
        Assert.Equal(DocumentSettings.Default, DocumentSettings.Decode("not json"));
    }

    [Fact]
    public void RoundTrips()
    {
        var changed = new DocumentSettings { UseMicrosoftOffice = false, SmartPunctuation = true };
        Assert.Equal(changed, DocumentSettings.Decode(changed.Stored));
        Assert.Equal(new DocumentSettings { SmartPunctuation = true }, DocumentSettings.Decode("{\"smartPunctuation\":true}"));
    }
}

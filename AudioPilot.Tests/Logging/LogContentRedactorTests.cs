using AudioPilot.Logging;

namespace AudioPilot.Tests.Logging;

public sealed class LogContentRedactorTests
{
    [Theory]
    [InlineData("C:/Users/ExampleUser/Private Folder/settings.json")]
    [InlineData("//PrivateServer/Private Share/settings.json")]
    [InlineData(@"C:\Users\ExampleUser\Private.Folder\Private Folder\settings.json")]
    [InlineData(@"C:\Users\ExampleUser\Private Folder")]
    public void Sanitize_RemovesWholeWindowsPathIncludingSpacesAndDottedFolders(string path)
    {
        string sanitized = LogContentRedactor.Sanitize($"Cannot read {path}; reason=access-denied");

        Assert.DoesNotContain("Private", sanitized, StringComparison.Ordinal);
        Assert.Contains("reason=access-denied", sanitized, StringComparison.Ordinal);
        Assert.Equal(sanitized, LogContentRedactor.Sanitize(sanitized));
    }

    [Fact]
    public void Sanitize_MediaSnapshot_RedactsApostrophesAndPreservesUsefulDiagnostics()
    {
        const string log = "commandSeq=15 outcome=loading title='Don't Skip This Fixture', artist='Fixture Artist's Name', album='Artist's Album', source='id[private-browser]', positionSec='12.5', status='Playing'";

        string sanitized = LogContentRedactor.Sanitize(log);

        Assert.DoesNotContain("Don't Skip This Fixture", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Fixture Artist", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Name", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Album", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-browser", sanitized, StringComparison.Ordinal);
        Assert.Contains("commandSeq=15 outcome=loading", sanitized, StringComparison.Ordinal);
        Assert.Contains("positionSec='12.5', status='Playing'", sanitized, StringComparison.Ordinal);
        Assert.Contains($"artist='{LogPrivacy.RedactedLabel("Fixture Artist's Name")}'", sanitized, StringComparison.Ordinal);
        Assert.Equal(sanitized, LogContentRedactor.Sanitize(sanitized));
    }

    [Theory]
    [InlineData("device")]
    [InlineData("process")]
    [InlineData("session")]
    [InlineData("id")]
    public void Sanitize_UnquotedPrivacyLabel_RedactsRawValueAndPreservesExistingHash(string kind)
    {
        string expected = $"source={kind}[{LogPrivacy.RedactedLabel("Private name")}]";

        Assert.Equal(expected, LogContentRedactor.Sanitize($"source={kind}[Private name]"));
        Assert.Equal(expected, LogContentRedactor.Sanitize(expected));
    }

    [Fact]
    public void Sanitize_UnexpectedMetricValues_RemainRedacted()
    {
        string sanitized = LogContentRedactor.Sanitize("status='private status' positionSec='private position'");

        Assert.DoesNotContain("private status", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("private position", sanitized, StringComparison.Ordinal);
    }
}

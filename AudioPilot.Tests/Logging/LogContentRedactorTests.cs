using AudioPilot.Logging;

namespace AudioPilot.Tests.Logging;

public sealed class LogContentRedactorTests
{
    [Fact]
    public void Sanitize_MediaSnapshot_RedactsApostrophesAndPreservesUsefulDiagnostics()
    {
        const string log = "commandSeq=15 outcome=loading title='Don't Stop', artist='Guns N' Roses', album='Artist's Album', source='id[private-browser]', positionSec='12.5', status='Playing'";

        string sanitized = LogContentRedactor.Sanitize(log);

        Assert.DoesNotContain("Don't Stop", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Guns N", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Roses", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Album", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-browser", sanitized, StringComparison.Ordinal);
        Assert.Contains("commandSeq=15 outcome=loading", sanitized, StringComparison.Ordinal);
        Assert.Contains("positionSec='12.5', status='Playing'", sanitized, StringComparison.Ordinal);
        Assert.Contains($"artist='{LogPrivacy.RedactedLabel("Guns N' Roses")}'", sanitized, StringComparison.Ordinal);
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

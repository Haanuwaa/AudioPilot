using AudioPilot.Logging;

namespace AudioPilot.Tests.Logging;

public sealed class LogContentRedactorTests
{
    [Theory]
    [InlineData("target=device[len=4 hash=ABCDEF12] PrivateSuffix]")]
    [InlineData("source=id[len=4 hash=ABCDEF12] PrivateSuffix]")]
    [InlineData("title=len=4 hash=ABCDEF12 PrivateSuffix")]
    [InlineData("title='len=4 hash=ABCDEF12' PrivateSuffix")]
    [InlineData("Routine 'len=4 hash=ABCDEF12' PrivateSuffix' failed.")]
    [InlineData("target='device[len=4 hash=ABCDEF12]' PrivateSuffix'")]
    [InlineData("target=device[len=4 hash=ABCDEF12], PrivateSuffix]")]
    [InlineData("description=len=4 hash=ABCDEF12; PrivateSuffix")]
    public void Sanitize_DoesNotTrustPartialRedactionPrefixes(string content)
    {
        string sanitized = LogContentRedactor.Sanitize(content);
        Assert.DoesNotContain("PrivateSuffix", sanitized);
        Assert.Equal(sanitized, LogContentRedactor.Sanitize(sanitized));
    }

    [Theory]
    [InlineData("device[raw:\"Private truncated value")]
    [InlineData("title=raw:\"Private truncated escape\\")]
    public void Sanitize_RedactsIncompleteRawFrames(string content)
    {
        string sanitized = LogContentRedactor.Sanitize(content);
        Assert.DoesNotContain("Private", sanitized);
        Assert.Equal(sanitized, LogContentRedactor.Sanitize(sanitized));
    }

    [Theory]
    [InlineData("Private ] ' \" name\nPrivate next record")]
    [InlineData("len=4 hash=ABCDEF12] PrivateSuffix")]
    [InlineData("Private 🎵 日本語\\path")]
    public void Sanitize_DecodesCompleteFramedRawValues(string raw)
    {
        string framed = LogPrivacy.FormatRawValue(raw);
        Assert.DoesNotContain('\n', framed);
        string sanitized = LogContentRedactor.Sanitize($"source=device[{framed}]");
        Assert.Equal($"source=device[{LogPrivacy.RedactedLabel(raw)}]", sanitized);
        Assert.Equal(sanitized, LogContentRedactor.Sanitize(sanitized));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Sanitize_RedactsLegacyExceptionContinuationsAndPreservesNextEntry(string newline)
    {
        string raw = string.Join(newline,
            "[2026-10-25 10:00:00.000] [Error] [Fixture] read-failed",
            "  Exception: IOException (HRESULT=0x80004005): PrivateFirst",
            "PrivateContinuation",
            "  Inner exception: InvalidOperationException: PrivateInner",
            "PrivateInnerContinuation",
            "[2026-10-25 10:00:01.000] [Info] [Fixture] recovery-success");
        string sanitized = LogContentRedactor.Sanitize(raw);
        Assert.DoesNotContain("Private", sanitized);
        Assert.Contains("Exception: IOException (HRESULT=0x80004005):", sanitized);
        Assert.Contains("Inner exception: InvalidOperationException:", sanitized);
        Assert.Contains("[Info] [Fixture] recovery-success", sanitized);
        Assert.Equal(sanitized, LogContentRedactor.Sanitize(sanitized));
    }

    [Theory]
    [InlineData("routineName=Private Office flow=output")]
    [InlineData("title=Private Browser Page")]
    [InlineData("pattern=Private.*Document reason=invalid")]
    [InlineData("networks[Private Office, Private Guest]")]
    [InlineData("target=device[Headset [Private Office] Name]")]
    [InlineData("target=device[Headset ] Private Office]")]
    [InlineData("title='Owner's Private Title' status='Playing'")]
    [InlineData("Routine 'Owner's Private Office' failed.")]
    [InlineData("Imported settings contain an unsupported top-level property: PrivateProperty.")]
    [InlineData("Cannot open \"C:\\Users\\ExampleUser\\Folder, Private Folder\\settings.json\".")]
    public void Sanitize_RemovesPrivateValuesWithAmbiguousDelimiters(string content)
    {
        string result = LogContentRedactor.Sanitize(content);
        Assert.DoesNotContain("Private", result, StringComparison.Ordinal);
        Assert.Equal(result, LogContentRedactor.Sanitize(result));
    }

    [Theory]
    [InlineData("C:/Users/ExampleUser/Private Folder/settings.json")]
    [InlineData("//PrivateServer/Private Share/settings.json")]
    [InlineData(@"C:\Users\ExampleUser\Private.Folder\Private Folder\settings.json")]
    [InlineData(@"C:\Users\ExampleUser\Private Folder")]
    [InlineData(@"C:\Users\ExampleUser\Owner's Private Folder\settings.json")]
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

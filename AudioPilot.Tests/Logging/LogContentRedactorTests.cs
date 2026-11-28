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

    [Fact]
    public void Sanitize_PreservesLegacyOutputAcrossMixedNewlines()
    {
        const string input = "[2026-11-18 00:00:00.000] [Info] [Fixture] device[Private A]\r\n"
            + "Exception: InvalidOperationException: private first line\r\n"
            + "  continuation 'secret' and √\r\n"
            + "  Inner exception: IOException: private second line\n"
            + "        internal details α\r"
            + "[2026-11-18 00:00:01.000] [Info] [Fixture] device[len=2 hash=ABCD1234] volume=50\r\n";
        const string expected = "[2026-11-18 00:00:00.000] [Info] [Fixture] device[len=9 hash=3E27EE67]\r\n"
            + "Exception: InvalidOperationException: len=49 hash=92CDFCC8\n"
            + "  Inner exception: IOException: len=46 hash=EB095833\r"
            + "[2026-11-18 00:00:01.000] [Info] [Fixture] device[len=2 hash=ABCD1234] volume=50\r\n";

        Assert.Equal(expected, LogContentRedactor.Sanitize(input));
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

    [Fact]
    public void Sanitize_PreservesStackTracesForRedactedAndFramedExceptions()
    {
        const string redactedLog =
            "[2026-11-28 14:00:00.000] [Error] [SettingsService] save-failed [SaveSettings]\n"
            + "  Exception: IOException (HRESULT=0x80070020): len=18 hash=A1B2C3D4\n"
            + "   at AudioPilot.Services.Configuration.SettingsService.SaveSettings()\n"
            + "   at AudioPilot.ViewModels.AppViewModel.ApplySettingsAsync()";
        string sanitizedRedacted = LogContentRedactor.Sanitize(redactedLog);
        Assert.Contains("len=18 hash=A1B2C3D4", sanitizedRedacted, StringComparison.Ordinal);
        Assert.Contains("at AudioPilot.Services.Configuration.SettingsService.SaveSettings()", sanitizedRedacted, StringComparison.Ordinal);
        Assert.Contains("at AudioPilot.ViewModels.AppViewModel.ApplySettingsAsync()", sanitizedRedacted, StringComparison.Ordinal);
        Assert.Equal(sanitizedRedacted, LogContentRedactor.Sanitize(sanitizedRedacted));

        string framedMessage = LogPrivacy.FormatRawValue("Cannot open C:\\Users\\PrivateUser\\secret.json");
        string framedLog =
            $"[2026-11-28 14:00:01.000] [Error] [SettingsService] save-failed\n"
            + $"  Exception: IOException (HRESULT=0x80070020): {framedMessage}\n"
            + "   at AudioPilot.Services.Configuration.SettingsService.SaveSettings()";
        string sanitizedFramed = LogContentRedactor.Sanitize(framedLog);
        Assert.DoesNotContain("PrivateUser", sanitizedFramed, StringComparison.Ordinal);
        Assert.Contains("at AudioPilot.Services.Configuration.SettingsService.SaveSettings()", sanitizedFramed, StringComparison.Ordinal);
        Assert.Equal(sanitizedFramed, LogContentRedactor.Sanitize(sanitizedFramed));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Sanitize_PreservesTrailingMethodTagAfterPrivacyLabelsAndSensitiveFields(string lineEnding)
    {
        string[] lines =
        [
            "[2026-11-28 14:00:00.000] [Info] [VolumeControlService] applied processName=process[len=6 hash=5678EF01] [ApplySavedVolume]",
            "[2026-11-28 14:00:00.001] [Info] [Routine] updated routineName=len=8 hash=ABCDEF12 [SaveRoutine]",
            "[2026-11-28 14:00:00.002] [Info] [VolumeControlService] bare processName=process[len=6 hash=5678EF01]",
            "[2026-11-28 14:00:00.003] [Info] [Routine] bare routineName=len=8 hash=ABCDEF12",
        ];

        foreach (string singleLine in lines)
        {
            Assert.Equal(singleLine, LogContentRedactor.Sanitize(singleLine));
        }

        string multiLine = string.Join(lineEnding, lines) + lineEnding;
        Assert.Equal(multiLine, LogContentRedactor.Sanitize(multiLine));
    }
}

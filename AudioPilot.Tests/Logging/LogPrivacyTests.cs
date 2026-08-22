using System.Security.Cryptography;
using System.Text;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Logging;

[CollectionDefinition("LogPrivacy", DisableParallelization = true)]
public sealed class LogPrivacyTestCollection;

[Collection("LogPrivacy")]
public sealed class LogPrivacyTests : IDisposable
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CliRedaction_OverridesDisabledLogRedaction(bool json)
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = false } });
        string volume = CliOutputFormatter.FormatVolumeResult("master", 50, false, json, "volume-get-success", "private-device", redactOutput: true);
        string setting = CliOutputFormatter.FormatSettingValue("Private Speakers", redactOutput: true);
        Assert.DoesNotContain("private-device", volume, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Speakers", setting, StringComparison.Ordinal);
        Assert.Contains("hash=", volume, StringComparison.Ordinal);
        Assert.Contains("hash=", setting, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        LogPrivacy.ApplySettings(null);
    }

    [Fact]
    public void Label_RedactsByDefault()
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = true } });

        string result = LogPrivacy.Label("Desk Speakers");

        Assert.StartsWith("len=", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Desk Speakers", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExceptionMessages_HonorPrivacyWithoutLosingTypeOrHResult(bool redact)
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = redact } });
        using var logger = Logger.CreateInMemoryForTests();
        logger.Error("Privacy", "operation-failed", exception: new System.Runtime.InteropServices.COMException("Private Call Title", unchecked((int)0x88890004)));
        string text = logger.DisposeAndReadLogTextForTests();
        Assert.Contains("COMException", text, StringComparison.Ordinal);
        Assert.Contains("0x88890004", text, StringComparison.Ordinal);
        Assert.Equal(!redact, text.Contains("Private Call Title", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Desk Speakers", 1)]
    [InlineData("Café 🎵 日本語", 1)]
    [InlineData("Café 🎵 日本語", 200)]
    [InlineData("\uD800", 1)]
    public void RedactedLabel_PreservesHashForUtf8AndLargeInputs(string unit, int repetitions)
    {
        string value = string.Concat(Enumerable.Repeat(unit, repetitions));
        string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 4);

        Assert.Equal($"len={value.Length} hash={expectedHash}", LogPrivacy.RedactedLabel($"  {value}  "));
    }

    [Fact]
    public void Label_ReturnsRawValue_WhenRedactionDisabled()
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = false } });

        string result = LogPrivacy.Label("Desk Speakers");

        Assert.Equal("Desk Speakers", result);
    }

    [Fact]
    public void ApplySettings_Null_ResetsToPrivacyFirstDefault()
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = false } });
        LogPrivacy.ApplySettings(null);

        Assert.True(LogPrivacy.IsRedactionEnabled);
    }

    [Fact]
    public void MixerLogIdentifiers_RedactSessionLabelsAndProcessIds()
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = true } });

        string session = MixerViewModel.FormatSessionIdForLog("name:Private Call");
        string process = MixerViewModel.FormatProcessIdForLog(4242);

        Assert.StartsWith("session[len=", session, StringComparison.Ordinal);
        Assert.StartsWith("id[len=", process, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Call", session, StringComparison.Ordinal);
        Assert.DoesNotContain("4242", process, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalLoggerDiagnostic_StripsAbsolutePathFromFallbackException()
    {
        const string rawPath = @"C:\Users\ExampleUser\AudioPilot\settings.json";
        var exception = new IOException($"Could not read {rawPath}");

        string diagnostic = Logger.FormatInternalDiagnosticPayload("logger-shutdown-failed", exception);

        Assert.Contains("hash=", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPath, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExampleUser", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("C:/Users/ExampleUser/Private Folder/settings.json")]
    [InlineData("//PrivateServer/Private Share/settings.json")]
    public void ExceptionRedaction_RemovesAlternateWindowsPathSeparators(string path)
    {
        Assert.DoesNotContain("Private", Logger.SanitizeExceptionMessage($"Cannot read {path}"), StringComparison.Ordinal);
        Assert.DoesNotContain("Private", Logger.SanitizeExceptionDetails($"at Example.Read() in {path}:line 12"), StringComparison.Ordinal);
    }

    [Fact]
    public void PathRedaction_DoesNotMistakeAnHttpsSchemeForADriveLetter()
    {
        const string message = "Request failed: https://example.invalid/releases";

        Assert.Equal(message, Logger.SanitizeExceptionMessage(message));
        Assert.Equal(message, LogContentRedactor.Sanitize(message));
    }
}

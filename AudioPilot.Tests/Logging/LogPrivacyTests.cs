using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Logging;

[CollectionDefinition("LogPrivacy", DisableParallelization = true)]
public sealed class LogPrivacyTestCollection;

[Collection("LogPrivacy")]
public sealed class LogPrivacyTests : IDisposable
{
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

        Assert.Contains("<path>", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPath, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExampleUser", diagnostic, StringComparison.OrdinalIgnoreCase);
    }
}

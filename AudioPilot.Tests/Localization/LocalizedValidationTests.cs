using System.Text.Json;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.Tests.Localization;

public sealed class LocalizedValidationTests
{
    [Fact]
    public void RoutineDiagnosticsCarryResourceKeysAndKeepUserNamesAndEnglishContracts()
    {
        var settings = new Settings();
        settings.Routines.Items =
        [
            new() { Name = "Private routine", HotkeyCycleGroup = "Invalid group", RestorePreviousAudioOnDeactivate = true,
                RouteToApplication = true, OutputMuteAction = (RoutineMuteAction)999,
                CommunicationsOutput = new() { Id = "microphone", Playback = false },
                Triggers = [new() { Kind = RoutineTriggerKind.Application }] },
            new() { Name = "", ShowInTrayMenu = true },
            new() { Name = new string('a', AudioRoutine.MaxNameLength + 1) },
        ];
        var diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, [], []).Warnings
            .Where(item => item.Code.StartsWith("invalid-routine-", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(diagnostics);
        var spanish = new LocalizationService("es");
        foreach (var diagnostic in diagnostics)
        {
            var message = Assert.IsType<LocalizedText>(diagnostic.UiMessage);
            var guidance = Assert.IsType<LocalizedText>(diagnostic.UiSuggestedAction);
            Assert.Equal(diagnostic.Message, LocalizationService.English.Format(message));
            Assert.Equal(diagnostic.SuggestedAction, LocalizationService.English.Format(guidance));
            Assert.NotEqual(diagnostic.Message, spanish.Format(message));
            Assert.NotEqual(diagnostic.SuggestedAction, spanish.Format(guidance));
            Assert.DoesNotContain("Text unavailable.", spanish.Format(message), StringComparison.Ordinal);
        }
        var mute = Assert.Single(diagnostics, item => item.Code == "invalid-routine-mute-0");
        Assert.Contains("Private routine", spanish.Format(mute.UiMessage!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", "Imported settings must be a JSON object.", "objeto JSON")]
    [InlineData("{\"SchemaVersion\":\"9.0.0\"}", "Imported settings use unsupported schema version 9.0.0. Current supported version is 1.0.0.", "9.0.0")]
    [InlineData("{\"SchemaVersion\":\"1.0.0\",\"PrivateName\":true}", "Imported settings contain an unsupported top-level property: PrivateName.", "PrivateName")]
    public void ImportValidationKeepsItsExceptionContractAndCarriesStructuredUiText(string json, string invariant, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ParseImportedSettings(json, new Settings(), true));
        Assert.Equal(invariant, error.Message);
        var present = SettingsFailure.ForDialog(error, new("Message_FailedToImportSettingsFrom0", "settings.json"));
        string spanish = present(new LocalizationService("es"));
        Assert.Contains(expected, spanish, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(invariant, spanish);
        Assert.Equal(invariant, present(LocalizationService.English));
    }

    [Fact]
    public void DuplicatePropertyRetainsThePropertyNameAndJsonException()
    {
        var error = Assert.Throws<JsonException>(() => SettingsTransferService.ParseImportedSettings(
            "{\"SchemaVersion\":\"1.0.0\",\"Language\":\"en\",\"language\":\"es\"}", new Settings(), true));
        Assert.Equal("Duplicate JSON property: language.", error.Message);
        Assert.Equal("Propiedad JSON duplicada: language.",
            SettingsFailure.ForDialog(error, new("Validation_ImportJsonInvalid"))(new LocalizationService("es")));
    }

    [Fact]
    public void ShortcutDiagnosticsKeepEnglishForAutomationAndRenderTheInvalidInputVerbatim()
    {
        var settings = new Settings();
        settings.Hotkeys.Media.NextTrack = "PrivateInvalidInput";
        var diagnostic = Assert.Single(SettingsValidationService.EvaluateDiagnostics(settings, [], []).Warnings,
            item => item.Code == "invalid-hotkey-next-track-hotkey");
        Assert.Equal("Next track hotkey value 'PrivateInvalidInput' is invalid.", diagnostic.Message);
        Assert.Equal("Set a valid combination (example: Ctrl+Alt+H) or clear it with config set next-track-hotkey \"\".", diagnostic.SuggestedAction);
        var language = new LocalizationService("es");
        string spanish = language.Format(Assert.IsType<LocalizedText>(diagnostic.UiMessage));
        Assert.Contains("pista siguiente", spanish, StringComparison.Ordinal);
        Assert.Contains("PrivateInvalidInput", spanish, StringComparison.Ordinal);
    }
}

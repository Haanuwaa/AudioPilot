using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Localization;

public sealed partial class LocalizationServiceTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void RevisedUiWordingProjectsDiagnosticTemplatesAndPreservesTheirArguments(string language)
    {
        var localization = new LocalizationService(language);
        (string Key, string Source, string Argument)[] cases =
        [
            ("Message_ReservedByWindows0ChooseADifferent", "Reserved by Windows ({0}). Choose a different hotkey.", "Win+L"),
            ("Message_ConflictingActions0ChooseADifferentHotkey", "Conflicting actions: {0}. Choose a different hotkey.", "My hotkey app"),
            ("Message_And0MoreHotkeyChanges", "…and {0} more hotkey changes.", "3"),
            ("Message_Click0ToSave", "Click {0} to save.", "Apply settings"),
            ("Message_FixTheIssueThenClick0", "Fix the issue, then click {0}.", "Apply settings"),
        ];
        foreach (var (key, source, argument) in cases)
        {
            string projectedArgument = key is "Message_Click0ToSave" or "Message_FixTheIssueThenClick0"
                ? localization["Common_Apply"] : argument;
            string expected = localization.FormatKey(key, projectedArgument);
            Assert.Equal(expected, AppMessages.Translate(string.Format(CultureInfo.InvariantCulture, source, argument), localization));
            Assert.Equal(expected, AppMessages.Translate(LocalizationService.English.FormatKey(key, argument), localization));
        }
        const string raw = "Windows error: My hotkey app could not click Apply settings (0x80070005).";
        Assert.Equal(raw, AppMessages.Translate(raw, localization));
        Assert.Equal(localization["Message_PerApplicationAudioAssignmentsWereResetRunning"], AppMessages.Translate(
            "Per-application audio assignments were reset. Running applications may need to restart audio playback before they follow the default devices again.", localization));
    }

    [Theory]
    [InlineData("en", "Output volume", "Input volume")]
    [InlineData("es", "Volumen de salida", "Volumen de entrada")]
    public void EndpointVolumePresentationProjectsStableLabelsWithoutChangingSessionIdentity(string language, string output, string input)
    {
        var localization = new LocalizationService(language);
        var endpoint = new AudioSessionItem("Master Volume", 70, true, false);
        var application = new AudioSessionItem("Master Volume", 70, false, false, processId: 123);
        Assert.Equal(output, localization.FormatValue(endpoint.DisplayLabel));
        Assert.Equal("Master Volume", endpoint.DisplayName);
        Assert.Equal("Master Volume", localization.FormatValue(application.DisplayLabel));
        Assert.Equal(localization["Ui_MasterVolumeUpHotkey"], AppMessages.Translate("Master volume up hotkey", localization));
        Assert.Equal(localization.FormatKey("Message_UnsavedChangesList", localization["Message_MasterVolumeStep"]),
            AppMessages.Translate("Unsaved: Master volume step.", localization));
        Assert.Equal(localization.FormatKey("Message_MasterVolume0", "70"), AppMessages.Translate("Output volume 70%", localization));
        Assert.True(AppMessages.CanTranslateOverlayHeading("Output volume 70%"));
        var inputEndpoint = new AudioSessionItem("Microphone Volume", 60, false, true,
            endpointName: "Virtual input", endpointRole: NAudio.CoreAudioApi.Role.Communications);
        Assert.Equal(input, localization.FormatValue(inputEndpoint.DisplayLabel));
        Assert.Equal("Microphone Volume", inputEndpoint.DisplayName);
        string help = localization.FormatValue(inputEndpoint.EndpointDescription);
        Assert.Contains(localization["Ui_InputVolumeHelp"], help, StringComparison.Ordinal);
        Assert.Contains("Virtual input", help, StringComparison.Ordinal);
        Assert.Contains(localization["Ui_Communications"], help, StringComparison.Ordinal);
        Assert.Equal(localization["Ui_MicrophoneVolumeUpHotkey"], AppMessages.Translate("Microphone volume up hotkey", localization));
        Assert.Equal(localization.FormatKey("Message_MicrophoneVolume0", "60"), AppMessages.Translate("Input volume 60%", localization));
        Assert.True(AppMessages.CanTranslateOverlayHeading("Input volume 60%"));
    }

    private static readonly string[] LocalizedProjects = ["AudioPilot", "AudioPilot.Updater"];

    [Fact]
    public void BundledCatalogsHaveMatchingKeysAndFormatArguments_AndResolveThroughThePublishedResources()
    {
        string root = Path.Combine(TestRepository.Root, "AudioPilot.Core", "Localization");
        var english = Read(Path.Combine(root, "Strings.resx"));
        Assert.Equal(english.Count, english.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var en = new LocalizationService("en");
        foreach (LanguageOption option in LocalizationService.Languages.Where(option => option.Code != "System"))
        {
            var translated = Read(Path.Combine(root, option.Code == "en" ? "Strings.resx" : $"Strings.{option.Code}.resx"));
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            var localization = new LocalizationService(option.Code);
            foreach (string key in english.Keys)
            {
                Assert.Equal(english[key], en[key]);
                Assert.Equal(translated[key], localization[key]);
                Assert.Equal(CompositeFormat.Parse(english[key]).MinimumArgumentCount, CompositeFormat.Parse(translated[key]).MinimumArgumentCount);
                object[] arguments = [.. Enumerable.Range(0, CompositeFormat.Parse(english[key]).MinimumArgumentCount).Select(index => (object)$"⟦argument-{index}⟧")];
                string original = string.Format(CultureInfo.InvariantCulture, english[key], arguments);
                string formatted = string.Format(CultureInfo.InvariantCulture, translated[key], arguments);
                foreach (string argument in arguments.Cast<string>().Where(argument => original.Contains(argument, StringComparison.Ordinal)))
                    Assert.Contains(argument, formatted, StringComparison.Ordinal);
                if (key != "Common_Empty") Assert.False(string.IsNullOrWhiteSpace(translated[key]), key);
            }
        }
        foreach (string path in LocalizedProjects.SelectMany(project => Directory.EnumerateFiles(Path.Combine(TestRepository.Root, project), "*.xaml", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            foreach (Match reference in MyRegex().Matches(File.ReadAllText(path)))
                Assert.True(english.ContainsKey(reference.Groups[1].Value), $"Unknown resource in {path}: {reference.Value}");

        static Dictionary<string, string> Read(string path) => XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(element => (string)element.Attribute("name")!, element => (string)element.Element("value")!);
    }

    [Theory]
    [InlineData("es-BO", "es", "Salida")]
    [InlineData("en-GB", "en", "Output")]
    [InlineData("fr-FR", "en", "Output")]
    public void SystemLanguageUsesTheSupportedParent_AndSwitchingPreservesRegionalCulture(string system, string expected, string output)
    {
        CultureInfo regional = CultureInfo.CurrentCulture;
        CultureInfo ui = CultureInfo.CurrentUICulture;
        var localization = new LocalizationService(systemCulture: CultureInfo.GetCultureInfo(system));
        Assert.Equal("System", localization.Language);
        Assert.Equal("System", LocalizationService.NormalizeLanguage(" system "));
        Assert.Equal(expected, localization.EffectiveLanguage);
        Assert.Equal(output, localization["Common_Output"]);
        localization.SetLanguage("es");
        Assert.Equal("Audífonos · Disponible", localization.Format(new("Picker_Help", new LocalizedText("DeviceType_Headphones"), new LocalizedText("Picker_Available"))));
        Assert.Same(regional, CultureInfo.CurrentCulture);
        Assert.Same(ui, CultureInfo.CurrentUICulture);
        int revision = localization.Revision;
        localization.SetLanguage(" ES ");
        Assert.Equal(revision, localization.Revision);
    }

    [GeneratedRegex(@"(?:\{loc:Loc |\bFormat=)(\w+)[},]")]
    private static partial Regex MyRegex();

    [Theory]
    [InlineData("en", "es-BO")]
    [InlineData("es", "en-US")]
    public void FormattingPreservesRegionalValuesAndCallerArguments(string language, string region)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(region);
            var localization = new LocalizationService(language);
            object?[] values = [1234.5, new DateTime(2026, 11, 14, 17, 15, 0)];
            object?[] original = [.. values];
            string expected = string.Format(CultureInfo.CurrentCulture, localization["Picker_Help"], values);
            Assert.Equal(expected, localization.FormatKey("Picker_Help", values));
            Assert.Equal(expected, localization.Format(new("Picker_Help", values)));
            Assert.Equal(original, values);
            Assert.Equal(localization["Common_Output"], localization.FormatKey("Common_Output"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void NestedMessagesRemainReusableAcrossLanguageChangesAndCovariantArgumentArrays()
    {
        var localization = new LocalizationService("en");
        var nested = new LocalizedText("Picker_Help", new LocalizedText("DeviceType_Headphones"), new LocalizedText("Picker_Available"));
        LocalizedText[] arguments = [nested, new("Common_Output")];
        var message = new LocalizedText("Picker_Help", arguments);
        foreach (string language in new[] { "en", "es", "en" })
        {
            localization.SetLanguage(language);
            string expected = string.Format(CultureInfo.CurrentCulture, localization["Picker_Help"],
                string.Format(CultureInfo.CurrentCulture, localization["Picker_Help"], localization["DeviceType_Headphones"], localization["Picker_Available"]),
                localization["Common_Output"]);
            Assert.Equal(expected, localization.Format(message));
            Assert.Equal(expected, localization.FormatKey("Picker_Help", arguments));
            Assert.Same(nested, arguments[0]);
            Assert.IsType<LocalizedText>(nested.Arguments[0]);
        }
    }
}

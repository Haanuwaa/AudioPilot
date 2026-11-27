using AudioPilot.Tests.Helpers;
namespace AudioPilot.Tests.Views;

public sealed class SettingsPanelCharacterizationTests
{
    private static readonly string[] CriticalSettingsContracts =
    [
        "IsChecked=\"{Binding SettingsOutputRoleMultimediaDraft",
        "IsChecked=\"{Binding SettingsInputRoleCommunicationsDraft",
        "Hotkey=\"{Binding OutputReverseHotkey}",
        "SelectedValue=\"{Binding SettingsListenMonitorOutputDeviceIdDraft",
        "Command=\"{Binding ResetPerAppAudioRoutingCommand}",
        "Hotkey=\"{Binding SettingsToggleAppVisibilityHotkeyDraftCapture}",
        "Text=\"{Binding SettingsMasterVolumeStepPercentDraft",
        "Hotkey=\"{Binding SettingsMuteMicHotkeyDraftCapture}",
        "Hotkey=\"{Binding SettingsMuteSoundHotkeyDraftCapture}",
        "Hotkey=\"{Binding SettingsDeafenHotkeyDraftCapture}",
        "IsChecked=\"{Binding SettingsOverlayEnabledDraft",
        "Text=\"{Binding SettingsOverlayDurationSecondsDraft",
        "IsChecked=\"{Binding SettingsPreserveAudioLevelsDraft",
        "IsChecked=\"{Binding SettingsKeepMuteStateWhenSwitchingDraft",
        "SelectedItem=\"{Binding SettingsDeviceReferenceFileModeDraft",
        "IsChecked=\"{Binding SettingsRedactLogContentDraft",
        "IsChecked=\"{Binding SettingsAutoSaveEnabledDraft",
        "IsChecked=\"{Binding SettingsPlayAppSoundsDraft",
        "Command=\"{Binding ApplySettingsCommand}",
    ];

    [Fact]
    public void SettingsSurface_PreservesCriticalBindingsAndCommandsExactlyOnce()
    {
        string repositoryRoot = TestRepository.Root;
        string viewsDirectory = Path.Combine(repositoryRoot, "AudioPilot", "Views");
        string[] settingsFiles =
        [
            Path.Combine(repositoryRoot, "AudioPilot", "MainWindow.xaml"),
            .. Directory.Exists(viewsDirectory)
                ? Directory.GetFiles(viewsDirectory, "Settings*Panel.xaml", SearchOption.TopDirectoryOnly)
                : [],
        ];
        string settingsSurface = string.Join(Environment.NewLine, settingsFiles.Select(File.ReadAllText));

        foreach (string contract in CriticalSettingsContracts)
        {
            Assert.Equal(1, CountOccurrences(settingsSurface, contract));
        }
    }

    private static int CountOccurrences(string value, string token)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }
}

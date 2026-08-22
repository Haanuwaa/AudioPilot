namespace AudioPilot.Tests.Views;

public sealed class SettingsPanelCharacterizationTests
{
    private static readonly string[] CriticalSettingsContracts =
    [
        "IsChecked=\"{Binding SettingsOutputRoleMultimediaDraft",
        "IsChecked=\"{Binding SettingsInputRoleCommunicationsDraft",
        "Text=\"{Binding OutputReverseHotkey.DisplayText",
        "SelectedValue=\"{Binding SettingsListenMonitorOutputDeviceIdDraft",
        "Command=\"{Binding ResetPerAppAudioRoutingCommand}",
        "Text=\"{Binding SettingsToggleAppVisibilityHotkeyDraftCapture.DisplayText",
        "Text=\"{Binding SettingsMasterVolumeStepPercentDraft",
        "Content=\"Toggle Mic:\"",
        "Content=\"Toggle Sound:\"",
        "Content=\"Toggle Deafen:\"",
        "Content=\"Enable overlays\" IsChecked=\"{Binding SettingsOverlayEnabledDraft",
        "Text=\"{Binding SettingsOverlayDurationSecondsDraft",
        "IsChecked=\"{Binding SettingsPreserveAudioLevelsDraft",
        "SelectedItem=\"{Binding SettingsDeviceReferenceFileModeDraft",
        "IsChecked=\"{Binding SettingsRedactLogContentDraft",
        "IsChecked=\"{Binding SettingsAutoSaveEnabledDraft",
        "IsChecked=\"{Binding SettingsPlayDialogSoundsDraft",
        "Command=\"{Binding ApplySettingsCommand}",
    ];

    [Fact]
    public void SettingsSurface_PreservesCriticalBindingsAndCommandsExactlyOnce()
    {
        string repositoryRoot = ResolveRepositoryRoot();
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

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AudioPilot.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the AudioPilot repository root.");
    }
}

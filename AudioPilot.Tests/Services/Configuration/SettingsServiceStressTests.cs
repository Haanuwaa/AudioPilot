using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Configuration;

[Trait(TestCategories.Name, TestCategories.Stress)]
[Collection("LoggerFileIsolation")]
public sealed class SettingsServiceStressTests
{
    [StressFact]
    public void SaveAndLoadChurn_PreservesLatestSettingsAcrossRepeatedWrites_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(SaveAndLoadChurn_PreservesLatestSettingsAcrossRepeatedWrites_WhenStressEnabled)))
        {
            return;
        }

        using var workspace = new TestSettingsWorkspace(nameof(SettingsServiceStressTests));
        var service = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);

        for (int iteration = 0; iteration < 200; iteration++)
        {
            Settings expected = CreateSettings(iteration);

            service.SaveSettings(expected);
            Settings loaded = service.LoadSettings();

            Assert.Equal(expected.DeviceSwitching.Output.SwitchHotkey, loaded.DeviceSwitching.Output.SwitchHotkey);
            Assert.Equal(expected.Hotkeys.App.ToggleAppVisibility, loaded.Hotkeys.App.ToggleAppVisibility);
            Assert.Equal(expected.Theme, loaded.Theme);
            Assert.Equal(expected.RunAtStartup, loaded.RunAtStartup);
            Assert.Equal(expected.Hotkeys.Global.AdditionalStandaloneKeys, loaded.Hotkeys.Global.AdditionalStandaloneKeys);
            Assert.Equal(expected.DeviceSwitching.Output.SwitchRoles, loaded.DeviceSwitching.Output.SwitchRoles);

            AudioRoutine loadedRoutine = Assert.Single(loaded.Routines.Items);
            AudioRoutine expectedRoutine = Assert.Single(expected.Routines.Items);
            Assert.Equal(expectedRoutine.Name, loadedRoutine.Name);
            Assert.Equal(expectedRoutine.Hotkey, loadedRoutine.Hotkey);
            Assert.Equal(expectedRoutine.TriggerAppPath, loadedRoutine.TriggerAppPath);
        }

        string primarySettingsPath = Path.Combine(workspace.PrimaryDir, AppConstants.Files.SettingsFileName);
        string backupPath = Path.Combine(workspace.PrimaryDir, AppConstants.Files.BackupFolderName, $"{AppConstants.Files.SettingsFileName}.bak");

        Assert.True(File.Exists(primarySettingsPath));
        Assert.True(File.Exists(backupPath));
    }

    private static Settings CreateSettings(int iteration)
    {
        return new Settings
        {
            Theme = iteration % 2 == 0 ? AppTheme.Dark : AppTheme.Light,
            RunAtStartup = iteration % 3 == 0,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = $"Ctrl+Alt+F{(iteration % 12) + 1}",
                    SwitchRoles = iteration % 2 == 0
                        ? ["Multimedia", "Communications", "Console"]
                        : ["Multimedia", "Console"]
                }
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = $"Ctrl+Shift+F{((iteration + 1) % 12) + 1}"
                },
                Global = new HotkeysGlobalSettings
                {
                    AdditionalStandaloneKeys = iteration % 2 == 0 ? ["Home", "PrintScreen"] : ["End"]
                }
            },
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = $"routine-{iteration}",
                        Name = $"Routine {iteration}",
                        Enabled = true,
                        Hotkey = $"Ctrl+Alt+{((iteration % 9) + 1)}",
                        TriggerAppPath = $"C:/Apps/App{iteration}.exe",
                    }
                ]
            }
        };
    }
}

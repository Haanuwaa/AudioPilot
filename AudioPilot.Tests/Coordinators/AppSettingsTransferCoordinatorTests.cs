using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSettingsTransferCoordinatorTests : IDisposable
{
    private readonly TestSettingsWorkspace _workspace;

    public AppSettingsTransferCoordinatorTests()
    {
        _workspace = new TestSettingsWorkspace(nameof(AppSettingsTransferCoordinatorTests));
    }

    [Fact]
    public void ResolveInitialDirectory_WhenSettingsDirectoryExists_ReturnsDirectory()
    {
        string settingsDirectory = Path.Combine(_workspace.Root, "settings-home");
        Directory.CreateDirectory(settingsDirectory);

        string resolved = AppSettingsTransferCoordinator.ResolveInitialDirectory(Path.Combine(settingsDirectory, "settings.json"));

        Assert.Equal(settingsDirectory, resolved);
    }

    [Fact]
    public void ResolveInitialDirectory_WhenSettingsDirectoryMissing_ReturnsMyDocuments()
    {
        string missingSettingsPath = Path.Combine(_workspace.Root, "missing-home", "settings.json");

        string resolved = AppSettingsTransferCoordinator.ResolveInitialDirectory(missingSettingsPath);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), resolved);
    }

    [Fact]
    public void BuildDefaultExportFileName_UsesStableTimestampFormat()
    {
        string fileName = AppSettingsTransferCoordinator.BuildDefaultExportFileName(new DateTime(2026, 3, 12, 14, 5, 9));

        Assert.Equal("AudioPilot-settings-20260312-140509.zip", fileName);
    }

    [Fact]
    public async Task ExportAsync_WritesProvidedCurrentSettings()
    {
        var settingsService = CreateSettingsService();
        string exportPath = Path.Combine(_workspace.Root, "export.json");
        var settings = BuildSettings("export-output");

        await AppSettingsTransferCoordinator.ExportAsync(settingsService, settings, exportPath);

        Assert.True(File.Exists(exportPath));
        string text = File.ReadAllText(exportPath);
        Assert.Contains("export-output", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_LoadsPersistedSettings_WhenCurrentSettingsIsNull()
    {
        var settingsService = CreateSettingsService();
        string exportPath = Path.Combine(_workspace.Root, "persisted-export.json");
        settingsService.SaveSettings(BuildSettings("persisted-output"));

        await AppSettingsTransferCoordinator.ExportAsync(settingsService, currentSettings: null, exportPath);

        Assert.True(File.Exists(exportPath));
        string text = File.ReadAllText(exportPath);
        Assert.Contains("persisted-output", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_SavesImportedSettingsAndReturnsThem()
    {
        var settingsService = CreateSettingsService();
        settingsService.SaveSettings(BuildSettings("current-output"));

        string importPath = Path.Combine(_workspace.Root, "import.json");
        SettingsTransferService.ExportSettings(BuildSettings("imported-output"), importPath);

        using var semaphore = new SemaphoreSlim(1, 1);
        Settings imported = await AppSettingsTransferCoordinator.ImportAsync(settingsService, currentSettings: null, semaphore, importPath);
        Settings persisted = settingsService.LoadSettings();

        Assert.Equal("imported-output", imported.DeviceSwitching.Output.CycleDevices[0].Id);
        Assert.Equal("imported-output", persisted.DeviceSwitching.Output.CycleDevices[0].Id);
    }

    [Fact]
    public async Task ImportAsync_ReleasesSemaphoreWhenImportFails()
    {
        var settingsService = CreateSettingsService();
        string importPath = Path.Combine(_workspace.Root, "invalid.txt");
        File.WriteAllText(importPath, "not-a-supported-import");

        using var semaphore = new SemaphoreSlim(1, 1);

        await Assert.ThrowsAsync<NotSupportedException>(() => AppSettingsTransferCoordinator.ImportAsync(settingsService, new Settings(), semaphore, importPath));

        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task ImportAsync_WaitsForSemaphore_ThenSavesImportedSettings()
    {
        var settingsService = CreateSettingsService();
        settingsService.SaveSettings(BuildSettings("current-output"));

        string importPath = Path.Combine(_workspace.Root, "delayed-import.json");
        SettingsTransferService.ExportSettings(BuildSettings("serialized-imported-output"), importPath);

        using var semaphore = new SemaphoreSlim(1, 1);
        await semaphore.WaitAsync(TestContext.Current.CancellationToken);

        Task<Settings> importTask = AppSettingsTransferCoordinator.ImportAsync(settingsService, currentSettings: null, semaphore, importPath);

        await TestExecutionGuards.AssertDoesNotCompleteWithinAsync(
            importTask,
            TimeSpan.FromMilliseconds(150),
            "Import unexpectedly completed before the semaphore was released.");

        semaphore.Release();

        Settings imported = await importTask;
        Settings persisted = settingsService.LoadSettings();

        Assert.Equal("serialized-imported-output", imported.DeviceSwitching.Output.CycleDevices[0].Id);
        Assert.Equal("serialized-imported-output", persisted.DeviceSwitching.Output.CycleDevices[0].Id);
        Assert.Equal(1, semaphore.CurrentCount);
    }

    private SettingsService CreateSettingsService()
    {
        return new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
    }

    private static Settings BuildSettings(string outputDeviceId)
    {
        return new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = outputDeviceId, Name = "Desk Speakers" },
                        new CycleDevice { Id = outputDeviceId + "-2", Name = "Headset" },
                    ],
                    SwitchHotkey = "Ctrl+Alt+O"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "mic-1", Name = "Desk Mic" },
                        new CycleDevice { Id = "mic-2", Name = "Headset Mic" },
                    ],
                    SwitchHotkey = "Ctrl+Alt+I"
                }
            }
        };
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}

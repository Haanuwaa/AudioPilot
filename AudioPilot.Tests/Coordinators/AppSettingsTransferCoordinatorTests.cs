using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSettingsTransferCoordinatorTests : IDisposable
{
    [Fact]
    public void ImportPreviewNamesCanonicalSectionsWithoutReportingUnchangedRoutineItems()
    {
        var current = new Settings();
        current.Routines.Items.Add(new() { Name = "Keep this routine", MasterVolumePercent = 40 });
        Settings imported = current.Clone();
        imported.App.AutoSaveEnabled = true;
        imported.Startup.UseTaskScheduler = true;
        imported.Updates.CheckAutomatically = true;
        imported.Diagnostics.RedactLogContent = false;
        imported.Routines.DefaultTimeZoneId = current.Routines.DefaultTimeZoneId == "UTC" ? "Pacific Standard Time" : "UTC";
        var preview = AppSettingsTransferCoordinator.DescribeImport(current, imported);
        string english = preview(LocalizationService.English);
        Assert.Contains("General settings will change.", english);
        Assert.Contains("Startup options will change.", english);
        Assert.Contains("Update checks will change.", english);
        Assert.Contains("Logging and diagnostics settings will change.", english);
        Assert.Contains("Default routine time zone:", english);
        Assert.DoesNotContain("Routines:", english);
        Assert.DoesNotContain("Keep this routine", english);
        string spanish = preview(new LocalizationService("es"));
        Assert.Contains("Cambiarán las opciones de inicio.", spanish);
        Assert.Contains("Zona horaria predeterminada de las rutinas:", spanish);
        Assert.Contains(imported.Routines.DefaultTimeZoneId, spanish);
        Assert.DoesNotContain("Keep this routine", spanish);
    }

    [Fact]
    public void IdenticalImport_DoesNotListUnchangedSettings_AndRealChangesAreSummarized()
    {
        var current = new Settings();
        Assert.Equal("No saved settings will change.", AppSettingsTransferCoordinator.DescribeImport(current, current.Clone())(LocalizationService.English));
        var changed = current.Clone();
        changed.Startup.Enabled = !current.Startup.Enabled;
        string description = AppSettingsTransferCoordinator.DescribeImport(current, changed)(LocalizationService.English);
        Assert.Contains("Run at startup", description);
        Assert.DoesNotContain("Input switch order", description);
        Assert.DoesNotContain("Output switch order", description);
        Assert.DoesNotContain("Push-to-talk", description);

        var trayChanged = current.Clone();
        trayChanged.Startup.StartInTray = StartupTrayBehavior.Always;
        string trayDescription = AppSettingsTransferCoordinator.DescribeImport(current, trayChanged)(LocalizationService.English);
        Assert.Contains("Startup options will change.", trayDescription);

        var closeChanged = current.Clone();
        closeChanged.App.CloseButtonBehavior = CloseButtonBehavior.HideToTray;
        Assert.Contains("General settings will change.", AppSettingsTransferCoordinator.DescribeImport(current, closeChanged)(LocalizationService.English));

        var leftClickChanged = current.Clone();
        leftClickChanged.App.TrayLeftClickAction = TrayLeftClickAction.ToggleWindow;
        Assert.Contains("General settings will change.", AppSettingsTransferCoordinator.DescribeImport(current, leftClickChanged)(LocalizationService.English));

        var minimizeChanged = current.Clone();
        minimizeChanged.App.MinimizeButtonBehavior = MinimizeButtonBehavior.MinimizeToTaskbar;
        Assert.Contains("General settings will change.", AppSettingsTransferCoordinator.DescribeImport(current, minimizeChanged)(LocalizationService.English));
    }

    private readonly TestSettingsWorkspace _workspace;

    [Fact]
    public void ImportPreviewDistinguishesEmptyOrderFromADeviceActuallyNamedNone()
    {
        var current = new Settings();
        var imported = current.Clone();
        imported.App.Language = "es";
        imported.DeviceSwitching.Output.CycleDevices.Add(new CycleDevice { Id = "device", Name = "None" });
        var preview = AppSettingsTransferCoordinator.DescribeImport(current, imported);
        Assert.Contains("Output switch order: None → None", preview(LocalizationService.English));
        string spanish = preview(new LocalizationService("es"));
        Assert.Contains("Orden de salida: Ninguno → None", spanish);
        Assert.Contains("Idioma:", spanish);
    }

    public AppSettingsTransferCoordinatorTests()
    {
        _workspace = new TestSettingsWorkspace(nameof(AppSettingsTransferCoordinatorTests));
    }

    [Fact]
    public async Task PreviewDoesNotSave_AndImportAppliesExactlyTheValidatedSnapshot()
    {
        var service = CreateSettingsService();
        Settings current = BuildSettings("original");
        service.SaveSettings(current);
        string path = Path.Combine(_workspace.Root, "preview.json");
        Settings wanted = BuildSettings("previewed");
        wanted.Hotkeys.Mute.PushToTalkEnabled = true;
        wanted.Hotkeys.Mute.PushToTalk = "Ctrl+Shift+F9";
        SettingsTransferService.ExportSettings(wanted, path);
        Settings prepared = AppSettingsTransferCoordinator.PrepareImport(SettingsTransferService.ReadImportText(path), current, replaceImport: true);
        Assert.Equal("original", service.LoadSettings().DeviceSwitching.Output.CycleDevices[0].Id);
        string summary = AppSettingsTransferCoordinator.DescribeImport(current, prepared)(LocalizationService.English);
        Assert.Contains("Push-to-talk mode: Off → On", summary);
        Assert.Contains("mutes the default microphones", summary);
        File.WriteAllText(path, "invalid replacement after preview");
        using var semaphore = new SemaphoreSlim(1, 1);
        Settings? published = null;
        Assert.Null(await AppSettingsTransferCoordinator.CommitPreparedImportAsync(current, prepared, () => service.LoadSettingsReadOnly(), semaphore,
            service.SaveSettings, settings => published = settings, TestContext.Current.CancellationToken));
        Assert.Same(prepared, published);
        Assert.Equal("previewed", service.LoadSettings().DeviceSwitching.Output.CycleDevices[0].Id);
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
    public async Task CommitPreparedImport_RequiresFreshApprovalAfterSavedSettingsChange()
    {
        var settingsService = CreateSettingsService();
        Settings baseline = BuildSettings("current-output");
        Settings prepared = BuildSettings("imported-output");
        Settings latest = baseline.Clone();
        latest.Startup.Enabled = true;
        settingsService.SaveSettings(latest);
        using var semaphore = new SemaphoreSlim(1, 1);
        var changed = await AppSettingsTransferCoordinator.CommitPreparedImportAsync(baseline, prepared, () => settingsService.LoadSettingsReadOnly(),
            semaphore, _ => Assert.Fail("Stale approval must not save."), _ => Assert.Fail("Stale approval must not publish."), TestContext.Current.CancellationToken);
        Assert.NotNull(changed);
        Assert.True(changed.Startup.Enabled);
        Assert.True(settingsService.LoadSettingsReadOnly().Startup.Enabled);
        Assert.Contains("Run at startup", AppSettingsTransferCoordinator.DescribeImport(changed, prepared)(LocalizationService.English));
        Assert.Null(await AppSettingsTransferCoordinator.CommitPreparedImportAsync(changed, prepared, () => settingsService.LoadSettingsReadOnly(),
            semaphore, settingsService.SaveSettings, _ => Assert.Equal(0, semaphore.CurrentCount), TestContext.Current.CancellationToken));
        Assert.False(settingsService.LoadSettingsReadOnly().Startup.Enabled);
    }

    [Fact]
    public async Task CommitPreparedImport_ReleasesSemaphoreWhenSaveFails()
    {
        Settings before = new();
        using var semaphore = new SemaphoreSlim(1, 1);
        await Assert.ThrowsAsync<IOException>(() => AppSettingsTransferCoordinator.CommitPreparedImportAsync(before, new Settings(), () => before,
            semaphore, _ => throw new IOException("Save failed."), _ => Assert.Fail("Failed save must not publish."), TestContext.Current.CancellationToken));

        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task CommitPreparedImport_WaitsForSemaphore_ThenSavesImportedSettings()
    {
        var settingsService = CreateSettingsService();
        Settings before = BuildSettings("current-output");
        settingsService.SaveSettings(before);
        Settings prepared = BuildSettings("serialized-imported-output");

        using var semaphore = new SemaphoreSlim(1, 1);
        await semaphore.WaitAsync(TestContext.Current.CancellationToken);

        var importTask = AppSettingsTransferCoordinator.CommitPreparedImportAsync(before, prepared, () => settingsService.LoadSettingsReadOnly(),
            semaphore, settingsService.SaveSettings, _ => Assert.Equal(0, semaphore.CurrentCount), TestContext.Current.CancellationToken);
        Assert.False(importTask.IsCompleted);
        Assert.Equal("current-output", settingsService.LoadSettingsReadOnly().DeviceSwitching.Output.CycleDevices[0].Id);
        semaphore.Release();
        Assert.Null(await importTask);
        Settings persisted = settingsService.LoadSettings();
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

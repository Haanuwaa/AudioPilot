using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRefreshCycleCoordinatorTests
{
    [Theory]
    [InlineData(true, (int)AppDialogResult.Declined, true, false, (int)RefreshWorkflowOutcome.Continue, 1, 1, 0)]
    [InlineData(false, (int)AppDialogResult.Confirmed, false, true, (int)RefreshWorkflowOutcome.AbortSettingsReloadNull, 0, 0, 0)]
    [InlineData(false, (int)AppDialogResult.Declined, true, true, (int)RefreshWorkflowOutcome.Continue, 0, 1, 1)]
    public async Task ExecuteAsync_HandlesExternalSettingsReloadDecisions(
        bool hasPendingLocalEdits,
        int promptResult,
        bool reloadReturnsSettings,
        bool expectedSettingsChanged,
        int expectedWorkflowOutcome,
        int expectedPromptCalls,
        int expectedRefreshCollectionsCalls,
        int expectedApplyCalls)
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshCycleCoordinatorTests), "refresh-cycle-external-reload.log", LogLevel.Info);
        int promptCalls = 0;
        int refreshCollectionsCalls = 0;
        int applyCalls = 0;

        AppRefreshExecutionResult result = await AppRefreshCycleCoordinator.ExecuteAsync(
            new AppRefreshExecutionInput(
                PromptOnPotentialOverwrite: true,
                RefreshMixerWhenWindowHidden: false,
                CheckSettingsFileChanges: true,
                IsWindowVisible: true,
                IsCleaningUp: false,
                OutputCycleCount: 2),
            new AppRefreshExecutionDependencies(
                HasPendingLocalEditsForRefresh: () => hasPendingLocalEdits,
                HasSettingsFileChangedAsync: static () => Task.FromResult(true),
                PromptReloadExternalSettings: () =>
                {
                    promptCalls++;
                    return Task.FromResult((AppDialogResult)promptResult);
                },
                RefreshAvailableDeviceCollectionsAsync: () =>
                {
                    refreshCollectionsCalls++;
                    return Task.CompletedTask;
                },
                LoadSettingsForRefreshAsync: () => Task.FromResult(reloadReturnsSettings ? new Settings() : null),
                ApplyExternallyReloadedSettings: _ => applyCalls++,
                HasUiSettingsDivergedFromCachedSettings: static () => false,
                PromptReloadCachedSettings: static () => Task.FromResult(AppDialogResult.Declined),
                GetCachedSettingsSnapshot: static () => null,
                GenerateDeviceReferenceFile: static () => { },
                RefreshDeviceCache: static () => { },
                UpdateMuteFlagsFromSystemAsync: static () => Task.CompletedTask,
                RefreshMixerAsync: static _ => Task.CompletedTask),
            "refresh:test-external-reload",
            loggerScope.Logger);

        Assert.Equal(expectedSettingsChanged, result.SettingsChanged);
        Assert.Equal((RefreshWorkflowOutcome)expectedWorkflowOutcome, result.WorkflowOutcome);
        Assert.Equal(expectedPromptCalls, promptCalls);
        Assert.Equal(expectedRefreshCollectionsCalls, refreshCollectionsCalls);
        Assert.Equal(expectedApplyCalls, applyCalls);
    }

    [Fact]
    public async Task ExecuteAsync_AppliesCachedSettings_WhenUiStateDriftedAndUserConfirmsReload()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshCycleCoordinatorTests), "refresh-cycle-cached-drift.log", LogLevel.Info);
        Settings cached = new() { RunAtStartup = true };
        Settings? applied = null;
        int promptReloadCachedCalls = 0;
        int getCachedSnapshotCalls = 0;

        AppRefreshExecutionResult result = await AppRefreshCycleCoordinator.ExecuteAsync(
            new AppRefreshExecutionInput(
                PromptOnPotentialOverwrite: true,
                RefreshMixerWhenWindowHidden: false,
                CheckSettingsFileChanges: false,
                IsWindowVisible: true,
                IsCleaningUp: false,
                OutputCycleCount: 1),
            new AppRefreshExecutionDependencies(
                HasPendingLocalEditsForRefresh: static () => false,
                HasSettingsFileChangedAsync: static () => Task.FromResult(false),
                PromptReloadExternalSettings: static () => Task.FromResult(AppDialogResult.Declined),
                RefreshAvailableDeviceCollectionsAsync: static () => Task.CompletedTask,
                LoadSettingsForRefreshAsync: static () => Task.FromResult<Settings?>(new Settings()),
                ApplyExternallyReloadedSettings: settings => applied = settings,
                HasUiSettingsDivergedFromCachedSettings: static () => true,
                PromptReloadCachedSettings: () =>
                {
                    promptReloadCachedCalls++;
                    return Task.FromResult(AppDialogResult.Confirmed);
                },
                GetCachedSettingsSnapshot: () =>
                {
                    getCachedSnapshotCalls++;
                    return cached;
                },
                GenerateDeviceReferenceFile: static () => { },
                RefreshDeviceCache: static () => { },
                UpdateMuteFlagsFromSystemAsync: static () => Task.CompletedTask,
                RefreshMixerAsync: static _ => Task.CompletedTask),
            "refresh:test-cached-drift",
            loggerScope.Logger);

        Assert.False(result.SettingsChanged);
        Assert.Equal(RefreshWorkflowOutcome.Continue, result.WorkflowOutcome);
        Assert.Equal(1, promptReloadCachedCalls);
        Assert.Equal(1, getCachedSnapshotCalls);
        Assert.Same(cached, applied);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotPromptForCachedDrift_WhenExternalSettingsWereAlreadyApplied()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshCycleCoordinatorTests), "refresh-cycle-skip-cached-after-external.log", LogLevel.Info);
        Settings reloaded = new() { Overlay = new OverlaySettings { Enabled = false } };
        Settings? applied = null;
        int promptExternalCalls = 0;
        int promptCachedCalls = 0;
        int getCachedSnapshotCalls = 0;

        AppRefreshExecutionResult result = await AppRefreshCycleCoordinator.ExecuteAsync(
            new AppRefreshExecutionInput(
                PromptOnPotentialOverwrite: true,
                RefreshMixerWhenWindowHidden: false,
                CheckSettingsFileChanges: true,
                IsWindowVisible: true,
                IsCleaningUp: false,
                OutputCycleCount: 1),
            new AppRefreshExecutionDependencies(
                HasPendingLocalEditsForRefresh: static () => false,
                HasSettingsFileChangedAsync: static () => Task.FromResult(true),
                PromptReloadExternalSettings: () =>
                {
                    promptExternalCalls++;
                    return Task.FromResult(AppDialogResult.Confirmed);
                },
                RefreshAvailableDeviceCollectionsAsync: static () => Task.CompletedTask,
                LoadSettingsForRefreshAsync: static () => Task.FromResult<Settings?>(new Settings { Overlay = new OverlaySettings { Enabled = false } }),
                ApplyExternallyReloadedSettings: settings => applied = settings,
                HasUiSettingsDivergedFromCachedSettings: static () => true,
                PromptReloadCachedSettings: () =>
                {
                    promptCachedCalls++;
                    return Task.FromResult(AppDialogResult.Confirmed);
                },
                GetCachedSettingsSnapshot: () =>
                {
                    getCachedSnapshotCalls++;
                    return new Settings();
                },
                GenerateDeviceReferenceFile: static () => { },
                RefreshDeviceCache: static () => { },
                UpdateMuteFlagsFromSystemAsync: static () => Task.CompletedTask,
                RefreshMixerAsync: static _ => Task.CompletedTask),
            "refresh:test-skip-cached-after-external",
            loggerScope.Logger);

        Assert.True(result.SettingsChanged);
        Assert.Equal(RefreshWorkflowOutcome.Continue, result.WorkflowOutcome);
        Assert.Equal(0, promptExternalCalls);
        Assert.Equal(0, promptCachedCalls);
        Assert.Equal(0, getCachedSnapshotCalls);
        Assert.NotNull(applied);
        Assert.False(applied!.Overlay.Enabled);
    }

    [Fact]
    public async Task ExecuteAsync_RunsRefreshWorkflow_AndPostRefreshEffects()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshCycleCoordinatorTests), "refresh-cycle-visible.log", LogLevel.Info);
        Settings applied = new();
        int refreshCollectionsCalls = 0;
        int generateCalls = 0;
        int cacheRefreshCalls = 0;
        int muteRefreshCalls = 0;
        int mixerRefreshCalls = 0;

        AppRefreshExecutionResult result = await AppRefreshCycleCoordinator.ExecuteAsync(
            new AppRefreshExecutionInput(
                PromptOnPotentialOverwrite: false,
                RefreshMixerWhenWindowHidden: false,
                CheckSettingsFileChanges: false,
                IsWindowVisible: true,
                IsCleaningUp: false,
                OutputCycleCount: 3),
            new AppRefreshExecutionDependencies(
                HasPendingLocalEditsForRefresh: static () => false,
                HasSettingsFileChangedAsync: static () => Task.FromResult(false),
                PromptReloadExternalSettings: static () => Task.FromResult(AppDialogResult.Declined),
                RefreshAvailableDeviceCollectionsAsync: () =>
                {
                    refreshCollectionsCalls++;
                    return Task.CompletedTask;
                },
                LoadSettingsForRefreshAsync: static () => Task.FromResult<Settings?>(new Settings()),
                ApplyExternallyReloadedSettings: settings => applied = settings,
                HasUiSettingsDivergedFromCachedSettings: static () => false,
                PromptReloadCachedSettings: static () => Task.FromResult(AppDialogResult.Declined),
                GetCachedSettingsSnapshot: static () => null,
                GenerateDeviceReferenceFile: () => generateCalls++,
                RefreshDeviceCache: () => cacheRefreshCalls++,
                UpdateMuteFlagsFromSystemAsync: () =>
                {
                    muteRefreshCalls++;
                    return Task.CompletedTask;
                },
                RefreshMixerAsync: _ =>
                {
                    mixerRefreshCalls++;
                    return Task.CompletedTask;
                }),
            "refresh:test-visible",
            loggerScope.Logger);

        Assert.False(result.SettingsChanged);
        Assert.Equal(RefreshWorkflowOutcome.Continue, result.WorkflowOutcome);
        Assert.Equal(1, refreshCollectionsCalls);
        Assert.Equal(1, generateCalls);
        Assert.Equal(1, cacheRefreshCalls);
        Assert.Equal(1, muteRefreshCalls);
        Assert.Equal(1, mixerRefreshCalls);
        Assert.NotNull(applied);
    }
}

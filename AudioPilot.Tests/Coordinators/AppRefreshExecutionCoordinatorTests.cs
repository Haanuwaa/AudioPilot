using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRefreshExecutionCoordinatorTests
{
    [Theory]
    [InlineData(false, (int)AppDialogResult.Declined, true, 0)]
    [InlineData(true, (int)AppDialogResult.Declined, false, 1)]
    [InlineData(true, (int)AppDialogResult.Confirmed, true, 1)]
    public async Task ResolveSettingsChangedForRefresh_ReturnsExpectedDecision(
        bool hasPendingLocalEdits,
        int promptResult,
        bool expectedResult,
        int expectedPromptCalls)
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshExecutionCoordinatorTests), "refresh-exec-decision.log", LogLevel.Info);
        int promptCalls = 0;

        bool result = await AppRefreshExecutionCoordinator.ResolveSettingsChangedForRefreshAsync(
            settingsChanged: true,
            promptOnPotentialOverwrite: true,
            hasPendingLocalEdits: hasPendingLocalEdits,
            promptOverwrite: () =>
            {
                promptCalls++;
                return Task.FromResult((AppDialogResult)promptResult);
            },
            refreshOpId: "refresh:test-decision",
            logger: loggerScope.Logger);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedPromptCalls, promptCalls);
    }

    [Theory]
    [InlineData(true, true, true, (int)AppDialogResult.Confirmed, true, 0, 0, 0)]
    [InlineData(false, true, true, (int)AppDialogResult.Declined, true, 1, 0, 0)]
    [InlineData(false, true, true, (int)AppDialogResult.Confirmed, false, 1, 1, 0)]
    [InlineData(false, true, true, (int)AppDialogResult.Confirmed, true, 1, 1, 1)]
    [InlineData(false, true, false, (int)AppDialogResult.Confirmed, true, 0, 0, 0)]
    [InlineData(false, false, true, (int)AppDialogResult.Confirmed, true, 0, 0, 0)]
    public async Task ApplyCachedSettingsIfUiDrifted_HandlesGuardAndApplyDecisions(
        bool settingsChanged,
        bool promptOnPotentialOverwrite,
        bool hasUiSettingsDivergedFromCachedSettings,
        int promptResult,
        bool cachedSnapshotAvailable,
        int expectedPromptCalls,
        int expectedGetCachedCalls,
        int expectedApplyCalls)
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshExecutionCoordinatorTests), "refresh-exec-guarded-noop.log", LogLevel.Info);
        int promptCalls = 0;
        int getCachedCalls = 0;
        int applyCalls = 0;
        Settings cached = new() { RunAtStartup = true };
        Settings? applied = null;

        await AppRefreshExecutionCoordinator.ApplyCachedSettingsIfUiDriftedAsync(
            settingsChanged,
            promptOnPotentialOverwrite,
            hasUiSettingsDivergedFromCachedSettings,
            () =>
            {
                promptCalls++;
                return Task.FromResult((AppDialogResult)promptResult);
            },
            () =>
            {
                getCachedCalls++;
                return cachedSnapshotAvailable ? cached : null;
            },
            settings =>
            {
                applyCalls++;
                applied = settings;
            },
            refreshOpId: "refresh:test-guarded-noop",
            logger: loggerScope.Logger);

        Assert.Equal(expectedPromptCalls, promptCalls);
        Assert.Equal(expectedGetCachedCalls, getCachedCalls);
        Assert.Equal(expectedApplyCalls, applyCalls);
        if (expectedApplyCalls == 1)
        {
            Assert.Same(cached, applied);
        }
        else
        {
            Assert.Null(applied);
        }
    }

    [Theory]
    [InlineData(false, false, false, 1, 0, 0, 0, null)]
    [InlineData(false, true, false, 1, 1, 1, 1, true)]
    [InlineData(true, false, true, 1, 1, 1, 1, false)]
    public async Task ApplyPostRefreshEffectsAsync_DispatchesExpectedWork(
        bool refreshMixerWhenWindowHidden,
        bool isWindowVisible,
        bool isCleaningUp,
        int expectedGenerateCalls,
        int expectedCacheRefreshCalls,
        int expectedMuteRefreshCalls,
        int expectedMixerRefreshCalls,
        bool? expectedMixerVisibleState)
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshExecutionCoordinatorTests), "refresh-exec-effects.log", LogLevel.Info);
        int generateCalls = 0;
        int cacheRefreshCalls = 0;
        int muteRefreshCalls = 0;
        int mixerRefreshCalls = 0;
        bool? mixerVisibleState = null;

        await AppRefreshExecutionCoordinator.ApplyPostRefreshEffectsAsync(
            refreshMixerWhenWindowHidden,
            isWindowVisible,
            isCleaningUp,
            () => generateCalls++,
            () => cacheRefreshCalls++,
            () =>
            {
                muteRefreshCalls++;
                return Task.CompletedTask;
            },
            visible =>
            {
                mixerRefreshCalls++;
                mixerVisibleState = visible;
                return Task.CompletedTask;
            },
            outputCycleCount: 2,
            refreshOpId: "refresh:test-effects",
            logger: loggerScope.Logger);

        Assert.Equal(expectedGenerateCalls, generateCalls);
        Assert.Equal(expectedCacheRefreshCalls, cacheRefreshCalls);
        Assert.Equal(expectedMuteRefreshCalls, muteRefreshCalls);
        Assert.Equal(expectedMixerRefreshCalls, mixerRefreshCalls);
        Assert.Equal(expectedMixerVisibleState, mixerVisibleState);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    public void ShouldRefreshDeviceCollections_ReturnsExpectedValue(
        bool settingsChanged,
        bool refreshMixerWhenWindowHidden,
        bool isWindowVisible,
        bool expected)
    {
        bool result = AppRefreshExecutionCoordinator.ShouldRefreshDeviceCollections(
            settingsChanged,
            refreshMixerWhenWindowHidden,
            isWindowVisible);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void ShouldDeferNonCriticalHotplugPostRefresh_ReturnsExpectedValue(
        bool refreshMixerWhenWindowHidden,
        bool isWindowVisible,
        bool expected)
    {
        bool result = AppRefreshExecutionCoordinator.ShouldDeferNonCriticalHotplugPostRefresh(
            refreshMixerWhenWindowHidden,
            isWindowVisible);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void ShouldRefreshForHotplug_RequiresVisibleAndNotCleaning(
        bool isWindowVisible,
        bool isCleaningUp,
        bool expected)
    {
        bool result = AppMixerRefreshGuardHelper.ShouldRefreshForHotplug(isWindowVisible, isCleaningUp);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ApplyPostRefreshEffectsAsync_LogsRefreshOpId_ForDeferredAndStateEvents()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshExecutionCoordinatorTests), "refresh-exec.log");

        await AppRefreshExecutionCoordinator.ApplyPostRefreshEffectsAsync(
            refreshMixerWhenWindowHidden: false,
            isWindowVisible: false,
            isCleaningUp: false,
            generateDeviceReferenceFile: static () => { },
            refreshDeviceCache: static () => { },
            updateMuteFlagsFromSystemAsync: static () => Task.CompletedTask,
            refreshMixerAsync: static _ => Task.CompletedTask,
            outputCycleCount: 4,
            refreshOpId: "refresh:test-log",
            logger: loggerScope.Logger);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains("opId=refresh:test-log", logText, StringComparison.Ordinal);
        Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.App.HotplugPostRefreshDeferred, logText, StringComparison.Ordinal);
        Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.App.HotplugMixerRefreshDeferred, logText, StringComparison.Ordinal);
        Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.App.RefreshState, logText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, (int)RefreshWorkflowOutcome.Continue, 1, 0, 0)]
    [InlineData(true, false, (int)RefreshWorkflowOutcome.AbortSettingsReloadNull, 0, 1, 0)]
    [InlineData(true, true, (int)RefreshWorkflowOutcome.Continue, 1, 1, 1)]
    public async Task RefreshCollectionsAndApplySettingsAsync_HandlesReloadOutcomes(
        bool settingsChanged,
        bool reloadReturnsSettings,
        int expectedOutcome,
        int expectedRefreshCalls,
        int expectedLoadCalls,
        int expectedApplyCalls)
    {
        int refreshCalls = 0;
        int loadCalls = 0;
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshExecutionCoordinatorTests), "refresh-workflow-decision.log", LogLevel.Info);

        RefreshWorkflowOutcome outcome = await AppRefreshExecutionCoordinator.RefreshCollectionsAndApplySettingsAsync(
            settingsChanged,
            refreshDeviceCollections: true,
            () =>
            {
                refreshCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                loadCalls++;
                return Task.FromResult(reloadReturnsSettings ? new Settings() : null);
            },
            _ => applyCalls++,
            refreshOpId: "refresh:decision",
            logger: loggerScope.Logger);

        Assert.Equal((RefreshWorkflowOutcome)expectedOutcome, outcome);
        Assert.Equal(expectedRefreshCalls, refreshCalls);
        Assert.Equal(expectedLoadCalls, loadCalls);
        Assert.Equal(expectedApplyCalls, applyCalls);
    }

    [Fact]
    public async Task RefreshCollectionsAndApplySettingsAsync_LogsRefreshOpId_WhenReloadFails()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshExecutionCoordinatorTests), "refresh-workflow.log");

        RefreshWorkflowOutcome outcome = await AppRefreshExecutionCoordinator.RefreshCollectionsAndApplySettingsAsync(
            settingsChanged: true,
            refreshDeviceCollections: false,
            refreshAvailableDeviceCollectionsAsync: static () => Task.CompletedTask,
            loadSettingsAsync: static () => Task.FromResult<Settings?>(null),
            applyExternallyReloadedSettings: static _ => { },
            refreshOpId: "refresh:failure-log",
            logger: loggerScope.Logger);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(RefreshWorkflowOutcome.AbortSettingsReloadNull, outcome);
        Assert.Contains("opId=refresh:failure-log", logText, StringComparison.Ordinal);
        Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.RefreshFailed, logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCollectionsAndApplySettingsAsync_SkipsCollectionRefresh_WhenDeferred()
    {
        int refreshCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(AppRefreshExecutionCoordinatorTests), "refresh-workflow-hidden-deferred.log", LogLevel.Info);

        RefreshWorkflowOutcome outcome = await AppRefreshExecutionCoordinator.RefreshCollectionsAndApplySettingsAsync(
            settingsChanged: false,
            refreshDeviceCollections: false,
            refreshAvailableDeviceCollectionsAsync: () =>
            {
                refreshCalls++;
                return Task.CompletedTask;
            },
            loadSettingsAsync: static () => Task.FromResult<Settings?>(new Settings()),
            applyExternallyReloadedSettings: static _ => { },
            refreshOpId: "refresh:hidden-deferred",
            logger: loggerScope.Logger);

        Assert.Equal(RefreshWorkflowOutcome.Continue, outcome);
        Assert.Equal(0, refreshCalls);
    }
}

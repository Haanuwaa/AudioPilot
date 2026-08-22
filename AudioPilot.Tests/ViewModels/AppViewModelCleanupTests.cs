using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("BackgroundTaskHelperDelayIsolation")]
public sealed class AppViewModelCleanupTests
{
    [Fact]
    public void CancelAndDetachDebounce_CancelsAndClearsCurrentSource()
    {
        using var current = new CancellationTokenSource();
        CancellationTokenSource? active = current;

        CancellationTokenSource? detached = AppViewModel.CancelAndDetachDebounce(ref active);

        Assert.Same(current, detached);
        Assert.Null(active);
        Assert.True(detached!.IsCancellationRequested);
    }

    [Fact]
    public void CancelAndDetachDebounce_ToleratesOwnerDisposingSourceConcurrently()
    {
        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        CancellationTokenSource? active = disposed;

        CancellationTokenSource? detached = AppViewModel.CancelAndDetachDebounce(ref active);

        Assert.Same(disposed, detached);
        Assert.Null(active);
    }

    [Fact]
    public void ReleaseOwnedDebounce_ClearsMatchingCurrentSource()
    {
        var current = new CancellationTokenSource();
        CancellationTokenSource? active = current;

        AppViewModel.ReleaseOwnedDebounce(ref active, current);

        Assert.Null(active);
    }

    [Fact]
    public void ReleaseOwnedDebounce_DoesNotClearNewerCurrentSource()
    {
        using var owned = new CancellationTokenSource();
        using var current = new CancellationTokenSource();
        CancellationTokenSource? active = current;

        AppViewModel.ReleaseOwnedDebounce(ref active, owned);

        Assert.Same(current, active);
    }

    [Fact]
    public void ResolveCleanupDisposalPlan_WhenBackgroundTasksComplete_DoesNotRequestForcedWarning()
    {
        AppViewModel.CleanupDisposalPlan plan = AppViewModel.ResolveCleanupDisposalPlan(backgroundTasksCompleted: true);

        Assert.True(plan.DisposeBackgroundWorkCts);
        Assert.True(plan.ClearBackgroundTaskRegistry);
        Assert.True(plan.DisposeSettingsWriteSemaphore);
        Assert.False(plan.LogForcedDisposalWarning);
    }

    [Fact]
    public void ResolveCleanupDisposalPlan_WhenBackgroundTasksRemain_RequestsForcedWarning()
    {
        AppViewModel.CleanupDisposalPlan plan = AppViewModel.ResolveCleanupDisposalPlan(backgroundTasksCompleted: false);

        Assert.True(plan.DisposeBackgroundWorkCts);
        Assert.True(plan.ClearBackgroundTaskRegistry);
        Assert.True(plan.DisposeSettingsWriteSemaphore);
        Assert.True(plan.LogForcedDisposalWarning);
    }

    [Fact]
    public async Task WaitForBackgroundTasksToCompleteAsync_ReturnsFalse_WhenPendingTaskOutlivesGraceWindow()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelCleanupTests), "cleanup.log");
        var viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell(loggerScope.Logger);

        var backgroundTasks = new ConcurrentDictionary<int, Task>();
        var taskSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backgroundTasks[1] = taskSource.Task;
        TestPrivateAccess.SetField(viewModel, "_backgroundTasks", backgroundTasks);

        Func<int, Task> originalDelay = BackgroundTaskHelper.DelayAsyncForTests;
        BackgroundTaskHelper.DelayAsyncForTests = static _ => Task.CompletedTask;

        try
        {
            bool completed = await InvokeWaitForBackgroundTasksToCompleteAsync(viewModel, "cleanup:test-timeout").WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.False(completed);
        }
        finally
        {
            BackgroundTaskHelper.DelayAsyncForTests = originalDelay;
            taskSource.TrySetCanceled(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RecoverAfterSystemResumeAsync_LogsCorrelatedSkip_WhenCleanupInProgress()
    {
        using var logger = Logger.CreateInMemoryForTests("resume-skip.log");
        logger.MinimumLevel = LogLevel.Trace;

        var viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell(logger);
        TestPrivateAccess.SetField(viewModel, "_isCleaningUp", true);

        await viewModel.RecoverAfterSystemResumeAsync("resume:test-skip");

        string logText = logger.DisposeAndReadLogTextForTests();

        Assert.Contains($"{AppConstants.Audio.LogEvents.ResumeRecovery.Skip} | opId=resume:test-skip reason=cleanup-in-progress", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupAsync_DetachesOwnedDraftAndCollectionHandlers()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelCleanupTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
        AppViewModel viewModel = harness.ViewModel;

        int hotkeyNotifications = 0;
        int outputCollectionNotifications = 0;
        viewModel.PropertyChanged += OnPropertyChanged;

        try
        {
            Assert.True(viewModel.Hotkey.LoadFromString("Ctrl+Alt+K"));
            viewModel.OutputCycleDevices.Add(new CycleDevice { Id = "before-cleanup", Name = "Before Cleanup" });

            Assert.True(hotkeyNotifications > 0);
            Assert.True(outputCollectionNotifications > 0);

            hotkeyNotifications = 0;
            outputCollectionNotifications = 0;

            await viewModel.CleanupAsync();

            Assert.True(viewModel.Hotkey.LoadFromString("Ctrl+Alt+L"));
            viewModel.OutputCycleDevices.Add(new CycleDevice { Id = "after-cleanup", Name = "After Cleanup" });

            Assert.Equal(0, hotkeyNotifications);
            Assert.Equal(0, outputCollectionNotifications);
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(AppViewModel.Hotkey), StringComparison.Ordinal))
            {
                hotkeyNotifications++;
            }

            if (string.Equals(e.PropertyName, nameof(AppViewModel.SelectedAvailableOutputIndex), StringComparison.Ordinal))
            {
                outputCollectionNotifications++;
            }
        }
    }

    [Fact]
    public async Task CleanupAsync_IsIdempotent_WhenInvokedTwice()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelCleanupTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);

        await harness.ViewModel.CleanupAsync();
        await harness.ViewModel.CleanupAsync();

        Assert.True(harness.ViewModel.IsCleaningUpForTests());
    }

    [Fact]
    public async Task CleanupAsync_DeactivatesActiveStatefulSessions()
    {
        using var logger = Logger.CreateInMemoryForTests("cleanup-stateful.log");
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelCleanupTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, logger: logger);

        Dictionary<string, AppViewModel.RoutineStatefulSession> activeSessions = harness.ViewModel.GetActiveRoutineStatefulSessionsForTests();
        activeSessions["application-launch:routine-focus:321"] = new AppViewModel.RoutineStatefulSession(
            "application-launch:routine-focus:321",
            "routine-focus",
            "Spotify Focus",
            RoutineTriggerKind.Application,
            activationSequence: 7,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: null,
            rootProcessId: 321);

        await harness.ViewModel.CleanupAsync();

        Assert.Empty(activeSessions);
        string logText = logger.DisposeAndReadLogTextForTests();
        Assert.Contains("routine-stateful-session-deactivated", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupAsync_ReturnsControlPromptly_WhenProcessMonitorDisposeBlocks()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelCleanupTests));
        var blockingMonitor = new BlockingProcessLifecycleMonitor();
        using var invocationReturned = new ManualResetEventSlim(initialState: false);
        using var cleanupFinished = new ManualResetEventSlim(initialState: false);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                    workspace,
                    Dispatcher.CurrentDispatcher,
                    processLifecycleMonitor: blockingMonitor,
                    steamBigPictureSignalMonitor: new FakeSteamBigPictureSignalMonitor());

                blockingMonitor.Start();
                Task cleanupTask = harness.ViewModel.CleanupAsync();
                invocationReturned.Set();
                TestPrivateAccess.RunTaskOnDispatcher(cleanupTask);
            }
            catch (Exception ex)
            {
                failure = ex;
                invocationReturned.Set();
            }
            finally
            {
                cleanupFinished.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(invocationReturned.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.False(cleanupFinished.IsSet);

        blockingMonitor.WaitForDisposeEntry(TimeSpan.FromSeconds(2));
        blockingMonitor.ReleaseDispose();

        Assert.True(cleanupFinished.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        thread.Join(TimeSpan.FromSeconds(5));
        Assert.Null(failure);
    }

    [Fact]
    public async Task CleanupAsync_WhenProcessMonitorDisposeThrows_StillDisposesSteamMonitor()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelCleanupTests));
        var steamMonitor = new FakeSteamBigPictureSignalMonitor();
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
            workspace,
            Dispatcher.CurrentDispatcher,
            processLifecycleMonitor: new ThrowingProcessLifecycleMonitor(),
            steamBigPictureSignalMonitor: steamMonitor);

        await harness.ViewModel.CleanupAsync();

        Assert.Equal(1, steamMonitor.DisposeCallCount);
        Assert.True(harness.ViewModel.IsCleaningUpForTests());
    }

    [Fact]
    public void RoutineLastRunRefreshTimerTick_DoesNothing_WhenCleanupIsInProgress()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelCleanupTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
        AppViewModel viewModel = harness.ViewModel;
        AudioRoutine routine = new()
        {
            Name = "Shutdown Guard"
        };
        int lastRunStatusNotifications = 0;

        routine.PropertyChanged += OnRoutinePropertyChanged;
        viewModel.Routines.Add(routine);
        viewModel.SetIsCleaningUpForTests(true);

        try
        {
            viewModel.InvokeRoutineLastRunRefreshTimerTickForTests();

            Assert.Equal(0, lastRunStatusNotifications);
        }
        finally
        {
            routine.PropertyChanged -= OnRoutinePropertyChanged;
        }

        void OnRoutinePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(AudioRoutine.LastRunStatusText), StringComparison.Ordinal))
            {
                lastRunStatusNotifications++;
            }
        }
    }

    private static async Task<bool> InvokeWaitForBackgroundTasksToCompleteAsync(AppViewModel viewModel, string cleanupOpId)
    {
        Task<bool> task = TestPrivateAccess.InvokeNonPublicTask<bool>(viewModel, "WaitForBackgroundTasksToCompleteAsync", cleanupOpId);
        return await task;
    }
}

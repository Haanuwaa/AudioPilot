using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Tests.ViewModels;

[Collection("DeviceCacheHelperIsolation")]
public sealed class AppViewModelOrchestrationTests
{

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    public void ResolveNextSettingsTabIndex_CyclesAcrossFourTabs(int current, int expected)
    {
        int next = AppViewModel.ResolveNextSettingsTabIndex(current);

        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    public void ResolveSettingsTabIndex_AcceptsEveryMainTab(string parameter, int expected)
    {
        int? tabIndex = AppViewModel.ResolveSettingsTabIndex(parameter);

        Assert.Equal(expected, tabIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("4")]
    [InlineData("settings")]
    public void ResolveSettingsTabIndex_RejectsInvalidParameters(string? parameter)
    {
        Assert.Null(AppViewModel.ResolveSettingsTabIndex(parameter));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void ShouldApplySettingsTabSave_ReturnsExpectedValue(int tabIndex, bool expected)
    {
        bool result = AppViewModel.ShouldApplySettingsTabSave(tabIndex);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void ShouldSaveRoutinesTab_ReturnsExpectedValue(int tabIndex, bool expected)
    {
        bool result = AppViewModel.ShouldSaveRoutinesTab(tabIndex);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResumeHotkeyRegistrationResult_FailedCount_AndAllSucceeded_AggregateCorrectly()
    {
        var allGood = new AppViewModel.ResumeHotkeyRegistrationResult(
            ToggleAppVisibilityRegistered: true,
            MediaHotkeysRegistered: true,
            MuteHotkeysRegistered: true,
            ListenToInputRegistered: true,
            VolumeStepHotkeysRegistered: true,
            OutputSwitchRegistered: true,
            InputSwitchRegistered: true,
            OutputReverseSwitchRegistered: true,
            InputReverseSwitchRegistered: true);

        var partial = new AppViewModel.ResumeHotkeyRegistrationResult(
            ToggleAppVisibilityRegistered: false,
            MediaHotkeysRegistered: false,
            MuteHotkeysRegistered: true,
            ListenToInputRegistered: false,
            VolumeStepHotkeysRegistered: true,
            OutputSwitchRegistered: true,
            InputSwitchRegistered: false,
            OutputReverseSwitchRegistered: true,
            InputReverseSwitchRegistered: true);

        Assert.True(allGood.AllSucceeded);
        Assert.Equal(0, allGood.FailedCount);

        Assert.False(partial.AllSucceeded);
        Assert.Equal(4, partial.FailedCount);
    }

    [Fact]
    public async Task RefreshDevicesForHotplugAsync_WhenRefreshAlreadyInProgress_LogsSkipAndQueuesSingleRerun()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelOrchestrationTests), "refresh-coalesce.log", LogLevel.Debug);
        AppViewModel viewModel = CreateRefreshViewModelShell(loggerScope.Logger);

        Assert.True(viewModel.TryBeginRefreshCycle());

        await viewModel.RefreshDevicesForHotplugAsync();

        Assert.True(viewModel.EndRefreshCycleAndTryRestart());
        Assert.True(viewModel.IsRefreshing);

        viewModel.EndRefreshCycle();
        Assert.False(viewModel.IsRefreshing);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.App.RefreshSkip, logText, StringComparison.Ordinal);
        Assert.Contains("reason=in-progress", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshDevicesForHotplugAsync_CollapsesMultipleOverlappingRequestsIntoSingleRerun()
    {
        AppViewModel viewModel = CreateRefreshViewModelShell();

        Assert.False(viewModel.IsRefreshing);
        Assert.True(viewModel.TryBeginRefreshCycle());
        Assert.True(viewModel.IsRefreshing);

        await viewModel.RefreshDevicesForHotplugAsync();
        await viewModel.RefreshDevicesForHotplugAsync();

        Assert.True(viewModel.EndRefreshCycleAndTryRestart());
        Assert.True(viewModel.IsRefreshing);

        Assert.False(viewModel.EndRefreshCycleAndTryRestart());
        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public void RefreshDevicesForHotplugAsync_WhenCalledFromWorkerThread_RaisesRefreshStateOnDispatcher()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;
            AppShellService shell = TestPrivateAccess.GetField<AppShellService>(viewModel, "_shell");
            TestAppMainWindowManager shellManager = TestPrivateAccess.GetField<TestAppMainWindowManager>(shell, "_windowManager");
            shellManager.VisibilityWindow = TestWindowFactory.CreateOffscreenWindow();

            int dispatcherThreadId = Environment.CurrentManagedThreadId;
            int isRefreshingNotificationThreadId = 0;

            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AppViewModel.IsRefreshing) && isRefreshingNotificationThreadId == 0)
                {
                    isRefreshingNotificationThreadId = Environment.CurrentManagedThreadId;
                }
            };

            Task refreshTask = Task.Run(() => viewModel.RefreshDevicesForHotplugAsync());

            TestPrivateAccess.RunTaskOnDispatcher(refreshTask);

            Assert.Equal(dispatcherThreadId, isRefreshingNotificationThreadId);
            Assert.False(viewModel.IsRefreshing);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshAvailableDevices_PreservesEditsDuringEnumerationAndHonorsCleanup(bool cleanup)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;
            var outputDevice = new CycleDevice { Id = "edited-output", Name = "Edited output" };
            var inputDevice = new CycleDevice { Id = "edited-input", Name = "Edited input" };
            using var releaseEnumeration = new ManualResetEventSlim();
            var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task worker = ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                workerStarted.SetResult();
                Assert.True(releaseEnumeration.Wait(TimeSpan.FromSeconds(5)));
            });

            Task? refresh = null;
            int collectionChangeCount = 0;
            bool allChangesOnDispatcher = true;
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            viewModel.AvailableOutputDeviceNames.CollectionChanged += (_, _) =>
            {
                collectionChangeCount++;
                allChangesOnDispatcher &= dispatcher.CheckAccess();
            };

            try
            {
                TestPrivateAccess.RunTaskOnDispatcher(workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                refresh = TestPrivateAccess.InvokeNonPublicTask(viewModel, "RefreshAvailableDeviceCollectionsAsync");
                Assert.False(refresh.IsCompleted);

                viewModel.OutputCycleDevices.Add(outputDevice);
                viewModel.InputCycleDevices.Add(inputDevice);
                viewModel.SelectedOutputCycleDevices.Add(outputDevice);
                viewModel.SelectedInputCycleDevices.Add(inputDevice);
                viewModel.SetIsCleaningUpForTests(cleanup);
            }
            finally
            {
                releaseEnumeration.Set();
                TestPrivateAccess.RunTaskOnDispatcher(worker.WaitAsync(TimeSpan.FromSeconds(5)));
                if (refresh != null)
                {
                    TestPrivateAccess.RunTaskOnDispatcher(refresh.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                viewModel.SetIsCleaningUpForTests(false);
            }

            Assert.Contains(outputDevice, viewModel.OutputCycleDevices);
            Assert.Contains(inputDevice, viewModel.InputCycleDevices);
            Assert.Contains(outputDevice, viewModel.SelectedOutputCycleDevices);
            Assert.Contains(inputDevice, viewModel.SelectedInputCycleDevices);
            Assert.Equal(cleanup, collectionChangeCount == 0);
            Assert.True(allChangesOnDispatcher);
        });
    }

    [Fact]
    public void HandleWindowVisibilityChanged_WhenCleaningUp_SkipsQueuedRefresh()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            AppViewModel viewModel = CreateVisibilityRefreshViewModelShell();

            viewModel.SetIsCleaningUpForTests(true);

            viewModel.HandleWindowVisibilityChanged(true);

            Assert.Equal(0, viewModel.GetBackgroundTaskCountForTests());
        });
    }

    [Fact]
    public void HandleWindowVisibilityChanged_WhenVisibleOnSettingsTab_DoesNotAcquireSessionMonitoring()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.ViewModel.MarkStartupVisibilityResolved();
            harness.ViewModel.SelectedSettingsTabIndex = 3;

            harness.ViewModel.HandleWindowVisibilityChanged(true);

            Assert.Equal(0, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Output));
            Assert.Equal(0, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Input));
        });
    }

    [Fact]
    public void ShowWindow_WhenStartupVisibilityIsPending_DoesNotAcquireHiddenSessionMonitoring()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.ViewModel.SelectedSettingsTabIndex = 0;

            harness.ViewModel.ShowWindow();

            Assert.True(harness.ViewModel.HasInteractiveShowRequest);
            Assert.Equal(0, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Output));
            Assert.Equal(0, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Input));
        });
    }

    [Fact]
    public void HandleWindowVisibilityChanged_WhenStateDoesNotChange_SkipsDuplicateHiddenWork()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var loggerScope = new TestLoggerScope(nameof(AppViewModelOrchestrationTests), "visibility-dedupe.log", LogLevel.Debug);
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, logger: loggerScope.Logger);

            harness.ViewModel.MarkStartupVisibilityResolved();
            harness.ViewModel.HandleWindowVisibilityChanged(false);
            harness.ViewModel.HandleWindowVisibilityChanged(false);

            string logText = loggerScope.DisposeAndReadLogText();
            Assert.Equal(1, CountOccurrences(logText, "mixer-idle-trim | context=window-hidden"));
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenWindowVisibleEnteringEditorTab_AcquiresSessionMonitoring()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.ViewModel.MarkStartupVisibilityResolved();
            harness.ViewModel.SelectedSettingsTabIndex = 3;
            harness.ViewModel.HandleWindowVisibilityChanged(true);

            harness.ViewModel.SelectedSettingsTabIndex = 0;

            Assert.Equal(1, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Output));
            Assert.Equal(1, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Input));
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenWindowVisibleLeavingEditorTab_ReleasesSessionMonitoring()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.ViewModel.MarkStartupVisibilityResolved();
            harness.ViewModel.SelectedSettingsTabIndex = 0;
            harness.ViewModel.HandleWindowVisibilityChanged(true);

            harness.ViewModel.SelectedSettingsTabIndex = 3;

            Assert.Equal(0, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Output));
            Assert.Equal(0, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Input));
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenWindowVisibleSwitchingMixerTabs_SwapsSessionMonitoringMode()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.ViewModel.MarkStartupVisibilityResolved();
            harness.ViewModel.SelectedSettingsTabIndex = 0;
            harness.ViewModel.HandleWindowVisibilityChanged(true);

            harness.ViewModel.SelectedSettingsTabIndex = 1;

            Assert.Equal(1, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Output));
            Assert.Equal(1, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Input));
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenLeavingVisibleOutputTabWithSessions_MarksOutputMixerStale()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            ReplaceWithRealMixers(harness.ViewModel, harness.Audio);
            harness.ViewModel.MarkStartupVisibilityResolved();
            MixerViewModel outputMixer = harness.ViewModel.Mixer;
            outputMixer.Sessions.Add(new AudioSessionItem("Spotify", 40f, isMaster: false, isMic: false, processId: 321));
            harness.ViewModel.SelectedSettingsTabIndex = 0;
            harness.ViewModel.HandleWindowVisibilityChanged(true);

            harness.ViewModel.SelectedSettingsTabIndex = 3;

            Assert.True(outputMixer.RequiresActivationRefresh);
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenReturningToStaleMixerWithSessions_QueuesActivationRefresh()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            ReplaceWithRealMixers(harness.ViewModel, harness.Audio);
            harness.ViewModel.MarkStartupVisibilityResolved();
            MixerViewModel outputMixer = harness.ViewModel.Mixer;
            outputMixer.Sessions.Add(new AudioSessionItem("Spotify", 40f, isMaster: false, isMic: false, processId: 321));
            harness.ViewModel.SelectedSettingsTabIndex = 0;
            harness.ViewModel.HandleWindowVisibilityChanged(true);
            harness.ViewModel.SelectedSettingsTabIndex = 3;

            harness.ViewModel.SelectedSettingsTabIndex = 0;

            Assert.True(harness.ViewModel.GetBackgroundTaskCountForTests() > 0);
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenVisibleActivationRefreshCompletes_ReleasesActivationDebounce()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            int originalDebounceMs = RuntimeTuningConfig.VisibleMixerActivationRefreshDebounceMs;
            RuntimeTuningConfig.VisibleMixerActivationRefreshDebounceMs = 25;

            try
            {
                using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
                using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
                ReplaceWithRealMixers(harness.ViewModel, harness.Audio);
                harness.ViewModel.MarkStartupVisibilityResolved();
                MixerViewModel outputMixer = harness.ViewModel.Mixer;
                outputMixer.Sessions.Add(new AudioSessionItem("Spotify", 40f, isMaster: false, isMic: false, processId: 321));
                harness.ViewModel.SelectedSettingsTabIndex = 0;
                harness.ViewModel.HandleWindowVisibilityChanged(true);
                harness.ViewModel.SelectedSettingsTabIndex = 3;

                harness.ViewModel.SelectedSettingsTabIndex = 0;
                harness.ViewModel.HandleWindowVisibilityChanged(false);

                WaitForQueuedBackgroundTasks(harness.ViewModel);

                Assert.Null(TestPrivateAccess.GetField<CancellationTokenSource?>(harness.ViewModel, "_visibleMixerActivationRefreshDebounceCts"));
            }
            finally
            {
                RuntimeTuningConfig.VisibleMixerActivationRefreshDebounceMs = originalDebounceMs;
            }
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenSwitchingFromOutputToInput_MarksOutputMixerStale()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            ReplaceWithRealMixers(harness.ViewModel, harness.Audio);
            harness.ViewModel.MarkStartupVisibilityResolved();
            MixerViewModel outputMixer = harness.ViewModel.Mixer;
            outputMixer.Sessions.Add(new AudioSessionItem("Spotify", 40f, isMaster: false, isMic: false, processId: 321));
            harness.ViewModel.SelectedSettingsTabIndex = 0;
            harness.ViewModel.HandleWindowVisibilityChanged(true);

            harness.ViewModel.SelectedSettingsTabIndex = 1;

            Assert.True(outputMixer.RequiresActivationRefresh);
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenReturningToNonStaleMixerWithSessions_DoesNotQueueActivationRefresh()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelOrchestrationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            ReplaceWithRealMixers(harness.ViewModel, harness.Audio);
            harness.ViewModel.MarkStartupVisibilityResolved();
            MixerViewModel outputMixer = harness.ViewModel.Mixer;
            outputMixer.Sessions.Add(new AudioSessionItem("Spotify", 40f, isMaster: false, isMic: false, processId: 321));
            harness.ViewModel.SelectedSettingsTabIndex = 3;
            harness.ViewModel.HandleWindowVisibilityChanged(true);

            harness.ViewModel.SelectedSettingsTabIndex = 0;

            Assert.False(outputMixer.RequiresActivationRefresh);
            Assert.Equal(0, harness.ViewModel.GetBackgroundTaskCountForTests());
        });
    }

    [Theory]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 1, 0, true)]
    [InlineData(0, 0, 1, true)]
    [InlineData(0, 0, 0, false)]
    public void HasPendingMixerRefreshSignals_ReturnsExpectedValue(
        int pendingSessionCreatedSignals,
        int pendingSessionLifecycleSignals,
        int pendingShowWindowMixerRefreshSignals,
        bool expected)
    {
        bool actual = AppViewModel.HasPendingMixerRefreshSignals(
            pendingSessionCreatedSignals,
            pendingSessionLifecycleSignals,
            pendingShowWindowMixerRefreshSignals);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void QueueSteamBigPictureSignalEvaluation_WhenFallbackRevalidationActive_QueuesDebouncedWork()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            AppViewModel viewModel = AppViewModelHarnessBuilder.CreateOrchestrationViewModelShell();
            viewModel.ConfigureSteamBigPictureFallbackForTests(
            [
                new AudioRoutine
                {
                    Id = "routine-steam",
                    Name = "Steam",
                    Enabled = true,
                    TriggerKind = RoutineTriggerKind.SteamBigPicture,
                    OutputDeviceId = "out-1",
                    OutputDeviceName = "Speakers",
                }
            ]);

            InvokeNonPublicVoid(viewModel, "QueueSteamBigPictureSignalEvaluation");

            Assert.True(viewModel.GetPendingSteamBigPictureSignalCountForTests() > 0);
            Assert.True(viewModel.HasSteamBigPictureDebounceForTests());
        });
    }

    [Fact]
    public void OnAudioSessionLifecycleChanged_WhenFallbackRevalidationActive_QueuesSteamRevalidation()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var loggerScope = new TestLoggerScope(nameof(AppViewModelOrchestrationTests), "steam-fallback-session.log", LogLevel.Trace);
            var window = TestWindowFactory.CreateOffscreenWindow();

            AppViewModel viewModel = CreateSessionCreatedViewModelShell(loggerScope.Logger, window);
            viewModel.ConfigureSteamBigPictureFallbackForTests(
            [
                new AudioRoutine
                {
                    Id = "routine-steam",
                    Name = "Steam",
                    Enabled = true,
                    TriggerKind = RoutineTriggerKind.SteamBigPicture,
                    OutputDeviceId = "out-1",
                }
            ]);

            InvokeNonPublicVoid(
                viewModel,
                "OnAudioSessionLifecycleChanged",
                new AudioSessionLifecycleSignal(
                    AudioMixerMode.Output,
                    AudioSessionLifecycleSignalKind.Disconnected,
                    "session-1",
                    DisconnectReason: AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval));

            Assert.True(viewModel.GetPendingSteamBigPictureSignalCountForTests() > 0);
            WaitForQueuedBackgroundTasks(viewModel);
        });
    }

    [Fact]
    public void HandleWindowVisibilityChanged_WhenFallbackRevalidationActive_QueuesSteamRevalidation()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            AppViewModel viewModel = CreateVisibilityRefreshViewModelShell();
            viewModel.ConfigureSteamBigPictureFallbackForTests(
            [
                new AudioRoutine
                {
                    Id = "routine-steam",
                    Name = "Steam",
                    Enabled = true,
                    TriggerKind = RoutineTriggerKind.SteamBigPicture,
                    OutputDeviceId = "out-1",
                }
            ]);

            viewModel.HandleWindowVisibilityChanged(true);

            Assert.True(viewModel.GetPendingSteamBigPictureSignalCountForTests() > 0);
        });
    }

    [Fact]
    public void HandleWindowVisibilityChanged_WhenPendingHiddenSessionSignals_QueuesCoalescedMixerRefresh()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            AppViewModel viewModel = CreateVisibilityRefreshViewModelShell();

            viewModel.SetPendingSessionCreatedSignalsForTests(1);

            viewModel.HandleWindowVisibilityChanged(true);

            AppViewModel.PendingMixerRefreshSignalsForTests pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(1, pendingSignals.PendingShowWindowMixerRefreshSignals);
            Assert.True(pendingSignals.HasSessionRefreshDebounce);

            WaitForQueuedBackgroundTasks(viewModel);

            pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(0, pendingSignals.PendingSessionCreatedSignals);
            Assert.Equal(0, pendingSignals.PendingShowWindowMixerRefreshSignals);
            Assert.False(pendingSignals.HasSessionRefreshDebounce);
        });
    }

    [Fact]
    public void OnAudioSessionLifecycleChanged_WhenSharedRowSignalArrivesFromOtherMixer_QueuesVisibleMixerRefresh()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = TestWindowFactory.CreateOffscreenWindow();
            AppViewModel viewModel = CreateSessionCreatedViewModelShell(Logger.Instance, window);

            InvokeNonPublicVoid(
                viewModel,
                "OnAudioSessionLifecycleChanged",
                new AudioSessionLifecycleSignal(
                    AudioMixerMode.Input,
                    AudioSessionLifecycleSignalKind.VolumeChanged,
                    "SystemSoundsSession-1",
                    AffectsSharedRows: true));

            AppViewModel.PendingMixerRefreshSignalsForTests pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();
            Assert.False(pendingSignals.HasSessionRefreshDebounce);
            Assert.Equal(1, pendingSignals.PendingInputSessionLifecycleSignals);
        });
    }

    [Fact]
    public void QueueShowWindowMixerRefresh_WhenWindowHidden_DrainsSharedPendingSignals_AndSkipsMixerRefresh()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var loggerScope = new TestLoggerScope(nameof(AppViewModelOrchestrationTests), "show-window-hidden.log", LogLevel.Trace);
            var window = TestWindowFactory.CreateOffscreenWindow();

            AppViewModel viewModel = CreateSessionCreatedViewModelShell(loggerScope.Logger, window);

            InvokeNonPublicVoid(viewModel, "QueueShowWindowMixerRefresh", MixerRefreshTarget.Output);

            AppViewModel.PendingMixerRefreshSignalsForTests pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(1, pendingSignals.PendingShowWindowMixerRefreshSignals);
            Assert.True(pendingSignals.HasSessionRefreshDebounce);

            WaitForQueuedBackgroundTasks(viewModel);

            pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(0, pendingSignals.PendingShowWindowMixerRefreshSignals);
            Assert.False(pendingSignals.HasSessionRefreshDebounce);

            string logText = loggerScope.DisposeAndReadLogText();
            Assert.Contains("Show-window events coalesced=1", logText, StringComparison.Ordinal);
            Assert.DoesNotContain(AppConstants.Audio.LogEvents.Diagnostics.MixerRefreshCoordination, logText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void HasPendingMixerRestoreWork_WhenQueueWorkStillRunning_ReturnsTrue()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            AppViewModel viewModel = CreateVisibilityRefreshViewModelShell();

            TestPrivateAccess.SetField(viewModel, "_pendingMixerRestoreQueueCount", 1);

            MethodInfo? method = viewModel.GetType().GetMethod("HasPendingMixerRestoreWork", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            bool hasPendingRestoreWork = (bool)method!.Invoke(viewModel, null)!;

            Assert.True(hasPendingRestoreWork);
        });
    }

    [Fact]
    public void DrainPendingMixerRefreshSignals_ResetsAllSources_AndReturnsCombinedCount()
    {
        int pendingSessionCreatedSignals = 2;
        int pendingSessionLifecycleSignals = 3;
        int pendingShowWindowMixerRefreshSignals = 1;

        int totalSignals = AppViewModel.DrainPendingMixerRefreshSignals(
            ref pendingSessionCreatedSignals,
            ref pendingSessionLifecycleSignals,
            ref pendingShowWindowMixerRefreshSignals);

        Assert.Equal(6, totalSignals);
        Assert.Equal(0, pendingSessionCreatedSignals);
        Assert.Equal(0, pendingSessionLifecycleSignals);
        Assert.Equal(0, pendingShowWindowMixerRefreshSignals);
    }

    [Fact]
    public void OnRoutineAppProcessStarted_WhenFallbackRevalidationActive_QueuesSteamRevalidation()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var processMonitor = new FakeProcessLifecycleMonitor();
            var steamMonitor = new FakeSteamBigPictureSignalMonitor
            {
                StartResult = new SteamBigPictureSignalMonitorStartResult(false, "inactive", "win-event-unavailable")
            };
            using var harness = AppViewModelHarnessBuilder.CreateRoutineStatefulHarness(
                Dispatcher.CurrentDispatcher,
                Logger.Instance,
                processMonitor,
                steamMonitor);
            AppViewModel viewModel = harness.ViewModel;
            var settings = new Settings
            {
                Routines = new RoutinesSettings
                {
                    Items =
                    [
                        new AudioRoutine
                        {
                            Id = "routine-steam",
                            Name = "Steam",
                            Enabled = true,
                            TriggerKind = RoutineTriggerKind.SteamBigPicture,
                            OutputDeviceId = "out-1",
                            OutputDeviceName = "Speakers",
                        }
                    ]
                }
            };

            viewModel.SetCachedSettingsForTests(settings);
            InvokeNonPublicVoid(viewModel, "RefreshRoutineRuntimeTriggers");
            viewModel.EnableRoutineAppStartMonitoring();
            processMonitor.FireProcessStarted(4242);

            Assert.True(viewModel.GetPendingSteamBigPictureSignalCountForTests() > 0);
        });
    }

    [Fact]
    public void SteamBigPictureFallbackRetry_WhenHookRecovers_LeavesFallbackMode()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var steamMonitor = new FakeSteamBigPictureSignalMonitor();
            using var harness = AppViewModelHarnessBuilder.CreateRoutineStatefulHarness(
                Dispatcher.CurrentDispatcher,
                Logger.Instance,
                new FakeProcessLifecycleMonitor(),
                steamMonitor);
            AppViewModel viewModel = harness.ViewModel;
            viewModel.ConfigureSteamBigPictureFallbackForTests(
            [
                new AudioRoutine
                {
                    Id = "routine-steam",
                    Name = "Steam",
                    Enabled = true,
                    TriggerKind = RoutineTriggerKind.SteamBigPicture,
                    OutputDeviceId = "out-1",
                }
            ]);

            bool recovered = viewModel.TryRecoverSteamBigPictureMonitorFromFallbackForTests();

            Assert.True(recovered);
            Assert.Equal(1, steamMonitor.StartCallCount);
            Assert.True(steamMonitor.IsRunning);
            Assert.False(viewModel.IsUsingSteamBigPictureFallbackForTests());
        });
    }

    [Fact]
    public void SteamBigPictureFallbackRetry_WhenHookStillFails_RemainsInFallbackMode()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var steamMonitor = new FakeSteamBigPictureSignalMonitor
            {
                StartResult = new SteamBigPictureSignalMonitorStartResult(false, "inactive", "win-event-unavailable")
            };
            using var harness = AppViewModelHarnessBuilder.CreateRoutineStatefulHarness(
                Dispatcher.CurrentDispatcher,
                Logger.Instance,
                new FakeProcessLifecycleMonitor(),
                steamMonitor);
            AppViewModel viewModel = harness.ViewModel;
            viewModel.ConfigureSteamBigPictureFallbackForTests(
            [
                new AudioRoutine
                {
                    Id = "routine-steam",
                    Name = "Steam",
                    Enabled = true,
                    TriggerKind = RoutineTriggerKind.SteamBigPicture,
                    OutputDeviceId = "out-1",
                }
            ]);

            bool recovered = viewModel.TryRecoverSteamBigPictureMonitorFromFallbackForTests();

            Assert.False(recovered);
            Assert.Equal(1, steamMonitor.StartCallCount);
            Assert.False(steamMonitor.IsRunning);
            Assert.True(viewModel.IsUsingSteamBigPictureFallbackForTests());
        });
    }

    [Fact]
    public void OnAudioSessionLifecycleChanged_WhenWindowHidden_PreservesPendingSignals_AndSkipsScheduling()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = TestWindowFactory.CreateOffscreenWindow();

            AppViewModel viewModel = CreateSessionCreatedViewModelShell(Logger.Instance, window);
            InvokeNonPublicVoid(
                viewModel,
                "OnAudioSessionLifecycleChanged",
                new AudioSessionLifecycleSignal(
                    AudioMixerMode.Output,
                    AudioSessionLifecycleSignalKind.Disconnected,
                    "session-1",
                    DisconnectReason: AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval));

            AppViewModel.PendingMixerRefreshSignalsForTests pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(1, pendingSignals.PendingSessionLifecycleSignals);
            Assert.Equal(0, viewModel.GetBackgroundTaskCountForTests());
            Assert.Equal(1, pendingSignals.PendingSessionLifecycleSignals);
            Assert.False(pendingSignals.HasSessionRefreshDebounce);
        });
    }

    [Fact]
    public void OnAudioSessionCreated_WhenWindowHidden_PreservesPendingSignals_AndSkipsScheduling()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = TestWindowFactory.CreateOffscreenWindow();

            AppViewModel viewModel = CreateSessionCreatedViewModelShell(Logger.Instance, window);

            InvokeNonPublicVoid(viewModel, "OnAudioSessionCreated", AudioMixerMode.Input);

            AppViewModel.PendingMixerRefreshSignalsForTests pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(1, pendingSignals.PendingSessionCreatedSignals);
            Assert.Equal(0, pendingSignals.PendingOutputSessionCreatedSignals);
            Assert.Equal(1, pendingSignals.PendingInputSessionCreatedSignals);
            Assert.Equal(0, viewModel.GetBackgroundTaskCountForTests());
            Assert.Equal(1, pendingSignals.PendingSessionCreatedSignals);
            Assert.False(pendingSignals.HasSessionRefreshDebounce);
        });
    }

    [Fact]
    public void OnAudioSessionLifecycleChanged_WhenRecordingSignalQueuedWhileWindowHidden_PreservesInputPendingSignalsOnly()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = TestWindowFactory.CreateOffscreenWindow();

            AppViewModel viewModel = CreateSessionCreatedViewModelShell(Logger.Instance, window);
            InvokeNonPublicVoid(
                viewModel,
                "OnAudioSessionLifecycleChanged",
                new AudioSessionLifecycleSignal(
                    AudioMixerMode.Input,
                    AudioSessionLifecycleSignalKind.VolumeChanged,
                    "capture-session-1"));

            AppViewModel.PendingMixerRefreshSignalsForTests pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(1, pendingSignals.PendingSessionLifecycleSignals);
            Assert.Equal(0, pendingSignals.PendingOutputSessionLifecycleSignals);
            Assert.Equal(1, pendingSignals.PendingInputSessionLifecycleSignals);
            Assert.Equal(0, viewModel.GetBackgroundTaskCountForTests());
            Assert.False(pendingSignals.HasSessionRefreshDebounce);
        });
    }

    [Fact]
    public void OnAudioSessionLifecycleChanged_WhenNonDefaultPlaybackEndpointChangesWhileWindowHidden_PreservesOutputPendingSignalsOnly()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = TestWindowFactory.CreateOffscreenWindow();

            AppViewModel viewModel = CreateSessionCreatedViewModelShell(Logger.Instance, window);
            InvokeNonPublicVoid(
                viewModel,
                "OnAudioSessionLifecycleChanged",
                new AudioSessionLifecycleSignal(
                    AudioMixerMode.Output,
                    AudioSessionLifecycleSignalKind.VolumeChanged,
                    "render-session-2",
                    EndpointId: "render-secondary"));

            AppViewModel.PendingMixerRefreshSignalsForTests pendingSignals = viewModel.GetPendingMixerRefreshSignalsForTests();

            Assert.Equal(1, pendingSignals.PendingSessionLifecycleSignals);
            Assert.Equal(1, pendingSignals.PendingOutputSessionLifecycleSignals);
            Assert.Equal(0, pendingSignals.PendingInputSessionLifecycleSignals);
            Assert.Equal(0, viewModel.GetBackgroundTaskCountForTests());
            Assert.False(pendingSignals.HasSessionRefreshDebounce);
        });
    }

    [Fact]
    public void BuildDeviceTopologyFingerprint_IsOrderInsensitive_WithinEachDirection()
    {
        var outputA = new[]
        {
            new CycleDevice { Id = "out-2", Name = "Headset" },
            new CycleDevice { Id = "out-1", Name = "Speakers" },
        };
        var inputA = new[]
        {
            new CycleDevice { Id = "in-2", Name = "USB Mic" },
            new CycleDevice { Id = "in-1", Name = "Webcam Mic" },
        };

        var outputB = new[]
        {
            new CycleDevice { Id = "out-1", Name = "Speakers" },
            new CycleDevice { Id = "out-2", Name = "Headset" },
        };
        var inputB = new[]
        {
            new CycleDevice { Id = "in-1", Name = "Webcam Mic" },
            new CycleDevice { Id = "in-2", Name = "USB Mic" },
        };

        string first = AppViewModel.BuildDeviceTopologyFingerprint(outputA, inputA);
        string second = AppViewModel.BuildDeviceTopologyFingerprint(outputB, inputB);

        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildDeviceTopologyFingerprint_Changes_WhenIdOrNameChanges()
    {
        var output = new[]
        {
            new CycleDevice { Id = "out-1", Name = "Speakers" },
        };
        var input = new[]
        {
            new CycleDevice { Id = "in-1", Name = "Mic" },
        };

        string baseline = AppViewModel.BuildDeviceTopologyFingerprint(output, input);

        string renamed = AppViewModel.BuildDeviceTopologyFingerprint(
            [new CycleDevice { Id = "out-1", Name = "Speakers (Renamed)" }],
            input);

        string changedId = AppViewModel.BuildDeviceTopologyFingerprint(
            [new CycleDevice { Id = "out-9", Name = "Speakers" }],
            input);

        Assert.NotEqual(baseline, renamed);
        Assert.NotEqual(baseline, changedId);
    }

    [Fact]
    public void BuildDuplicateHotkeyConflicts_ReturnsConflict_WhenSameComboUsedByTwoActions()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+I"
                }
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Ctrl+Alt+O"
                }
            }
        };

        List<string> conflicts = AppViewModel.BuildDuplicateHotkeyConflicts(settings);

        string conflict = Assert.Single(conflicts);
        Assert.Contains("Ctrl+Alt+O", conflict, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Output switch", conflict, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Show/hide app", conflict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDuplicateHotkeyKeySet_ReturnsNormalizedDuplicateHotkey()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O"
                }
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Control+Alt+O"
                }
            }
        };

        HashSet<string> duplicateKeys = AppViewModel.BuildDuplicateHotkeyKeySet(settings);

        Assert.Single(duplicateKeys);
        Assert.Contains("Ctrl+Alt+O", duplicateKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDuplicateHotkeyKeySet_IncludesShowCurrentTrackConflicts()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = string.Empty
                },
                Media = new HotkeysMediaSettings
                {
                    ShowCurrentTrack = "Ctrl+Alt+Y",
                    PlayPause = "Control+Alt+Y",
                    NextTrack = "Ctrl+Alt+J",
                    PreviousTrack = "Ctrl+Alt+K"
                }
            }
        };

        HashSet<string> duplicateKeys = AppViewModel.BuildDuplicateHotkeyKeySet(settings);

        Assert.Single(duplicateKeys);
        Assert.Contains("Ctrl+Alt+Y", duplicateKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDuplicateHotkeyKeySet_IncludesStopMediaConflicts()
    {
        var settings = new Settings
        {
            Hotkeys = new HotkeysSettings
            {
                Media = new HotkeysMediaSettings
                {
                    PlayPause = "Ctrl+Alt+P",
                    NextTrack = "Ctrl+Alt+J",
                    PreviousTrack = "Control+Alt+J",
                }
            }
        };

        HashSet<string> duplicateKeys = AppViewModel.BuildDuplicateHotkeyKeySet(settings);

        Assert.Single(duplicateKeys);
        Assert.Contains("Ctrl+Alt+J", duplicateKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSettingsForCommit_Blocks_WhenDuplicateHotkeysExist()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O",
                    ReverseSwitchHotkey = "Ctrl+Alt+Shift+O"
                },
                Input = new DeviceSwitchingInputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+I",
                    ReverseSwitchHotkey = "Ctrl+Alt+Shift+I"
                }
            },
            Hotkeys = new HotkeysSettings
            {
                App = new HotkeysAppSettings
                {
                    ToggleAppVisibility = "Ctrl+Alt+O"
                },
                Media = new HotkeysMediaSettings
                {
                    PlayPause = "Ctrl+Alt+P"
                }
            }
        };

        AppViewModel.SettingsCommitValidationResult result = AppViewModel.ValidateSettingsForCommit(settings);

        Assert.True(result.HasBlockingIssues);
        Assert.Contains(result.BlockingMessages, message => message.Contains("Duplicate hotkey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDuplicateHotkeyConflicts_BlocksRoutineToRoutineSharing_AndBuiltInToRoutineConflicts()
    {
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    HotkeysEnabled = true,
                    SwitchHotkey = "Ctrl+Alt+O"
                }
            },
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        Hotkey = "Ctrl+Alt+R"
                    },
                    new AudioRoutine
                    {
                        Id = "routine-2",
                        Name = "Headset",
                        Enabled = true,
                        OutputDeviceId = "out-2",
                        OutputDeviceName = "Headset",
                        Hotkey = "Ctrl+Alt+R"
                    },
                    new AudioRoutine
                    {
                        Id = "routine-3",
                        Name = "Conflict",
                        Enabled = true,
                        OutputDeviceId = "out-3",
                        OutputDeviceName = "TV",
                        Hotkey = "Ctrl+Alt+O"
                    }
                ]
            }
        };

        List<string> conflicts = AppViewModel.BuildDuplicateHotkeyConflicts(settings);

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, entry => entry.Contains("Ctrl+Alt+O", StringComparison.OrdinalIgnoreCase)
            && entry.Contains("Output switch", StringComparison.OrdinalIgnoreCase)
            && entry.Contains("Routine: Conflict", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(conflicts, entry => entry.Contains("Ctrl+Alt+R", StringComparison.OrdinalIgnoreCase)
            && entry.Contains("Routine: Desk", StringComparison.OrdinalIgnoreCase)
            && entry.Contains("Routine: Headset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateSettingsForCommit_Blocks_InvalidRoutineDefinitions()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = string.Empty,
                        Enabled = true,
                        Hotkey = "Ctrl+Alt+R"
                    }
                ]
            }
        };

        AppViewModel.SettingsCommitValidationResult result = AppViewModel.ValidateSettingsForCommit(settings);

        Assert.True(result.HasBlockingIssues);
        Assert.Contains(result.BlockingMessages, message => message.Contains("must have a name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateSettingsForCommit_Blocks_EnabledRoutineWithoutAnyTrigger()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        Hotkey = string.Empty
                    }
                ]
            }
        };

        AppViewModel.SettingsCommitValidationResult result = AppViewModel.ValidateSettingsForCommit(settings);

        Assert.True(result.HasBlockingIssues);
        Assert.Contains(result.BlockingMessages, message => message.Contains("must have a hotkey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateSettingsForCommit_Blocks_EnabledRoutineWithoutHotkey_WhenOnlyTrayTriggerConfigured()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        Hotkey = string.Empty,
                        ShowInTrayMenu = true,
                    }
                ]
            }
        };

        AppViewModel.SettingsCommitValidationResult result = AppViewModel.ValidateSettingsForCommit(settings);

        Assert.True(result.HasBlockingIssues);
        Assert.Contains(result.BlockingMessages, message => message.Contains("must have a hotkey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateSettingsForCommit_Allows_EnabledRoutineWithoutHotkey_WhenStartupTriggerConfigured()
    {
        var settings = new Settings
        {
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        Hotkey = string.Empty,
                        UsesApplicationTrigger = true,
                        TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                        ShowInTrayMenu = true,
                    }
                ]
            }
        };

        AppViewModel.SettingsCommitValidationResult result = AppViewModel.ValidateSettingsForCommit(settings);

        Assert.False(result.HasBlockingIssues);
    }

    [Theory]
    [InlineData("Ctrl+Alt+H", "Control+Alt+H", true)]
    [InlineData("Ctrl+Alt+.", "Ctrl+Alt+period", true)]
    [InlineData("Ctrl+Alt+H", "Ctrl+Shift+H", false)]
    public void AreHotkeyStringsEquivalent_NormalizesAliases(string left, string right, bool expected)
    {
        bool equivalent = AppViewModel.AreHotkeyStringsEquivalent(left, right);

        Assert.Equal(expected, equivalent);
    }

    private static AppViewModel CreateRefreshViewModelShell(Logger? logger = null)
    {
        AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell(logger);
        TestPrivateAccess.SetField(viewModel, "_dispatcher", Dispatcher.CurrentDispatcher);
        TestPrivateAccess.SetField(viewModel, "_refreshCoordinator", new AppRefreshCoordinator());
        return viewModel;
    }

    private static AppViewModel CreateVisibilityRefreshViewModelShell()
    {
        return AppViewModelHarnessBuilder.CreateOrchestrationViewModelShell();
    }

    private static AppViewModel CreateSessionCreatedViewModelShell(Logger logger, Window window)
    {
        return AppViewModelHarnessBuilder.CreateOrchestrationViewModelShell(logger, window, includeRoutineStateCollections: true);
    }

    private static void ReplaceWithRealMixers(AppViewModel viewModel, AudioDeviceService audio)
    {
        AppViewModelHarnessBuilder.EnsureDeviceCacheInitialized(audio);
        MixerViewModel outputMixer = new(audio, Dispatcher.CurrentDispatcher, AudioMixerMode.Output);
        MixerViewModel inputMixer = new(audio, Dispatcher.CurrentDispatcher, AudioMixerMode.Input);
        MixerViewModel.ConnectSharedSessionPair(outputMixer, inputMixer);

        TestPrivateAccess.SetField(viewModel, "_mixer", outputMixer);
        TestPrivateAccess.SetField(viewModel, "_inputMixer", inputMixer);
        TestPrivateAccess.SetField(viewModel, "_mixerFactory", () => outputMixer);
        TestPrivateAccess.SetField(viewModel, "_inputMixerFactory", () => inputMixer);
        TestPrivateAccess.SetField(viewModel, "_mixersConnected", true);
    }

    private static void InvokeNonPublicVoid(object target, string methodName, params object?[]? args)
    {
        MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private static void WaitForQueuedBackgroundTasks(AppViewModel viewModel)
    {
        TestPrivateAccess.RunTaskOnDispatcher(viewModel.WaitForQueuedBackgroundTasksForTestsAsync());
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}


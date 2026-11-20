using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.Services.Diagnostics;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelAudioTestingTests
{
    private static readonly AudioEndpointReference TestOutput = new("output-1", "Test output");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public Task SwitchToSelectedRejectsInvalidSelectionsAndReusesTheDeviceAction(int tab) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var testService = new RecordingAudioEndpointTestService(AudioEndpointTestState.Idle);
        using var workspace = new TestSettingsWorkspace(nameof(SwitchToSelectedRejectsInvalidSelectionsAndReusesTheDeviceAction));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, audioEndpointTestService: testService);
        var model = harness.ViewModel;
        var target = new CycleDevice { Id = "fixture-target", Name = "Fixture target" };
        var cycle = tab == 0 ? model.OutputCycleDevices : model.InputCycleDevices;
        var selection = tab == 0 ? model.SelectedOutputCycleDevices : model.SelectedInputCycleDevices;
        cycle.Add(target);
        cycle.Add(new CycleDevice { Id = "fixture-other", Name = "Fixture other" });
        model.SelectedSettingsTabIndex = tab;
        var command = Assert.IsType<RelayCommand>(model.SwitchToSelectedDeviceCommand);
        command.Execute(null);
        selection.Add(target);
        command.Execute(null);
        Assert.Equal(0, testService.StopCount);
        var inventory = new AudioDeviceInventory([target]);
        TestPrivateAccess.InvokeNonPublic(model, "ApplyAvailableDeviceInventory", inventory, inventory);
        selection.Add(cycle[1]);
        command.Execute(null);
        selection.Clear();
        selection.Add(target);
        cycle.Remove(target);
        command.Execute(null);
        cycle.Add(target);
        model.SelectedSettingsTabIndex = 2;
        command.Execute(null);
        Assert.Equal(0, testService.StopCount);
        model.SelectedSettingsTabIndex = tab;
        Assert.True(command.CanExecute(null));

        // Retire the native switch coordinator so this command-path test cannot touch actual devices.
        TestPrivateAccess.GetField<AppSwitchCommandCoordinator>(model, "_switchCoordinator").Dispose();
        command.Execute(null);
        await command.LastExecutionTaskForTests;
        Assert.Equal(1, testService.StopCount);
        var history = TestPrivateAccess.GetField<Lazy<ExecutionHistoryService>>(model, "_executionHistory").Value;
        Assert.Equal("switch-canceled", Assert.Single(history.GetEntries()).DiagCode);
        Assert.Equal(AudioEndpointTestStopReason.Replaced, await testService.StopObserved.Task);
        var contextAction = Assert.IsType<RelayCommand>(tab == 0 ? model.SetDefaultOutputDeviceCommand : model.SetDefaultInputDeviceCommand);
        contextAction.Execute(target);
        await contextAction.LastExecutionTaskForTests;
        Assert.Equal(2, testService.StopCount);
        Assert.Equal(2, history.GetEntries().Count);
        Assert.All(history.GetEntries(), entry => Assert.True(entry.Skipped));
        Assert.Null(model.ActiveDeviceSwitchFeedback);
        Assert.Empty(harness.Messages.ErrorMessages);
        model.BeginRuntimeShutdown();
        command.Execute(null);
        Assert.False(command.CanExecute(null));
        Assert.Equal(2, testService.StopCount);
    });

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public Task UnavailableDirectSwitchRecordsOneOutcomeAndShowsFeedbackWithoutOverlays(bool output, bool overlays) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(UnavailableDirectSwitchRecordsOneOutcomeAndShowsFeedbackWithoutOverlays));
        using var logger = TestLoggerScope.CreateFileBacked(nameof(AppViewModelAudioTestingTests), "direct-switch.log");
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, logger: logger.Logger);
        harness.SetCachedSettings(new Settings { DeviceSwitching = new() { BluetoothReconnectEnabled = false } });
        var model = harness.ViewModel;
        TestPrivateAccess.GetField<OverlayService>(model, "_overlay").UpdateEnabled(overlays);
        var target = new CycleDevice { Id = $"missing-fixture-{Guid.NewGuid():N}", Name = $"Missing fixture {Guid.NewGuid():N}" };
        var command = Assert.IsType<RelayCommand>(output ? model.SetDefaultOutputDeviceCommand : model.SetDefaultInputDeviceCommand);
        command.Execute(target);
        await command.LastExecutionTaskForTests;
        var entry = Assert.Single(TestPrivateAccess.GetField<Lazy<ExecutionHistoryService>>(model, "_executionHistory").Value.GetEntries());
        Assert.Equal("manual", entry.Source);
        Assert.Equal(output ? "switch-output-device" : "switch-input-device", entry.Action);
        Assert.Equal("switch-device-unavailable", entry.DiagCode);
        Assert.False(entry.Success);
        Assert.False(entry.Skipped);
        Assert.Empty(harness.Messages.ErrorMessages);
        var feedback = output ? model.OutputDeviceSwitchFeedback : model.InputDeviceSwitchFeedback;
        if (overlays)
        {
            Assert.Null(feedback);
            Assert.Equal(OverlayDeviceKind.Error, Assert.Single(harness.OverlayMessages).kind);
        }
        else
        {
            Assert.Empty(harness.OverlayMessages);
            Assert.NotNull(feedback);
            model.Localization.SetLanguage("en");
            Assert.Contains("Device unavailable", model.Localization.Format(feedback), StringComparison.Ordinal);
            model.Localization.SetLanguage("es");
            Assert.Contains("Dispositivo no disponible", model.Localization.Format(feedback), StringComparison.Ordinal);
            Assert.Contains(target.Name, model.Localization.Format(feedback), StringComparison.Ordinal);
        }
        Assert.Null(output ? model.InputDeviceSwitchFeedback : model.OutputDeviceSwitchFeedback);
        string log = logger.DisposeAndReadLogText();
        Assert.Single(log.Split('\n'), line => line.Contains("direct-device-switch-completed |", StringComparison.Ordinal));
        Assert.Contains("outcome=unavailable", log, StringComparison.Ordinal);
    });

    [Fact]
    public Task FailureBeforeNativeSwitchRecordsOneFailureWithoutAModalDialog() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var service = new RecordingAudioEndpointTestService(AudioEndpointTestState.Idle) { StopError = new IOException("Fixture stop failure") };
        using var workspace = new TestSettingsWorkspace(nameof(FailureBeforeNativeSwitchRecordsOneFailureWithoutAModalDialog));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, audioEndpointTestService: service);
        TestPrivateAccess.GetField<OverlayService>(harness.ViewModel, "_overlay").UpdateEnabled(false);
        var command = Assert.IsType<RelayCommand>(harness.ViewModel.SetDefaultOutputDeviceCommand);
        command.Execute(new CycleDevice { Id = "fixture", Name = "Fixture target" });
        await command.LastExecutionTaskForTests;
        var entry = Assert.Single(TestPrivateAccess.GetField<Lazy<ExecutionHistoryService>>(harness.ViewModel, "_executionHistory").Value.GetEntries());
        Assert.Equal("switch-failed", entry.DiagCode);
        Assert.Equal("IOException", entry.Reason);
        Assert.NotNull(harness.ViewModel.OutputDeviceSwitchFeedback);
        Assert.Empty(harness.Messages.ErrorMessages);
        service.StopError = null;
    });

    [Theory]
    [InlineData("")]
    [InlineData("output-1")]
    public async Task MonitorOutputRefresh_PreservesBoundSelectionAndRecoversMissingDevice(string preferredId)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var logger = TestLoggerScope.CreateInMemory("audio-test-monitor-selection.log");
            var service = new RecordingAudioEndpointTestService(AudioEndpointTestState.Idle);
            IReadOnlyList<CycleDevice> outputs = [new() { Id = "output-1", Name = "Speakers" }];
            await using var viewModel = new AudioTestingViewModel(service, Dispatcher.CurrentDispatcher, logger.Logger,
                () => (outputs, preferredId), (_, _) => false, TestContext.Current.CancellationToken);
            var selector = new ComboBox
            {
                ItemsSource = viewModel.MonitorOutputDevices,
                DisplayMemberPath = "Name",
            };
            selector.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(viewModel.SelectedMonitorOutput))
            {
                Source = viewModel,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            try
            {
                for (int restart = 0; restart < 3; restart++)
                {
                    viewModel.RefreshAudioTestMonitorOutputOptions();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal(preferredId, Assert.IsType<CycleDevice>(selector.SelectedItem).Id);
                }

                outputs = [];
                viewModel.RefreshAudioTestMonitorOutputOptions();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("Default output", Assert.IsType<CycleDevice>(selector.SelectedItem).Name);
                Assert.Equal(preferredId.Length > 0, viewModel.HasMonitorFallbackNotice);

                outputs = [new() { Id = "output-1", Name = "Speakers" }];
                viewModel.RefreshAudioTestMonitorOutputOptions();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(preferredId, Assert.IsType<CycleDevice>(selector.SelectedItem).Id);
                Assert.False(viewModel.HasMonitorFallbackNotice);
                Assert.Equal(0, service.ConfigureCount);
            }
            finally
            {
                BindingOperations.ClearAllBindings(selector);
                selector.ItemsSource = null;
            }
        });
    }

    [Fact]
    public async Task AudioTestingViewModel_DisposalIgnoresQueuedUiUpdatesAndDisablesCommands()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var logger = TestLoggerScope.CreateInMemory("audio-test-view-model-dispose.log");
            var service = new RecordingAudioEndpointTestService(AudioEndpointTestState.Idle);
            var viewModel = new AudioTestingViewModel(service, Dispatcher.CurrentDispatcher, logger.Logger,
                () => ([], null), (_, _) => false, TestContext.Current.CancellationToken);

            await viewModel.DisposeAsync();
            viewModel.ApplyAudioEndpointTestState(new AudioEndpointTestState(
                12, AudioEndpointTestKind.Input, AudioEndpointTestPhase.Running,
                new AudioEndpointReference("input-1", "Test input"), "Late update"));
            viewModel.TestInputCommand.Execute(new CycleDevice { Id = "input-1", Name = "Test input" });

            Assert.False(viewModel.IsInputTestPanelVisible);
            Assert.False(viewModel.TestInputCommand.CanExecute(new CycleDevice { Id = "input-1" }));
            Assert.Equal(0, service.StartInputCount);
            Assert.Equal(1, service.DisposeCount);
        });
    }

    [Fact]
    public async Task AudioTestingViewModel_StopCancelsMonitorChangeBeforeItReachesService()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var logger = TestLoggerScope.CreateInMemory("audio-test-view-model-stop.log");
            var service = new RecordingAudioEndpointTestService(AudioEndpointTestState.Idle);
            Func<CancellationToken, Task>? queued = null;
            await using var viewModel = new AudioTestingViewModel(service, Dispatcher.CurrentDispatcher, logger.Logger,
                () => ([], null), (work, _) => { queued = work; return true; }, TestContext.Current.CancellationToken);
            viewModel.HearMyself = true;
            Assert.NotNull(queued);

            await viewModel.StopAudioEndpointTestAsync(AudioEndpointTestStopReason.WindowHidden);
            await queued(TestContext.Current.CancellationToken);

            Assert.Equal(1, service.StopCount);
            Assert.Equal(0, service.ConfigureCount);
        });
    }

    [Theory]
    [InlineData((int)AudioEndpointTestStopReason.WindowHidden)]
    [InlineData((int)AudioEndpointTestStopReason.TabChanged)]
    public async Task PrivacyLifecycleStop_BypassesCanceledGeneralBackgroundQueue(int stopReasonValue)
    {
        var stopReason = (AudioEndpointTestStopReason)stopReasonValue;
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var running = new AudioEndpointTestState(
                4,
                AudioEndpointTestKind.Input,
                AudioEndpointTestPhase.Running,
                new AudioEndpointReference("input-1", "Test input"),
                "Microphone test is active.");
            var testService = new RecordingAudioEndpointTestService(running);
            using var workspace = new TestSettingsWorkspace(nameof(PrivacyLifecycleStop_BypassesCanceledGeneralBackgroundQueue));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                workspace,
                Dispatcher.CurrentDispatcher,
                allowBackgroundWork: false,
                audioEndpointTestService: testService);

            harness.ViewModel.RequestStopAudioEndpointTestForTests(stopReason);

            AudioEndpointTestStopReason reason = await testService.StopObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.Equal(stopReason, reason);
            Assert.Equal(1, testService.StopCount);
        });
    }

    [Fact]
    public async Task ShutdownCleanup_DisposesAudioTestServiceDespiteCanceledGeneralBackgroundQueue()
    {
        await SharedStaDispatcherHost.RunAsync(() =>
        {
            var testService = new RecordingAudioEndpointTestService(AudioEndpointTestState.Idle);
            using var workspace = new TestSettingsWorkspace(nameof(ShutdownCleanup_DisposesAudioTestServiceDespiteCanceledGeneralBackgroundQueue));
            var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                workspace,
                Dispatcher.CurrentDispatcher,
                allowBackgroundWork: false,
                audioEndpointTestService: testService);

            harness.Dispose();

            Assert.Equal(1, testService.DisposeCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DispatchedStateUpdate_IgnoresOlderOperationRevision()
    {
        await SharedStaDispatcherHost.RunAsync(() =>
        {
            var testService = new RecordingAudioEndpointTestService(AudioEndpointTestState.Idle);
            using var workspace = new TestSettingsWorkspace(nameof(DispatchedStateUpdate_IgnoresOlderOperationRevision));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                workspace,
                Dispatcher.CurrentDispatcher,
                audioEndpointTestService: testService);

            harness.ViewModel.ApplyAudioEndpointTestStateForTests(new AudioEndpointTestState(
                12,
                AudioEndpointTestKind.Output,
                AudioEndpointTestPhase.Running,
                TestOutput,
                "Replacement output test is running."));
            harness.ViewModel.ApplyAudioEndpointTestStateForTests(new AudioEndpointTestState(
                11,
                AudioEndpointTestKind.Output,
                AudioEndpointTestPhase.Failed,
                TestOutput,
                "Stale failure."));

            Assert.True(harness.ViewModel.AudioTesting.IsOutputTestRunning);
            Assert.Equal("Replacement output test is running.", harness.ViewModel.AudioTesting.Status);
            return Task.CompletedTask;
        });
    }

    private sealed class RecordingAudioEndpointTestService(AudioEndpointTestState initialState) : IAudioEndpointTestService
    {
        private AudioEndpointTestState _state = initialState;

        public event Action<AudioEndpointTestState>? StateChanged
        {
            add { }
            remove { }
        }

        public AudioEndpointTestState CurrentState => _state;

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }
        public int StartInputCount { get; private set; }
        public int ConfigureCount { get; private set; }
        public Exception? StopError { get; set; }

        public TaskCompletionSource<AudioEndpointTestStopReason> StopObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AudioInputLevelSnapshot ReadInputLevel() => AudioInputLevelSnapshot.Silence;

        public Task StartOutputTestAsync(AudioEndpointReference endpoint, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartInputTestAsync(
            AudioEndpointReference endpoint,
            AudioEndpointReference? initialMonitorEndpoint,
            CancellationToken cancellationToken = default)
        {
            StartInputCount++;
            return Task.CompletedTask;
        }

        public Task ConfigureInputMonitoringAsync(
            bool enabled,
            AudioEndpointReference? monitorEndpoint,
            float volume,
            CancellationToken cancellationToken = default)
        {
            ConfigureCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(
            AudioEndpointTestStopReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StopError != null) return Task.FromException(StopError);
            StopCount++;
            _state = AudioEndpointTestState.Idle;
            StopObserved.TrySetResult(reason);
            return Task.CompletedTask;
        }

        public Task ReconcileActiveEndpointsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

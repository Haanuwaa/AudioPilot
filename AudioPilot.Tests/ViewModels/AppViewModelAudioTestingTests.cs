using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelAudioTestingTests
{
    private static readonly AudioEndpointReference TestOutput = new("output-1", "Test output");

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

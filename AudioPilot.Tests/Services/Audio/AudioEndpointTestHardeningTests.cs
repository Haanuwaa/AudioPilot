using System.Windows.Threading;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioEndpointTestHardeningTests
{
    [Theory]
    [InlineData("AudioPilot could not update microphone monitoring.")]
    [InlineData("The microphone monitor output was disconnected.")]
    [InlineData("The microphone stopped unexpectedly.")]
    [InlineData("The selected audio device is unavailable.")]
    [InlineData("Microphone monitoring stopped unexpectedly. Start the microphone test again.")]
    [InlineData("Microphone monitoring could not start. Start the microphone test again.")]
    [InlineData("Microphone data processing failed.")]
    [InlineData("AudioPilot is muted in the Windows Volume Mixer. Unmute it and try again.")]
    [InlineData("AudioPilot is set to 0% volume in the Windows Volume Mixer. Raise it and try again.")]
    [InlineData("Headphones was disconnected.")]
    [InlineData("Headphones is disconnected or unavailable.")]
    [InlineData("Headphones is not currently active.")]
    [InlineData("Headphones is muted. Unmute it and try again.")]
    [InlineData("Headphones is set to 0% volume. Raise it and try again.")]
    [InlineData("Headphones is unavailable for microphone monitoring.")]
    [InlineData("The microphone format (8-bit Pcm) is not supported for metering.")]
    public void AudioTestMessages_TranslateToSpanish(string english)
    {
        var localization = new LocalizationService();
        localization.SetLanguage("es");

        string translated = AppMessages.Translate(english, localization);

        Assert.NotEqual(english, translated);
        Assert.False(string.IsNullOrWhiteSpace(translated));
    }

    [Fact]
    public async Task AudioTestingViewModel_UnchecksHearMyselfOnMonitorFailureAndIgnoresStaleSequence()
    {
        await TestExecutionGuards.RunOnSharedStaAsync(async () =>
        {
            using var logger = TestLoggerScope.CreateInMemory("audio-testing-vm-hardening.log");
            var fakeService = new StubAudioEndpointTestService();
            var output = new CycleDevice { Id = "out-1", Name = "Headphones" };
            var inputRef = new AudioEndpointReference("in-1", "Mic");
            await using var vm = new AudioTestingViewModel(
                fakeService,
                Dispatcher.CurrentDispatcher,
                logger.Logger,
                () => ([output], output.Id),
                static (_, _) => true,
                CancellationToken.None);

            vm.RefreshAudioTestMonitorOutputOptions();
            vm.ApplyAudioEndpointTestState(new AudioEndpointTestState(
                Revision: 1,
                Kind: AudioEndpointTestKind.Input,
                Phase: AudioEndpointTestPhase.Starting,
                Endpoint: inputRef,
                Status: "Opening Mic…",
                Sequence: 1));
            Assert.False(vm.CanConfigureMonitoring);

            vm.ApplyAudioEndpointTestState(new AudioEndpointTestState(
                Revision: 1,
                Kind: AudioEndpointTestKind.Input,
                Phase: AudioEndpointTestPhase.Running,
                Endpoint: inputRef,
                Status: "Microphone test is active",
                Sequence: 2));
            Assert.True(vm.CanConfigureMonitoring);

            vm.HearMyself = true;
            Assert.True(vm.HearMyself);

            vm.ApplyAudioEndpointTestState(new AudioEndpointTestState(
                Revision: 1,
                Kind: AudioEndpointTestKind.Input,
                Phase: AudioEndpointTestPhase.Running,
                Endpoint: inputRef,
                Status: "Headphones is unavailable for microphone monitoring.",
                FailureKind: AudioEndpointTestFailureKind.Unavailable,
                MonitoringEnabled: false,
                Sequence: 3));
            Assert.False(vm.HearMyself);
            Assert.Equal("Headphones is unavailable for microphone monitoring.", vm.Status);

            // A stale out-of-order state with an older sequence must not overwrite newer UI state.
            vm.ApplyAudioEndpointTestState(new AudioEndpointTestState(
                Revision: 1,
                Kind: AudioEndpointTestKind.Input,
                Phase: AudioEndpointTestPhase.Running,
                Endpoint: inputRef,
                Status: "Microphone test is active",
                Sequence: 2));
            Assert.Equal("Headphones is unavailable for microphone monitoring.", vm.Status);
        });
    }

    [Fact]
    public async Task AudioTestingViewModel_DoesNotRaiseStatusChangedWhenStatusTextIsUnchanged()
    {
        await TestExecutionGuards.RunOnSharedStaAsync(async () =>
        {
            using var logger = TestLoggerScope.CreateInMemory("audio-testing-vm-status-dedupe.log");
            var fakeService = new StubAudioEndpointTestService();
            var inputRef = new AudioEndpointReference("in-1", "Mic");
            await using var vm = new AudioTestingViewModel(
                fakeService,
                Dispatcher.CurrentDispatcher,
                logger.Logger,
                static () => ([], null),
                static (_, _) => true,
                CancellationToken.None);

            int statusNotifications = 0;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AudioTestingViewModel.Status)) statusNotifications++;
            };

            vm.ApplyAudioEndpointTestState(new AudioEndpointTestState(
                Revision: 1,
                Kind: AudioEndpointTestKind.Input,
                Phase: AudioEndpointTestPhase.Running,
                Endpoint: inputRef,
                Status: "Microphone test and live monitoring are active",
                MonitoringEnabled: true,
                MonitorVolume: 0.5f,
                Sequence: 1));
            Assert.Equal(1, statusNotifications);

            vm.ApplyAudioEndpointTestState(new AudioEndpointTestState(
                Revision: 1,
                Kind: AudioEndpointTestKind.Input,
                Phase: AudioEndpointTestPhase.Running,
                Endpoint: inputRef,
                Status: "Microphone test and live monitoring are active",
                MonitoringEnabled: true,
                MonitorVolume: 0.65f,
                Sequence: 2));
            Assert.Equal(1, statusNotifications);
        });
    }

    private sealed class StubAudioEndpointTestService : IAudioEndpointTestService
    {
        public event Action<AudioEndpointTestState>? StateChanged
        {
            add { }
            remove { }
        }

        public AudioEndpointTestState CurrentState { get; private set; } = AudioEndpointTestState.Idle;
        public AudioInputLevelSnapshot ReadInputLevel() => AudioInputLevelSnapshot.Silence;
        public Task StartOutputTestAsync(AudioEndpointReference endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartInputTestAsync(AudioEndpointReference endpoint, AudioEndpointReference? initialMonitorEndpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConfigureInputMonitoringAsync(bool enabled, AudioEndpointReference? monitorEndpoint, float volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(AudioEndpointTestStopReason reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReconcileActiveEndpointsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

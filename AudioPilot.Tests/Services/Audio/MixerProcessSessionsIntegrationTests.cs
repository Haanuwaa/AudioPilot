using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Collection("AudioHardwareStressIsolation")]
public sealed class MixerProcessSessionsIntegrationTests
{
    private static readonly bool[] InitialMuteStates = [true, false];

    [AudioHardwareFact]
    public async Task PushToTalkMode_EnforcesIdleMuteAndRestoresOnDisableWithoutRecording()
    {
        using var logger = TestLoggerScope.CreateInMemory("push-to-talk-hardware.log");
        var originals = new Dictionary<string, (MMDevice Device, bool Muted)>();
        var feedback = new ConcurrentQueue<MicrophoneHoldFeedback>();
        var service = new MicrophoneHoldService(logger.Logger, feedback.Enqueue);
        try
        {
            await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                if (!EnsureDefaultEndpointAvailable(enumerator, DataFlow.Capture, Role.Multimedia)) return;
                foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                {
                    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                    if (!originals.TryAdd(device.ID, (device, device.AudioEndpointVolume.Mute))) device.Dispose();
                }
                foreach (var (Device, Muted) in originals.Values) Device.AudioEndpointVolume.Mute = false;
            });
            if (originals.Count == 0) return;
            await WaitForMuteAsync(false);
            service.SetPushToTalkEnabled(true);
            await WaitForMuteAsync(true);
            await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                foreach (var (Device, Muted) in originals.Values) Device.AudioEndpointVolume.Mute = false;
            });
            await WaitForMuteAsync(true);
            int held = 1;
            service.Begin(false, () => Volatile.Read(ref held) != 0);
            await WaitForMuteAsync(false);
            Volatile.Write(ref held, 0);
            await WaitForMuteAsync(true);
            service.SetPushToTalkEnabled(false);
            await WaitForMuteAsync(false);
            await TestExecutionGuards.WaitUntilAsync(() => feedback.Any(item => item.Title == "Push-to-talk off"),
                "Push-to-talk did not report its restored state.", TimeSpan.FromSeconds(3));
            Assert.False(Assert.Single(feedback, item => item.Title == "Push-to-talk off").Muted);
        }
        finally
        {
            try { await service.DisposeAsync(); }
            finally
            {
                await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                {
                    foreach (var (Device, Muted) in originals.Values)
                    {
                        try { Device.AudioEndpointVolume.Mute = Muted; }
                        finally { Device.Dispose(); }
                    }
                });
                TestContext.Current.TestOutputHelper!.WriteLine(logger.DisposeAndReadLogText());
            }
        }

        Task WaitForMuteAsync(bool muted) => TestExecutionGuards.WaitUntilAsync(
            () => ComThreadingHelper.RunOnCoreAudioThread(() => originals.Values.All(entry => entry.Device.AudioEndpointVolume.Mute == muted)),
            $"Default microphones did not become muted={muted}.", TimeSpan.FromSeconds(3));
    }

    [AudioHardwareFact]
    public async Task MicrophoneHold_RestoresEachDefaultEndpointWithoutRecording()
    {
        using var logger = TestLoggerScope.CreateInMemory("microphone-hold-hardware.log");
        try
        {
            await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                if (!EnsureDefaultEndpointAvailable(enumerator, DataFlow.Capture, Role.Multimedia)) return;
                var originals = new Dictionary<string, (MMDevice Device, bool Muted)>();
                try
                {
                    foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                    {
                        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                        if (!originals.TryAdd(device.ID, (device, device.AudioEndpointVolume.Mute))) device.Dispose();
                    }
                    foreach (bool initial in InitialMuteStates)
                    {
                        foreach (var (Device, Muted) in originals.Values) Device.AudioEndpointVolume.Mute = initial;
                        Assert.True(SpinWait.SpinUntil(() => originals.Values.All(entry => entry.Device.AudioEndpointVolume.Mute == initial), TimeSpan.FromSeconds(2)));
                        using var lease = EndpointMicrophoneMuteLease.Capture(logger.Logger);
                        try
                        {
                            Assert.True(lease.Apply(!initial));
                            Assert.True(SpinWait.SpinUntil(() => originals.Values.All(entry => entry.Device.AudioEndpointVolume.Mute == !initial), TimeSpan.FromSeconds(2)));
                        }
                        finally { Assert.True(lease.Restore()); }
                        Assert.True(SpinWait.SpinUntil(() => originals.Values.All(entry => entry.Device.AudioEndpointVolume.Mute == initial), TimeSpan.FromSeconds(2)));
                    }
                }
                finally
                {
                    foreach (var (Device, Muted) in originals.Values)
                    {
                        try { Device.AudioEndpointVolume.Mute = Muted; }
                        finally { Device.Dispose(); }
                    }
                }
            });
        }
        finally { TestContext.Current.TestOutputHelper!.WriteLine(logger.DisposeAndReadLogText()); }
    }

    [AudioHardwareFact]
    public async Task ForegroundControls_AdjustEveryMatchingPlaybackSessionAndPreserveMute()
    {
        await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!EnsureDefaultEndpointAvailable(enumerator, DataFlow.Render, Role.Multimedia)) return;
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using AudioClient first = device.CreateAudioClient();
            using AudioClient second = device.CreateAudioClient();
            first.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1_000_000, 0, first.MixFormat, Guid.NewGuid());
            second.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1_000_000, 0, second.MixFormat, Guid.NewGuid());
            first.SimpleAudioVolume.Volume = 0.3f;
            first.SimpleAudioVolume.Mute = false;
            second.SimpleAudioVolume.Volume = 0.8f;
            second.SimpleAudioVolume.Mute = true;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using (var manager = device.AudioSessionManager)
            using (var sessions = manager.Sessions)
            {
                for (int index = 0; index < sessions.Count; index++)
                {
                    using var session = sessions[index];
                    if (session.GetProcessID == process.Id) session.DisplayName = "AudioPilot foreground test";
                }
            }
            var target = new ForegroundProcessTarget(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName);
            var service = new ForegroundAudioControlService(AudioPilot.Logging.Logger.Instance);
            var adjusted = service.Apply(target, 5);
            Assert.True(adjusted.Changed >= 2);
            Assert.Equal("AudioPilot foreground test", adjusted.Name);
            Assert.Equal(0.35f, first.SimpleAudioVolume.Volume, 3);
            Assert.Equal(0.85f, second.SimpleAudioVolume.Volume, 3);
            Assert.False(first.SimpleAudioVolume.Mute);
            Assert.True(second.SimpleAudioVolume.Mute);
            Assert.True(service.Apply(target, null).Muted);
            Assert.True(first.SimpleAudioVolume.Mute);
            Assert.False(service.Apply(target, null).Muted);
            Assert.False(second.SimpleAudioVolume.Mute);
        });
    }

    [AudioHardwareFact]
    public async Task NewSessions_RestorePlaybackButPreserveCurrentRecordingLevel()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!EnsureDefaultEndpointAvailable(enumerator, DataFlow.Render, Role.Console) ||
            !EnsureDefaultEndpointAvailable(enumerator, DataFlow.Capture, Role.Console))
        {
            return;
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        foreach ((AudioMixerMode mode, float inputAdjustment) in new[]
        {
            (AudioMixerMode.Input, 7f),
            (AudioMixerMode.Input, 100f),
            (AudioMixerMode.Output, 100f),
        })
        {
            var initializationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter(),
                waitForSessionInitializationAsync: token =>
                {
                    initializationEntered.TrySetResult();
                    return allowInitialization.Task.WaitAsync(token);
                });
            audio.UpdateSessionVolumeCache(process.ProcessName, process.ProcessName, 23f);
            audio.UpdateSessionVolumeCache(process.ProcessName, process.ProcessName, inputAdjustment, AudioMixerMode.Input);

            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            audio.AudioSessionCreated += createdMode =>
            {
                if (createdMode == mode) observed.TrySetResult();
            };
            using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(mode == AudioMixerMode.Input ? DataFlow.Capture : DataFlow.Render, Role.Console);
            float originalEndpointLevel = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
            audio.AcquireSessionMonitoring(mode);
            try
            {
                int expectedEndpoints = mode == AudioMixerMode.Input ? audio.GetActiveCaptureCycleEntries().Count : audio.GetActivePlaybackCycleEntries().Count;
                Assert.True(expectedEndpoints > 0);
                await TestExecutionGuards.WaitUntilAsync(
                    () => audio.GetSessionMonitoringEndpointCountForTests(mode) == expectedEndpoints,
                    "Session monitoring did not attach.", TimeSpan.FromSeconds(5));
                using AudioClient client = endpoint.CreateAudioClient();
                await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                {
                    client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                        1_000_000, 0, client.MixFormat, Guid.NewGuid());
                    if (mode == AudioMixerMode.Input) client.SimpleAudioVolume.Volume = 0.79f;
                });
                await initializationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                if (mode == AudioMixerMode.Input)
                {
                    await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = 0.53f);
                }
                float currentSessionLevel = client.SimpleAudioVolume.Volume;
                float currentEndpointLevel = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
                allowInitialization.TrySetResult();
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(mode == AudioMixerMode.Input ? currentSessionLevel : 0.23f, client.SimpleAudioVolume.Volume, 3);
                Assert.Equal(currentEndpointLevel, endpoint.AudioEndpointVolume.MasterVolumeLevelScalar, 3);
            }
            finally
            {
                allowInitialization.TrySetResult();
                try
                {
                    audio.ReleaseSessionMonitoring(mode);
                    await audio.DisposeAsync();
                }
                finally
                {
                    if (mode == AudioMixerMode.Input)
                    {
                        await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = originalEndpointLevel);
                    }
                }
            }
        }
    }

    [AudioHardwareFact]
    public async Task NewPlaybackSession_NewerMixerOrExternalChoiceWinsDuringInitialization()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!EnsureDefaultEndpointAvailable(enumerator, DataFlow.Render, Role.Console)) return;
        using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        foreach (bool externalChange in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var volumeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter(),
                waitForSessionInitializationAsync: token =>
                {
                    entered.TrySetResult();
                    return release.Task.WaitAsync(token);
                });
            var volumeService = TestPrivateAccess.GetField<VolumeControlService>(audio, "_volumeService");
            volumeService.RegisterPostSwitchSnapshot(new SessionVolumeSnapshot
            {
                ByProcess = new(StringComparer.OrdinalIgnoreCase) { [process.ProcessName] = 23f },
            }, endpoint.ID);
            audio.AudioSessionCreated += _ => notified.TrySetResult();
            audio.AudioSessionLifecycleChanged += signal =>
            {
                if (signal.ProcessId == (uint)process.Id && signal.VolumePercent is float value && Math.Abs(value - 61f) < 0.1f)
                    volumeObserved.TrySetResult();
            };
            audio.AcquireSessionMonitoring(AudioMixerMode.Output);
            try
            {
                await TestExecutionGuards.WaitUntilAsync(() => audio.GetSessionMonitoringEndpointCountForTests(AudioMixerMode.Output) > 0,
                    "Playback monitoring did not attach.", TimeSpan.FromSeconds(5));
                using AudioClient client = endpoint.CreateAudioClient();
                await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.None, 1_000_000, 0, client.MixFormat, Guid.NewGuid()));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                if (externalChange)
                {
                    client.SimpleAudioVolume.Volume = 0.61f;
                    await volumeObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                }
                else audio.UpdateSessionVolumeCache("Test player", process.ProcessName, 61f);
                release.TrySetResult();
                await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(0.61f, client.SimpleAudioVolume.Volume, 3);
            }
            finally
            {
                release.TrySetResult();
                audio.ReleaseSessionMonitoring(AudioMixerMode.Output);
                await audio.DisposeAsync();
            }
        }
    }

    [AudioHardwareFact]
    public void DelayedPlaybackRestore_UsesActualSessionEndpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!EnsureDefaultEndpointAvailable(enumerator, DataFlow.Render, Role.Console)) return;
        using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        using AudioClient client = endpoint.CreateAudioClient();
        Guid sessionId = Guid.NewGuid();
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1_000_000, 0, client.MixFormat, sessionId);
        client.SimpleAudioVolume.Volume = 0.79f;
        using var manager = endpoint.AudioSessionManager;
        using var sessions = manager.Sessions;
        using var audio = new AudioDeviceService();
        using var service = new VolumeControlService(audio, _ => null, _ => true);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var snapshot = new SessionVolumeSnapshot { ByProcess = new(StringComparer.OrdinalIgnoreCase) { [process.ProcessName] = 23f } };
        for (int index = 0; index < sessions.Count; index++)
        {
            using var session = sessions[index];
            if (!session.GetSessionIdentifier.Contains(sessionId.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            service.RegisterPostSwitchSnapshot(snapshot, "another-output");
            service.ApplySavedVolume(session, process.ProcessName, "Test player", AudioMixerMode.Output, endpoint.ID);
            Assert.Equal(0.79f, client.SimpleAudioVolume.Volume, 3);
            service.RegisterPostSwitchSnapshot(snapshot, endpoint.ID);
            service.ApplySavedVolume(session, process.ProcessName, "Test player", AudioMixerMode.Output, endpoint.ID);
            Assert.Equal(0.23f, client.SimpleAudioVolume.Volume, 3);
            return;
        }
        Assert.Fail("The isolated playback session was not enumerated.");
    }

    [AudioHardwareFact]
    public async Task ProcessMutations_KeepPlaybackAndCaptureIndependent()
    {
        using var logger = TestLoggerScope.CreateInMemory("mixer-direction.log");
        await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!EnsureDefaultEndpointAvailable(enumerator, DataFlow.Render, Role.Multimedia) ||
                !EnsureDefaultEndpointAvailable(enumerator, DataFlow.Capture, Role.Console))
            {
                return;
            }
            using MMDevice output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using MMDevice input = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            float originalVolume = input.AudioEndpointVolume.MasterVolumeLevelScalar;
            bool originalMute = input.AudioEndpointVolume.Mute;
            try
            {
                using AudioClient playback = output.CreateAudioClient();
                using AudioClient capture = input.CreateAudioClient();
                playback.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                    1_000_000, 0, playback.MixFormat, Guid.NewGuid());
                WaveFormat captureFormat = WaveFormat.CreateIeeeFloatWaveFormat(capture.MixFormat.SampleRate, capture.MixFormat.Channels);
                capture.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    1_000_000, 0, captureFormat, Guid.Empty);
                playback.SimpleAudioVolume.Volume = 0.7f;
                playback.SimpleAudioVolume.Mute = false;
                capture.SimpleAudioVolume.Volume = 0.8f;
                capture.SimpleAudioVolume.Mute = false;
                using var audio = new AudioDeviceService();
                var names = new ConcurrentDictionary<uint, string>();
                uint pid = (uint)Environment.ProcessId;

                Assert.True(AudioDeviceHelper.SetVolumeForSessionsByPid(audio, pid, 0.2f, names, logger.Logger, DataFlow.Render).IsCompleteSuccess);
                Assert.True(AudioDeviceHelper.SetMuteForSessionsByPid(audio, pid, true, names, logger.Logger, DataFlow.Render).IsCompleteSuccess);
                Assert.Equal(0.2f, playback.SimpleAudioVolume.Volume, 3);
                Assert.True(playback.SimpleAudioVolume.Mute);
                Assert.Equal(0.8f, capture.SimpleAudioVolume.Volume, 3);
                Assert.False(capture.SimpleAudioVolume.Mute);

                Assert.True(AudioDeviceHelper.SetVolumeForSessionsByPid(audio, pid, 0.3f, names, logger.Logger, DataFlow.Capture).IsCompleteSuccess);
                Assert.True(AudioDeviceHelper.SetMuteForSessionsByPid(audio, pid, true, names, logger.Logger, DataFlow.Capture).IsCompleteSuccess);
                Assert.Equal(0.3f, capture.SimpleAudioVolume.Volume, 3);
                Assert.True(capture.SimpleAudioVolume.Mute);
                Assert.Equal(0.2f, playback.SimpleAudioVolume.Volume, 3);
                Assert.True(playback.SimpleAudioVolume.Mute);
            }
            finally
            {
                input.AudioEndpointVolume.MasterVolumeLevelScalar = originalVolume;
                input.AudioEndpointVolume.Mute = originalMute;
            }
        });
    }

    [AudioHardwareFact]
    public async Task ProcessRow_ControlsSessionsCreatedAfterItsFirstSnapshot()
    {
        using var logger = TestLoggerScope.CreateInMemory("mixer-process-sessions.log");
        await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!EnsureDefaultEndpointAvailable(enumerator, DataFlow.Render, Role.Multimedia))
            {
                return;
            }
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using AudioClient originalClient = device.CreateAudioClient();
            Guid originalGuid = Guid.NewGuid();
            originalClient.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                1_000_000, 0, originalClient.MixFormat, originalGuid);
            using AudioSessionManager manager = device.AudioSessionManager;
            using SessionCollection sessions = manager.Sessions;
            using AudioSessionControl originalSession = Enumerable.Range(0, sessions.Count)
                .Select(index => sessions[index])
                .First(session =>
                {
                    if (session.GetSessionIdentifier.Contains(originalGuid.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    session.Dispose();
                    return false;
                });
            originalSession.SimpleAudioVolume.Volume = 0.37f;
            originalSession.SimpleAudioVolume.Mute = false;

            using AudioClient first = device.CreateAudioClient();
            using AudioClient second = device.CreateAudioClient();
            first.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                1_000_000, 0, first.MixFormat, Guid.NewGuid());
            second.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                1_000_000, 0, second.MixFormat, Guid.NewGuid());
            first.SimpleAudioVolume.Volume = 0.8f;
            second.SimpleAudioVolume.Volume = 0.9f;

            using var audio = new AudioDeviceService();
            DeviceCacheHelper.Initialize(audio);
            try
            {
                var mixer = new MixerViewModel(audio, Dispatcher.CurrentDispatcher, logger: logger.Logger);
                try
                {
                    var item = new AudioSessionItem("Test process", 23f, false, false,
                        processId: (uint)Environment.ProcessId,
                        sessionInstanceId: originalSession.GetSessionInstanceIdentifier,
                        endpointId: device.ID);
                    MethodInfo setVolume = typeof(MixerViewModel).GetMethod("ApplyVolumeChange", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    MethodInfo setMute = typeof(MixerViewModel).GetMethod("ApplyMuteChange", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    item.SetVolumeFromSystem(89f);
                    var volumeResult = (MixerViewModel.MixerMutationResult)setVolume.Invoke(mixer, [item, 23f, item.EndpointId])!;
                    Assert.True(volumeResult.IsSuccess);
                    Assert.True(volumeResult.Succeeded >= 3);
                    Assert.Equal(0.23f, first.SimpleAudioVolume.Volume, 3);
                    Assert.Equal(0.23f, second.SimpleAudioVolume.Volume, 3);

                    item.UpdateRoutingMetadataFromSystem(null, "departed-session", "departed-endpoint");
                    ReadOnlySpan<bool> muteStates = [true, false];
                    foreach (bool muted in muteStates)
                    {
                        var muteResult = (MixerViewModel.MixerMutationResult)setMute.Invoke(mixer, [item, muted, item.EndpointId, null])!;
                        Assert.True(muteResult.IsSuccess);
                        Assert.True(muteResult.Succeeded >= 3);
                        Assert.Equal(muted, first.SimpleAudioVolume.Mute);
                        Assert.Equal(muted, second.SimpleAudioVolume.Mute);
                    }
                    Assert.Equal(0.23f, originalSession.SimpleAudioVolume.Volume, 3);
                    Assert.False(originalSession.SimpleAudioVolume.Mute);
                }
                finally
                {
                    mixer.Cleanup();
                }
            }
            finally
            {
                DeviceCacheHelper.DisposeSingleton();
            }
        });
        string log = logger.DisposeAndReadLogText();
        Assert.Contains("mixer-process-volume-write", log);
        Assert.Contains("mixer-process-mute-write", log);
        Assert.Contains("mixer-process-volume-applied", log);
        Assert.DoesNotContain("apply-incomplete", log);
    }

    private static bool EnsureDefaultEndpointAvailable(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(flow, role);
            return true;
        }
        catch (CoreAudioException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            string message = $"A default {flow} endpoint for role {role} is required to create a mixer test session.";
            if (TestExecutionGuards.ShouldRequireIntegrationHardware())
            {
                throw TestExecutionGuards.CreateRequiredIntegrationPrerequisiteException(
                    nameof(MixerProcessSessionsIntegrationTests), message, ex);
            }

            return TestExecutionGuards.ReportOptionalIntegrationPrerequisite(
                nameof(MixerProcessSessionsIntegrationTests), message);
        }
    }
}

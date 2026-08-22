using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Collection("AudioHardwareStressIsolation")]
public sealed class MixerProcessSessionsIntegrationTests
{
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
                    var volumeResult = (MixerViewModel.MixerMutationResult)setVolume.Invoke(mixer, [item, 23f])!;
                    Assert.True(volumeResult.IsSuccess);
                    Assert.True(volumeResult.Succeeded >= 3);
                    Assert.Equal(0.23f, first.SimpleAudioVolume.Volume, 3);
                    Assert.Equal(0.23f, second.SimpleAudioVolume.Volume, 3);

                    item.UpdateRoutingMetadataFromSystem(null, "departed-session", "departed-endpoint");
                    ReadOnlySpan<bool> muteStates = [true, false];
                    foreach (bool muted in muteStates)
                    {
                        var muteResult = (MixerViewModel.MixerMutationResult)setMute.Invoke(mixer, [item, muted])!;
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

using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Helpers;

/// <summary>Owns a constructed mixer and isolated audio dependencies unless the caller supplies audio.</summary>
internal sealed class MixerTestFixture : IDisposable
{
    private readonly DeviceCacheHelper _cache;
    private readonly bool _ownsAudio;
    private readonly ReaderWriterLockSlim? _queryLock;

    internal AudioDeviceService Audio { get; }
    internal MixerViewModel Mixer { get; }

    internal MixerTestFixture(AudioMixerMode mixerMode = AudioMixerMode.Output,
        Dispatcher? dispatcher = null, AudioDeviceService? audio = null)
    {
        DeviceCacheHelper? cache = null;
        _ownsAudio = audio == null;
        Audio = audio ?? new AudioDeviceService(new FakeInputListenPropertyWriter(),
            audioSessionServiceFactory: _ => new AudioSessionService(new UnavailableAudioDeviceEnumerator(), () => cache),
            deviceCacheAccessor: () => cache);
        if (_ownsAudio)
        {
            _queryLock = new ReaderWriterLockSlim();
            TestPrivateAccess.SetField(Audio, "_endpointQueryHelper", new AudioDeviceEndpointQueryHelper(
                null!, _queryLock, Logger.Instance, () => true,
                () => [Role.Multimedia], () => [Role.Multimedia], (_, _) => { }));
        }
        _cache = cache = new DeviceCacheHelper(Audio, new DeviceCacheHelperRuntime
        {
            CaptureSnapshotsAsync = () => Task.FromResult(new DeviceCacheRefreshCapture([], [])),
            MaterializeDeviceById = _ => null,
            GetFallbackPlaybackDevice = (_, _) => null,
            GetFallbackRecordingDevice = (_, _) => null,
            ProbeMuteState = (_, _, _) => null,
            RunRefreshWorkAsync = work => work(),
        });
        Mixer = new MixerViewModel(Audio, dispatcher ?? Dispatcher.CurrentDispatcher, mixerMode, Logger.Instance, _cache);
    }

    public void Dispose()
    {
        try { Mixer.Cleanup(); }
        finally
        {
            try { if (_ownsAudio) Audio.Dispose(); }
            finally
            {
                try { _cache.Dispose(); }
                finally { _queryLock?.Dispose(); }
            }
        }
    }

    private sealed class UnavailableAudioDeviceEnumerator : IAudioDeviceEnumerator
    {
        public MMDeviceCollection GetActivePlaybackDevices() => throw new InvalidOperationException("The mixer fixture has no playback endpoints.");
        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds) => [];
        public MMDevice GetDefaultPlaybackDevice() => throw new InvalidOperationException("The mixer fixture has no default playback endpoint.");
        public MMDevice? GetDefaultRecordingDevice() => null;
        public List<MMDevice?> GetAllDefaultPlaybackDevices() => [];
        public List<MMDevice?> GetAllDefaultRecordingDevices() => [];
    }
}

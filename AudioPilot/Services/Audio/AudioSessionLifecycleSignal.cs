using AudioPilot.Models;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Audio
{
    internal enum AudioSessionLifecycleSignalKind
    {
        VolumeChanged,
        EndpointVolumeChanged,
        StateChanged,
        Disconnected,
    }

    internal readonly record struct AudioSessionLifecycleSignal(
        AudioMixerMode MixerMode,
        AudioSessionLifecycleSignalKind Kind,
        string SessionInstanceId,
        string EndpointId = "",
        bool AffectsSharedRows = false,
        AudioSessionState? State = null,
        AudioSessionDisconnectReason? DisconnectReason = null,
        float? VolumePercent = null,
        bool? IsMuted = null);
}

using System.Collections.Immutable;
using AudioPilot.Models;

namespace AudioPilot.Services.Audio.Metering;

[Flags]
internal enum AudioMeterModes { None = 0, Output = 1, Input = 2, Both = Output | Input }

internal enum AudioMeterStatus { Available, Unavailable, Unsupported }

// Native objects never cross this boundary. Session identity is scoped to its endpoint,
// rather than PID alone (one process can own several streams, or reuse a PID).
internal sealed record AudioMeterSource(AudioMixerMode Mode, string EndpointId, string? SessionInstanceId = null, uint? ProcessId = null)
{
    internal bool IsEndpoint => SessionInstanceId == null;
}

internal readonly record struct AudioMeterReading(AudioMeterSource Source, AudioMeterStatus Status, float? Peak, int? ErrorCode = null,
    float? VolumeScalar = null, bool? IsMuted = null)
{
    internal static AudioMeterReading FromPeak(AudioMeterSource source, float peak, float? volumeScalar = null, bool? isMuted = null) =>
        float.IsFinite(peak) && (volumeScalar == null || float.IsFinite(volumeScalar.Value)) && volumeScalar.HasValue == isMuted.HasValue
        ? new(source, AudioMeterStatus.Available, Math.Clamp(peak, 0, 1), VolumeScalar: volumeScalar.HasValue ? Math.Clamp(volumeScalar.Value, 0, 1) : null, IsMuted: isMuted)
        : new(source, AudioMeterStatus.Unavailable, null);
}

internal sealed record AudioMeterBatch(ImmutableArray<AudioMeterReading> Readings, AudioMeterModes CompleteModes,
    bool Truncated = false, bool NeedsRediscovery = false);

internal sealed record AudioMeterSnapshot(long DemandVersion, long SampleStarted, AudioMeterBatch Batch, TimeSpan ReadDuration, long AllocatedBytes, bool IncludesDiscovery, int? ErrorCode = null);

internal interface IAudioMeterBackend : IDisposable
{
    AudioMeterBatch Read(AudioMeterModes modes, bool refresh, CancellationToken cancellationToken);
}

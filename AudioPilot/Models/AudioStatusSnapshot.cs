namespace AudioPilot.Models
{
    public enum AudioEndpointStatusKind
    {
        Available,
        NoDevice,
        Unavailable,
    }

    public sealed record AudioEndpointStatus(
        AudioEndpointStatusKind Kind,
        string? DeviceName = null,
        float? VolumePercent = null,
        bool? Muted = null);

    public sealed record AudioStatusSnapshot(AudioEndpointStatus Output, AudioEndpointStatus Input);
}

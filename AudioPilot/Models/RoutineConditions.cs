using System.Text.Json.Serialization;
using AudioPilot.Helpers;

namespace AudioPilot.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DeviceAvailabilityTransition>))]
public enum DeviceAvailabilityTransition { Connected, Disconnected, Both }

/// <summary>Identifies one audio endpoint by its current or stable identity, with a name for display.</summary>
public sealed record RoutineDeviceReference
{
    public string Id { get; init; } = string.Empty;
    public string? StableId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Playback { get; init; } = true;
    [JsonIgnore] public string DisplayName => $"{(Playback ? "Output" : "Input")}: {Name}";
    [JsonIgnore] internal bool IsValid => !string.IsNullOrWhiteSpace(Id) || !string.IsNullOrEmpty(StableId);
    [JsonIgnore] internal string IdentityKey => $"{Playback}\u001f{Id}\u001f{StableId}";

    internal bool? GetAvailability(IReadOnlyList<CycleDevice> devices)
    {
        var (device, ambiguous) = Resolve(devices);
        return device != null ? true : ambiguous ? null : false;
    }

    internal (CycleDevice? Device, bool Ambiguous) Resolve(IReadOnlyList<CycleDevice> devices)
    {
        if (!string.IsNullOrWhiteSpace(Id) && devices.FirstOrDefault(device => string.Equals(device.Id, Id, StringComparison.OrdinalIgnoreCase)) is { } exact) return (exact, false);
        if (string.IsNullOrEmpty(StableId)) return (null, false);
        var matches = devices.Where(device => string.Equals(device.StableId, StableId, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? (matches[0], false) : (null, matches.Length > 1);
    }

}

/// <summary>Optional requirements checked together before executing a routine's actions.</summary>
public sealed record RoutineConditions
{
    public RoutineTimeWindow? TimeWindow { get; init; }
    public RoutineDeviceReference? Device { get; init; }
    public bool DeviceAvailable { get; init; } = true;
    public bool DeviceMustBeDefault { get; init; }
    public string RunningAppPath { get; init; } = string.Empty;
    public bool ApplicationMustBeRunning { get; init; } = true;
    public string ConnectedNetwork { get; init; } = string.Empty;
    public bool NetworkMustBeConnected { get; init; } = true;
    [JsonIgnore] public bool HasRequirements => TimeWindow != null || Device != null || !string.IsNullOrWhiteSpace(RunningAppPath) || !string.IsNullOrWhiteSpace(ConnectedNetwork);
    [JsonIgnore] internal string ConfigurationKey => $"{Device?.IdentityKey}\u001f{DeviceAvailable}\u001f{DeviceMustBeDefault}\u001f{RunningAppPath}\u001f{ApplicationMustBeRunning}\u001f{ConnectedNetwork}\u001f{NetworkMustBeConnected}\u001f{TimeWindow?.ConfigurationKey}";
    [JsonIgnore]
    public string Summary => BuildSummary();

    internal string BuildSummary(string? runningAppDisplayName = null) => string.Join("; ", new[]
    {
        TimeWindow?.Summary,
        Device == null ? null : $"{(DeviceMustBeDefault ? "Current default" : DeviceAvailable ? "Available" : "Unavailable")} {Device.DisplayName}",
        string.IsNullOrWhiteSpace(RunningAppPath) ? null : $"{(ApplicationMustBeRunning ? "Running" : "Not running")}: {(string.IsNullOrWhiteSpace(runningAppDisplayName) ? RoutineTriggerPathHelper.GetTriggerDisplayName(RunningAppPath) : runningAppDisplayName)}",
        string.IsNullOrWhiteSpace(ConnectedNetwork) ? null : $"{(NetworkMustBeConnected ? "Connected" : "Not connected")} network: {ConnectedNetwork}",
    }.OfType<string>());

    internal string? Validate() => Device is { IsValid: false } ? "Choose the device required by this routine."
        : !string.IsNullOrWhiteSpace(RunningAppPath) && !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(RunningAppPath)
            ? "Required application must be a full .exe path or packaged app AUMID." : TimeWindow?.Validate();
}

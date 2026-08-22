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
    [JsonIgnore] internal bool IsValid => !string.IsNullOrWhiteSpace(Id) || !string.IsNullOrWhiteSpace(StableId);
    [JsonIgnore] internal string IdentityKey => $"{Playback}\u001f{Id}\u001f{StableId}";

    internal bool? GetAvailability(IReadOnlyList<CycleDevice> devices)
    {
        if (!string.IsNullOrWhiteSpace(Id) && devices.Any(device => string.Equals(device.Id, Id, StringComparison.OrdinalIgnoreCase))) return true;
        if (string.IsNullOrWhiteSpace(StableId)) return false;
        return devices.Count(device => string.Equals(device.StableId, StableId, StringComparison.OrdinalIgnoreCase)) switch
        {
            0 => false,
            1 => true,
            _ => null,
        };
    }

}

/// <summary>Optional requirements checked together before executing a routine's actions.</summary>
public sealed record RoutineConditions
{
    public RoutineTimeWindow? TimeWindow { get; init; }
    public RoutineDeviceReference? Device { get; init; }
    public bool DeviceAvailable { get; init; } = true;
    public string RunningAppPath { get; init; } = string.Empty;
    public string ConnectedNetwork { get; init; } = string.Empty;
    [JsonIgnore] public bool HasRequirements => TimeWindow != null || Device != null || !string.IsNullOrWhiteSpace(RunningAppPath) || !string.IsNullOrWhiteSpace(ConnectedNetwork);
    [JsonIgnore] internal string ConfigurationKey => $"{Device?.IdentityKey}\u001f{DeviceAvailable}\u001f{RunningAppPath}\u001f{ConnectedNetwork}\u001f{TimeWindow?.ConfigurationKey}";
    [JsonIgnore]
    public string Summary => string.Join("; ", new[]
    {
        TimeWindow?.Summary,
        Device == null ? null : $"{(DeviceAvailable ? "Available" : "Unavailable")} {Device.DisplayName}",
        string.IsNullOrWhiteSpace(RunningAppPath) ? null : $"Running: {RoutineTriggerPathHelper.GetTriggerDisplayName(RunningAppPath)}",
        string.IsNullOrWhiteSpace(ConnectedNetwork) ? null : $"Connected network: {ConnectedNetwork}",
    }.OfType<string>());

    internal string? Validate() => Device is { IsValid: false } ? "Choose the device required by this routine."
        : !string.IsNullOrWhiteSpace(RunningAppPath) && !RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(RunningAppPath)
            ? "Required application must be a full .exe path or packaged app AUMID." : TimeWindow?.Validate();
}

namespace AudioPilot.Services.Audio.Testing;

internal sealed class AudioEndpointTestPreferences
{
    private string _preferredMonitorOutputId = string.Empty;
    private bool _monitorOutputInitialized;

    public bool HearMyself { get; set; }

    public double MonitorVolumePercent { get; set; } = 50d;

    public string GetPreferredMonitorOutputId(string? configuredMonitorOutputId)
    {
        if (!_monitorOutputInitialized)
        {
            _preferredMonitorOutputId = configuredMonitorOutputId ?? string.Empty;
            _monitorOutputInitialized = true;
        }

        return _preferredMonitorOutputId;
    }

    public void RememberMonitorOutput(string? endpointId)
    {
        _preferredMonitorOutputId = endpointId ?? string.Empty;
        _monitorOutputInitialized = true;
    }

    public string ResolveAvailableMonitorOutputId(
        IEnumerable<string> availableEndpointIds,
        string? configuredMonitorOutputId,
        out bool usedDefaultFallback)
    {
        ArgumentNullException.ThrowIfNull(availableEndpointIds);

        string preferredEndpointId = GetPreferredMonitorOutputId(configuredMonitorOutputId);
        usedDefaultFallback = !string.IsNullOrWhiteSpace(preferredEndpointId) &&
            !availableEndpointIds.Any(endpointId =>
                string.Equals(endpointId, preferredEndpointId, StringComparison.OrdinalIgnoreCase));

        return usedDefaultFallback ? string.Empty : preferredEndpointId;
    }
}

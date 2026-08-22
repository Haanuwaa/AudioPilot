namespace AudioPilot.Models
{
    internal readonly record struct AudioSessionSnapshot(
        string DisplayName,
        float Volume,
        string DeviceName,
        string? ProcessName,
        string? MainWindowTitle,
        uint? ProcessId,
        bool IsMuted = false,
        string SessionInstanceId = "",
        string EndpointId = "");
}

namespace AudioPilot.Models
{
    internal enum AudioSessionDeviceUse { None, One, Multiple }

    internal readonly record struct AudioSessionSnapshot(
        string DisplayName,
        float Volume,
        string DeviceName,
        string? ProcessName,
        string? MainWindowTitle,
        uint? ProcessId,
        bool IsMuted = false,
        string SessionInstanceId = "",
        string EndpointId = "",
        int SessionCount = 1,
        NAudio.CoreAudioApi.Role? EndpointRole = null,
        AudioSessionDeviceUse ActiveDeviceUse = AudioSessionDeviceUse.None);
}

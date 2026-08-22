using NAudio.CoreAudioApi;

namespace AudioPilot.Models
{
    public interface IAudioDeviceEnumerator
    {
        MMDeviceCollection GetActivePlaybackDevices();
        IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds);
        MMDevice GetDefaultPlaybackDevice();
        MMDevice? GetDefaultRecordingDevice();
        List<MMDevice?> GetAllDefaultPlaybackDevices();
        List<MMDevice?> GetAllDefaultRecordingDevices();
    }
}

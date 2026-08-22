namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceSessionVolumeRestoreHelper(AudioDeviceSessionProcessResolver sessionProcessResolver)
    {
        private readonly AudioDeviceSessionProcessResolver _sessionProcessResolver = sessionProcessResolver;

        public bool TryApplySavedVolume(uint pid, string displayName, Action<string, string> applySavedVolume)
        {
            if (!_sessionProcessResolver.TryResolveProcessName(pid, out string processName))
            {
                return false;
            }

            applySavedVolume(processName, displayName);
            return true;
        }
    }
}

using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Audio
{
    internal static class AudioDeviceSessionControlRestoreHelper
    {
        public static bool TryRestoreSession(
            AudioSessionState state,
            uint pid,
            string displayName,
            Func<uint, string, bool> tryApplySavedVolume)
        {
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                return false;
            }

            if (pid == 0)
            {
                return false;
            }

            return tryApplySavedVolume(pid, displayName);
        }
    }
}

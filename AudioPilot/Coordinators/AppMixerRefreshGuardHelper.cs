namespace AudioPilot.Coordinators
{
    internal static class AppMixerRefreshGuardHelper
    {
        public static bool CanRefreshMixer(bool isWindowVisible, bool isCleaningUp)
        {
            return isWindowVisible && !isCleaningUp;
        }
    }
}

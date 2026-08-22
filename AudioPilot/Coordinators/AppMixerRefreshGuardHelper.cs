namespace AudioPilot.Coordinators
{
    internal static class AppMixerRefreshGuardHelper
    {
        private static bool CanRefreshMixer(bool isWindowVisible, bool isCleaningUp)
        {
            return isWindowVisible && !isCleaningUp;
        }

        public static bool ShouldRefreshForNewSession(bool isWindowVisible, bool isCleaningUp)
        {
            return CanRefreshMixer(isWindowVisible, isCleaningUp);
        }

        public static bool ShouldRefreshForHotplug(bool isWindowVisible, bool isCleaningUp)
        {
            return CanRefreshMixer(isWindowVisible, isCleaningUp);
        }
    }
}

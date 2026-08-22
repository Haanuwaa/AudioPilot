using System.Windows.Input;

namespace AudioPilot.Services.Hotkeys
{
    internal static class InputLanguageManagerHelper
    {
        /// <summary>Returns no manager when an isolated WPF dispatcher has no input services, including WPF's null-manager failure path.</summary>
        public static InputLanguageManager? TryGetCurrent()
        {
            try
            {
                return InputLanguageManager.Current;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }
    }
}

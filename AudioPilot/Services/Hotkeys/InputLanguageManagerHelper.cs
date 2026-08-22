using System.Windows.Input;

namespace AudioPilot.Services.Hotkeys
{
    internal static class InputLanguageManagerHelper
    {
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
                // WPF can reach this path when no input manager exists for an isolated dispatcher.
                return null;
            }
        }
    }
}

using System.Runtime.InteropServices;
using System.Windows.Input;

namespace AudioPilot.Services.Hotkeys
{
    [Flags]
    internal enum HotkeyModifierMask : byte
    {
        None = 0,
        Ctrl = 1 << 0,
        Alt = 1 << 1,
        Shift = 1 << 2,
        Win = 1 << 3,
    }

    internal static partial class HotkeyModifierHelper
    {
        private const int VkShift = 0x10;
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkLWin = 0x5B;
        private const int VkRWin = 0x5C;

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        internal static HotkeyModifierMask GetModifierMaskFromKeys(IEnumerable<Key> modifiers)
        {
            HotkeyModifierMask mask = HotkeyModifierMask.None;
            foreach (Key modifier in modifiers)
            {
                mask |= modifier switch
                {
                    Key.LeftCtrl or Key.RightCtrl => HotkeyModifierMask.Ctrl,
                    Key.LeftAlt or Key.RightAlt => HotkeyModifierMask.Alt,
                    Key.LeftShift or Key.RightShift => HotkeyModifierMask.Shift,
                    Key.LWin or Key.RWin => HotkeyModifierMask.Win,
                    _ => HotkeyModifierMask.None,
                };
            }

            return mask;
        }

        internal static HotkeyModifierMask GetActiveModifierMask()
        {
            HotkeyModifierMask mask = HotkeyModifierMask.None;
            if (IsVirtualKeyPressed(VkControl))
            {
                mask |= HotkeyModifierMask.Ctrl;
            }

            if (IsVirtualKeyPressed(VkMenu))
            {
                mask |= HotkeyModifierMask.Alt;
            }

            if (IsVirtualKeyPressed(VkShift))
            {
                mask |= HotkeyModifierMask.Shift;
            }

            if (IsVirtualKeyPressed(VkLWin) || IsVirtualKeyPressed(VkRWin))
            {
                mask |= HotkeyModifierMask.Win;
            }

            return mask;
        }

        private static bool IsVirtualKeyPressed(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
    }
}

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
            if (IsKeyPressed(Key.LeftCtrl) || IsKeyPressed(Key.RightCtrl))
            {
                mask |= HotkeyModifierMask.Ctrl;
            }

            if (IsKeyPressed(Key.LeftAlt) || IsKeyPressed(Key.RightAlt))
            {
                mask |= HotkeyModifierMask.Alt;
            }

            if (IsKeyPressed(Key.LeftShift) || IsKeyPressed(Key.RightShift))
            {
                mask |= HotkeyModifierMask.Shift;
            }

            if (IsKeyPressed(Key.LWin) || IsKeyPressed(Key.RWin))
            {
                mask |= HotkeyModifierMask.Win;
            }

            return mask;
        }

        private static bool IsKeyPressed(Key key)
        {
            int vKey = KeyInterop.VirtualKeyFromKey(key);
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }
    }
}

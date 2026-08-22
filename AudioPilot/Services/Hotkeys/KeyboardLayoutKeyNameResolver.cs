using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace AudioPilot.Services.Hotkeys
{
    internal static partial class KeyboardLayoutKeyNameResolver
    {
        private const uint MAPVK_VK_TO_CHAR = 2;

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetKeyboardLayout(uint idThread);

        [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
        private static partial uint MapVirtualKeyEx(uint code, uint mapType, IntPtr keyboardLayout);

        public static string Resolve(Key key, string fallback)
        {
            if (!IsLayoutSensitive(key))
            {
                return fallback;
            }

            try
            {
                return Resolve(
                    key,
                    GetFallback(key, fallback),
                    GetKeyboardLayout(0),
                    static (virtualKey, mapType, keyboardLayout) => MapVirtualKeyEx(virtualKey, mapType, keyboardLayout));
            }
            catch (Exception)
            {
                return GetFallback(key, fallback);
            }
        }

        internal static string Resolve(
            Key key,
            string fallback,
            IntPtr keyboardLayout,
            Func<uint, uint, IntPtr, uint> mapVirtualKey)
        {
            ArgumentNullException.ThrowIfNull(mapVirtualKey);

            string effectiveFallback = GetFallback(key, fallback);
            if (!IsLayoutSensitive(key) || keyboardLayout == IntPtr.Zero)
            {
                return effectiveFallback;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0)
            {
                return effectiveFallback;
            }

            uint mapped = mapVirtualKey(unchecked((uint)virtualKey), MAPVK_VK_TO_CHAR, keyboardLayout);
            char character = unchecked((char)(mapped & ushort.MaxValue));
            if (character == '\0' || IsUnsafeDisplayCharacter(character))
            {
                return effectiveFallback;
            }

            string label = character.ToString();
            return key == Key.Decimal ? $"Num {label}" : label;
        }

        internal static bool IsLayoutSensitive(Key key)
        {
            return key is >= Key.D0 and <= Key.D9 or
                Key.Decimal or
                Key.OemSemicolon or
                Key.OemPlus or
                Key.OemComma or
                Key.OemMinus or
                Key.OemPeriod or
                Key.OemQuestion or
                Key.OemTilde or
                Key.OemOpenBrackets or
                Key.OemPipe or
                Key.OemCloseBrackets or
                Key.OemQuotes or
                Key.Oem102 or
                Key.AbntC1 or
                Key.AbntC2;
        }

        private static string GetFallback(Key key, string fallback)
        {
            return key switch
            {
                Key.OemPlus => "Equals/Plus",
                Key.OemMinus => "Minus",
                Key.OemComma => "Comma",
                Key.OemPeriod => "Period",
                Key.OemQuestion => "Slash",
                Key.OemSemicolon => "Semicolon",
                Key.OemQuotes => "Quote",
                Key.OemOpenBrackets => "Open Bracket",
                Key.OemCloseBrackets => "Close Bracket",
                Key.OemPipe => "Backslash",
                Key.OemTilde => "Backtick",
                Key.Oem102 => "International Key",
                Key.AbntC1 => "ABNT C1",
                Key.AbntC2 => "ABNT C2",
                _ => fallback,
            };
        }

        private static bool IsUnsafeDisplayCharacter(char character)
        {
            UnicodeCategory category = char.GetUnicodeCategory(character);
            return char.IsWhiteSpace(character) ||
                category is UnicodeCategory.Control or
                    UnicodeCategory.Format or
                    UnicodeCategory.Surrogate or
                    UnicodeCategory.PrivateUse or
                    UnicodeCategory.OtherNotAssigned or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator or
                    UnicodeCategory.SpaceSeparator;
        }
    }
}

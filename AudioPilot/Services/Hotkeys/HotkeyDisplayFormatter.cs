using System.Windows.Input;
using AudioPilot.Constants;

namespace AudioPilot.Services.Hotkeys
{
    internal static class HotkeyDisplayFormatter
    {
        private static readonly Dictionary<string, string> FriendlyMainInputNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Multiply"] = "Num *",
                ["Add"] = "Num +",
                ["Subtract"] = "Num -",
                ["Divide"] = "Num /",
                ["Decimal"] = "Num .",
                ["Return"] = "Enter",
                ["Prior"] = "Page Up",
                ["PageUp"] = "Page Up",
                ["Next"] = "Page Down",
                ["PageDown"] = "Page Down",
                ["D0"] = "0",
                ["D1"] = "1",
                ["D2"] = "2",
                ["D3"] = "3",
                ["D4"] = "4",
                ["D5"] = "5",
                ["D6"] = "6",
                ["D7"] = "7",
                ["D8"] = "8",
                ["D9"] = "9",
                ["NumPad0"] = "Num 0",
                ["NumPad1"] = "Num 1",
                ["NumPad2"] = "Num 2",
                ["NumPad3"] = "Num 3",
                ["NumPad4"] = "Num 4",
                ["NumPad5"] = "Num 5",
                ["NumPad6"] = "Num 6",
                ["NumPad7"] = "Num 7",
                ["NumPad8"] = "Num 8",
                ["NumPad9"] = "Num 9",
                ["Back"] = "Backspace",
                ["Capital"] = "Caps Lock",
                ["CapsLock"] = "Caps Lock",
                ["Escape"] = "Esc",
                ["Snapshot"] = "Print Screen",
                ["PrintScreen"] = "Print Screen",
                ["Scroll"] = "Scroll Lock",
                ["NumLock"] = "Num Lock",
                ["Apps"] = "Menu",
                ["MediaPlayPause"] = "Play/Pause",
                ["MediaNextTrack"] = "Next Track",
                ["MediaPreviousTrack"] = "Previous Track",
                ["MediaStop"] = "Media Stop",
                ["VolumeMute"] = "Volume Mute",
                ["VolumeUp"] = "Volume Up",
                ["VolumeDown"] = "Volume Down",
                ["BrowserBack"] = "Browser Back",
                ["BrowserForward"] = "Browser Forward",
                ["BrowserRefresh"] = "Browser Refresh",
                ["BrowserStop"] = "Browser Stop",
                ["BrowserSearch"] = "Browser Search",
                ["BrowserFavorites"] = "Browser Favorites",
                ["BrowserHome"] = "Browser Home",
                ["LaunchMail"] = "Mail",
                ["SelectMedia"] = "Media Select",
                ["LaunchApplication1"] = "App 1",
                ["LaunchApplication2"] = "App 2",
                ["MouseLeft"] = "Left Click",
                ["MouseRight"] = "Right Click",
                ["MouseMiddle"] = "Middle Click",
                ["MouseX1"] = "Mouse 4",
                ["MouseX2"] = "Mouse 5",
                ["WheelUp"] = "Wheel Up",
                ["WheelDown"] = "Wheel Down",
                ["WheelLeft"] = "Wheel Left",
                ["WheelRight"] = "Wheel Right",
            };

        private static readonly Dictionary<string, string> FriendlyModifierNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Control"] = "Ctrl",
                ["LeftCtrl"] = "Ctrl",
                ["RightCtrl"] = "Ctrl",
                ["LeftAlt"] = "Alt",
                ["RightAlt"] = "Alt",
                ["LeftShift"] = "Shift",
                ["RightShift"] = "Shift",
                ["Windows"] = "Win",
                ["LWin"] = "Win",
                ["RWin"] = "Win",
            };

        public static string FormatCompact(string? hotkey)
            => Format(hotkey, "+", KeyboardLayoutKeyNameResolver.Resolve);

        public static string FormatSpaced(string? hotkey)
            => Format(hotkey, " + ", KeyboardLayoutKeyNameResolver.Resolve);

        internal static string FormatCompact(string? hotkey, Func<Key, string, string> layoutKeyResolver)
            => Format(hotkey, "+", layoutKeyResolver);

        internal static string FormatSpaced(string? hotkey, Func<Key, string, string> layoutKeyResolver)
            => Format(hotkey, " + ", layoutKeyResolver);

        public static string FormatMainInput(string? token)
            => FormatMainInput(token, KeyboardLayoutKeyNameResolver.Resolve);

        private static string FormatMainInput(string? token, Func<Key, string, string> layoutKeyResolver)
        {
            string normalized = token?.Trim() ?? string.Empty;
            string fallback = FriendlyMainInputNames.TryGetValue(normalized, out string? friendlyName)
                ? friendlyName
                : normalized;

            return TryResolveLayoutSensitiveKey(normalized, out Key key)
                ? layoutKeyResolver(key, fallback)
                : fallback;
        }

        private static string Format(string? hotkey, string separator, Func<Key, string, string> layoutKeyResolver)
        {
            ArgumentNullException.ThrowIfNull(layoutKeyResolver);

            string normalized = hotkey?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            if (normalized == "+")
            {
                return FormatMainInput(normalized, layoutKeyResolver);
            }

            const string friendlyNumpadPlus = "Num +";
            if (normalized.EndsWith(friendlyNumpadPlus, StringComparison.OrdinalIgnoreCase))
            {
                string modifierPrefix = normalized[..^friendlyNumpadPlus.Length].TrimEnd();
                if (modifierPrefix.EndsWith('+'))
                {
                    modifierPrefix = modifierPrefix[..^1].TrimEnd();
                }

                return modifierPrefix.Length == 0
                    ? friendlyNumpadPlus
                    : $"{Format(modifierPrefix, separator, layoutKeyResolver)}{separator}{friendlyNumpadPlus}";
            }

            bool hasLiteralPlusMainInput = normalized.EndsWith("++", StringComparison.Ordinal);
            if (hasLiteralPlusMainInput)
            {
                string modifierPrefix = normalized[..^2];
                return modifierPrefix.Length == 0
                    ? FormatMainInput("+", layoutKeyResolver)
                    : $"{Format(modifierPrefix, separator, layoutKeyResolver)}{separator}{FormatMainInput("+", layoutKeyResolver)}";
            }

            string[] parts = normalized.Split('+', StringSplitOptions.TrimEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                parts[index] = FriendlyModifierNames.TryGetValue(parts[index], out string? friendlyModifier)
                    ? friendlyModifier
                    : FormatMainInput(parts[index], layoutKeyResolver);
            }

            return string.Join(separator, parts);
        }

        private static bool TryResolveLayoutSensitiveKey(string token, out Key key)
        {
            if (!AppConstants.Hotkeys.MainKeyAliases.TryGetValue(token, out key) &&
                !Enum.TryParse(token, ignoreCase: true, out key))
            {
                return false;
            }

            return KeyboardLayoutKeyNameResolver.IsLayoutSensitive(key);
        }
    }
}

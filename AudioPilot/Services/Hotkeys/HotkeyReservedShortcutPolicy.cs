using System.Windows.Input;

namespace AudioPilot.Services.Hotkeys
{
    internal static class HotkeyReservedShortcutPolicy
    {
        public static bool IsSupported(
            HotkeyMainInput input,
            IEnumerable<Key> modifiers,
            IEnumerable<string>? configuredStandaloneKeys = null)
        {
            List<Key> normalizedModifiers = [.. NormalizeModifiers(modifiers)];
            return input.IsSupportedModifierCount(normalizedModifiers.Count, configuredStandaloneKeys)
                && !IsReserved(input, normalizedModifiers, out _);
        }

        public static bool IsReserved(HotkeyMainInput input, IEnumerable<Key> modifiers, out string reservedShortcutName)
        {
            reservedShortcutName = string.Empty;
            if (input.Kind != HotkeyMainInputKind.Keyboard || !input.HasValue)
            {
                return false;
            }

            List<Key> normalizedModifiers = [.. NormalizeModifiers(modifiers)];
            bool hasCtrl = normalizedModifiers.Contains(Key.LeftCtrl);
            bool hasAlt = normalizedModifiers.Contains(Key.LeftAlt);
            bool hasShift = normalizedModifiers.Contains(Key.LeftShift);
            bool hasWin = normalizedModifiers.Contains(Key.LWin);

            switch (input.Key)
            {
                case Key.Tab when hasAlt && !hasCtrl && !hasWin:
                    reservedShortcutName = "Alt+Tab";
                    return true;

                case Key.Tab when hasWin && !hasAlt && !hasCtrl && !hasShift:
                    reservedShortcutName = "Win+Tab";
                    return true;

                case Key.Tab when hasWin && hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Ctrl+Win+Tab";
                    return true;

                case Key.Tab when hasWin && hasShift && !hasAlt && !hasCtrl:
                    reservedShortcutName = "Win+Shift+Tab";
                    return true;

                case Key.Escape when hasAlt && !hasCtrl && !hasWin && !hasShift:
                    reservedShortcutName = "Alt+Esc";
                    return true;

                case Key.Escape when hasCtrl && !hasAlt && !hasWin:
                    reservedShortcutName = hasShift ? "Ctrl+Shift+Esc" : "Ctrl+Esc";
                    return true;

                case Key.Delete when hasCtrl && hasAlt:
                    reservedShortcutName = "Ctrl+Alt+Delete";
                    return true;

                case Key.F4 when hasAlt && !hasCtrl && !hasWin && !hasShift:
                    reservedShortcutName = "Alt+F4";
                    return true;

                case Key.Space when hasAlt && !hasCtrl && !hasWin && !hasShift:
                    reservedShortcutName = "Alt+Space";
                    return true;

                case Key.L when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+L";
                    return true;

                case Key.D when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+D";
                    return true;

                case Key.R when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+R";
                    return true;

                case Key.K when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+K";
                    return true;

                case Key.S when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+S";
                    return true;

                case Key.A when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+A";
                    return true;

                case Key.P when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+P";
                    return true;

                case Key.Space when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+Space";
                    return true;

                case Key.S when hasWin && hasShift && !hasCtrl && !hasAlt:
                    reservedShortcutName = "Win+Shift+S";
                    return true;

                case Key.V when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+V";
                    return true;

                case Key.G when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+G";
                    return true;

                case Key.Left when hasWin:
                    reservedShortcutName = BuildWinArrowShortcutName("Left", hasCtrl, hasShift, hasAlt);
                    return true;

                case Key.Right when hasWin:
                    reservedShortcutName = BuildWinArrowShortcutName("Right", hasCtrl, hasShift, hasAlt);
                    return true;

                case Key.Up when hasWin:
                    reservedShortcutName = BuildWinArrowShortcutName("Up", hasCtrl, hasShift, hasAlt);
                    return true;

                case Key.Down when hasWin:
                    reservedShortcutName = BuildWinArrowShortcutName("Down", hasCtrl, hasShift, hasAlt);
                    return true;

                case Key.X when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+X";
                    return true;

                case Key.I when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+I";
                    return true;

                case Key.E when hasWin && !hasCtrl && !hasAlt && !hasShift:
                    reservedShortcutName = "Win+E";
                    return true;

                default:
                    return false;
            }
        }

        private static IEnumerable<Key> NormalizeModifiers(IEnumerable<Key> modifiers)
        {
            bool hasCtrl = false;
            bool hasAlt = false;
            bool hasShift = false;
            bool hasWin = false;

            foreach (Key modifier in modifiers)
            {
                switch (modifier)
                {
                    case Key.LeftCtrl:
                    case Key.RightCtrl:
                        hasCtrl = true;
                        break;

                    case Key.LeftAlt:
                    case Key.RightAlt:
                    case Key.System:
                        hasAlt = true;
                        break;

                    case Key.LeftShift:
                    case Key.RightShift:
                        hasShift = true;
                        break;

                    case Key.LWin:
                    case Key.RWin:
                        hasWin = true;
                        break;
                }
            }

            if (hasCtrl)
            {
                yield return Key.LeftCtrl;
            }

            if (hasAlt)
            {
                yield return Key.LeftAlt;
            }

            if (hasShift)
            {
                yield return Key.LeftShift;
            }

            if (hasWin)
            {
                yield return Key.LWin;
            }
        }

        private static string BuildWinArrowShortcutName(string direction, bool hasCtrl, bool hasShift, bool hasAlt)
        {
            List<string> parts = ["Win"];

            if (hasCtrl)
            {
                parts.Add("Ctrl");
            }

            if (hasShift)
            {
                parts.Add("Shift");
            }

            if (hasAlt)
            {
                parts.Add("Alt");
            }

            parts.Add(direction);
            return string.Join("+", parts);
        }
    }
}

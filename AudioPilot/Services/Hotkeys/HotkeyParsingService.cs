using System.Collections.Concurrent;
using System.Windows.Input;
using AudioPilot.Constants;

namespace AudioPilot.Services.Hotkeys
{
    internal sealed class HotkeyParsingService
    {
        private readonly ConcurrentDictionary<string, (HotkeyMainInput mainInput, List<Key> modifiers)?> _parseCache = new(StringComparer.OrdinalIgnoreCase);

        public (HotkeyMainInput mainInput, List<Key> modifiers)? ParseHotkeyString(string hotkeyString)
        {
            if (string.IsNullOrWhiteSpace(hotkeyString))
            {
                return null;
            }

            string normalizedHotkey = hotkeyString.Trim();
            return _parseCache.GetOrAdd(normalizedHotkey, str =>
            {
                string[] parts = SplitHotkeyParts(str);
                if (parts.Length == 0)
                {
                    return null;
                }

                var modifiers = new List<Key>();
                HotkeyMainInput mainInput = HotkeyMainInput.None;

                for (int index = 0; index < parts.Length; index++)
                {
                    if (index < parts.Length - 1)
                    {
                        Key? modifier = ParseModifierKey(parts[index]);
                        if (!modifier.HasValue)
                        {
                            return null;
                        }

                        modifiers.Add(modifier.Value);
                        continue;
                    }

                    mainInput = ParseMainInput(parts[index]);
                }

                return mainInput.HasValue ? (mainInput, modifiers) : null;
            });
        }

        private static string[] SplitHotkeyParts(string hotkey)
        {
            if (hotkey == "+")
            {
                return [hotkey];
            }

            if (hotkey.EndsWith('+'))
            {
                string prefixWithSeparator = hotkey[..^1].TrimEnd();
                if (!prefixWithSeparator.EndsWith('+'))
                {
                    return [];
                }

                string modifierPrefix = prefixWithSeparator[..^1].TrimEnd();
                if (modifierPrefix.Length == 0)
                {
                    return ["+"];
                }

                string[] modifiers = modifierPrefix.Split(
                    '+',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return [.. modifiers, "+"];
            }

            return hotkey.Split(
                '+',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public static string BuildComboKey(IEnumerable<Key> modifiers, HotkeyMainInput mainInput)
        {
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Key modifier in modifiers)
            {
                string name = NormalizeModifierName(modifier);
                if (seen.Add(name))
                {
                    normalized.Add(name);
                }
            }

            normalized.Sort(static (left, right) =>
            {
                int byOrder = GetModifierOrder(left).CompareTo(GetModifierOrder(right));
                if (byOrder != 0)
                {
                    return byOrder;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(left, right);
            });

            string mainToken = mainInput.SerializationToken;
            if (normalized.Count == 0)
            {
                return mainToken;
            }

            return $"{string.Join("+", normalized)}+{mainToken}";
        }

        public static string BuildComboKey(IEnumerable<Key> modifiers, Key mainKey)
        {
            return BuildComboKey(modifiers, HotkeyMainInput.FromKeyboard(mainKey));
        }

        public void ClearCache()
        {
            _parseCache.Clear();
        }

        private static Key? ParseModifierKey(string name)
        {
            if (Enum.TryParse(name, true, out Key key) && IsModifier(key))
            {
                return key;
            }

            if (AppConstants.Hotkeys.ModifierAliases.TryGetValue(name, out Key mappedModifier))
            {
                return mappedModifier;
            }

            return null;
        }

        private static HotkeyMainInput ParseMainInput(string name)
        {
            if (AppConstants.Hotkeys.MainKeyAliases.TryGetValue(name, out Key mappedMain))
            {
                return HotkeyMainInput.FromKeyboard(mappedMain);
            }

            if (Enum.TryParse(name, true, out Key key))
            {
                return HotkeyMainInput.FromKeyboard(key);
            }

            HotkeyMainInput keyboardAlias = name.ToLowerInvariant() switch
            {
                "space" or "spacebar" => HotkeyMainInput.FromKeyboard(Key.Space),
                "enter" or "return" => HotkeyMainInput.FromKeyboard(Key.Enter),
                "esc" or "escape" => HotkeyMainInput.FromKeyboard(Key.Escape),
                "tab" => HotkeyMainInput.FromKeyboard(Key.Tab),
                "back" or "backspace" => HotkeyMainInput.FromKeyboard(Key.Back),
                "del" or "delete" => HotkeyMainInput.FromKeyboard(Key.Delete),
                "ins" or "insert" => HotkeyMainInput.FromKeyboard(Key.Insert),
                "home" => HotkeyMainInput.FromKeyboard(Key.Home),
                "end" => HotkeyMainInput.FromKeyboard(Key.End),
                "pgup" or "pageup" => HotkeyMainInput.FromKeyboard(Key.PageUp),
                "pgdn" or "pagedown" => HotkeyMainInput.FromKeyboard(Key.PageDown),
                "up" => HotkeyMainInput.FromKeyboard(Key.Up),
                "down" => HotkeyMainInput.FromKeyboard(Key.Down),
                "left" => HotkeyMainInput.FromKeyboard(Key.Left),
                "right" => HotkeyMainInput.FromKeyboard(Key.Right),
                "prtsc" or "printscreen" => HotkeyMainInput.FromKeyboard(Key.PrintScreen),
                "scroll" => HotkeyMainInput.FromKeyboard(Key.Scroll),
                "pause" => HotkeyMainInput.FromKeyboard(Key.Pause),
                "numlock" => HotkeyMainInput.FromKeyboard(Key.NumLock),
                "caps" => HotkeyMainInput.FromKeyboard(Key.CapsLock),
                "lwin" => HotkeyMainInput.FromKeyboard(Key.LWin),
                "rwin" => HotkeyMainInput.FromKeyboard(Key.RWin),
                "apps" => HotkeyMainInput.FromKeyboard(Key.Apps),
                "f1" => HotkeyMainInput.FromKeyboard(Key.F1),
                "f2" => HotkeyMainInput.FromKeyboard(Key.F2),
                "f3" => HotkeyMainInput.FromKeyboard(Key.F3),
                "f4" => HotkeyMainInput.FromKeyboard(Key.F4),
                "f5" => HotkeyMainInput.FromKeyboard(Key.F5),
                "f6" => HotkeyMainInput.FromKeyboard(Key.F6),
                "f7" => HotkeyMainInput.FromKeyboard(Key.F7),
                "f8" => HotkeyMainInput.FromKeyboard(Key.F8),
                "f9" => HotkeyMainInput.FromKeyboard(Key.F9),
                "f10" => HotkeyMainInput.FromKeyboard(Key.F10),
                "f11" => HotkeyMainInput.FromKeyboard(Key.F11),
                "f12" => HotkeyMainInput.FromKeyboard(Key.F12),
                "f13" => HotkeyMainInput.FromKeyboard(Key.F13),
                "f14" => HotkeyMainInput.FromKeyboard(Key.F14),
                "f15" => HotkeyMainInput.FromKeyboard(Key.F15),
                "f16" => HotkeyMainInput.FromKeyboard(Key.F16),
                "f17" => HotkeyMainInput.FromKeyboard(Key.F17),
                "f18" => HotkeyMainInput.FromKeyboard(Key.F18),
                "f19" => HotkeyMainInput.FromKeyboard(Key.F19),
                "f20" => HotkeyMainInput.FromKeyboard(Key.F20),
                "f21" => HotkeyMainInput.FromKeyboard(Key.F21),
                "f22" => HotkeyMainInput.FromKeyboard(Key.F22),
                "f23" => HotkeyMainInput.FromKeyboard(Key.F23),
                "f24" => HotkeyMainInput.FromKeyboard(Key.F24),
                "num0" or "numpad0" => HotkeyMainInput.FromKeyboard(Key.NumPad0),
                "num1" or "numpad1" => HotkeyMainInput.FromKeyboard(Key.NumPad1),
                "num2" or "numpad2" => HotkeyMainInput.FromKeyboard(Key.NumPad2),
                "num3" or "numpad3" => HotkeyMainInput.FromKeyboard(Key.NumPad3),
                "num4" or "numpad4" => HotkeyMainInput.FromKeyboard(Key.NumPad4),
                "num5" or "numpad5" => HotkeyMainInput.FromKeyboard(Key.NumPad5),
                "num6" or "numpad6" => HotkeyMainInput.FromKeyboard(Key.NumPad6),
                "num7" or "numpad7" => HotkeyMainInput.FromKeyboard(Key.NumPad7),
                "num8" or "numpad8" => HotkeyMainInput.FromKeyboard(Key.NumPad8),
                "num9" or "numpad9" => HotkeyMainInput.FromKeyboard(Key.NumPad9),
                "num*" or "multiply" => HotkeyMainInput.FromKeyboard(Key.Multiply),
                "num+" or "add" => HotkeyMainInput.FromKeyboard(Key.Add),
                "num-" or "subtract" => HotkeyMainInput.FromKeyboard(Key.Subtract),
                "num." or "decimal" => HotkeyMainInput.FromKeyboard(Key.Decimal),
                "num/" or "divide" => HotkeyMainInput.FromKeyboard(Key.Divide),
                "mute" or "volumemute" => HotkeyMainInput.FromKeyboard(Key.VolumeMute),
                "voldown" => HotkeyMainInput.FromKeyboard(Key.VolumeDown),
                "volup" => HotkeyMainInput.FromKeyboard(Key.VolumeUp),
                "next" or "medianext" => HotkeyMainInput.FromKeyboard(Key.MediaNextTrack),
                "prev" or "mediaprev" => HotkeyMainInput.FromKeyboard(Key.MediaPreviousTrack),
                "play" or "pause" or "mediaplaypause" => HotkeyMainInput.FromKeyboard(Key.MediaPlayPause),
                "stop" or "mediastop" => HotkeyMainInput.FromKeyboard(Key.MediaStop),
                "browserback" => HotkeyMainInput.FromKeyboard(Key.BrowserBack),
                "browserforward" => HotkeyMainInput.FromKeyboard(Key.BrowserForward),
                "refresh" => HotkeyMainInput.FromKeyboard(Key.BrowserRefresh),
                "browserstop" => HotkeyMainInput.FromKeyboard(Key.BrowserStop),
                "search" => HotkeyMainInput.FromKeyboard(Key.BrowserSearch),
                "favorites" => HotkeyMainInput.FromKeyboard(Key.BrowserFavorites),
                "browserhome" => HotkeyMainInput.FromKeyboard(Key.BrowserHome),
                _ => HotkeyMainInput.None,
            };

            if (keyboardAlias.HasValue)
            {
                return keyboardAlias;
            }

            return HotkeyMainInput.TryParse(name, out HotkeyMainInput parsedInput)
                ? parsedInput
                : HotkeyMainInput.None;
        }

        private static bool IsModifier(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;
        }

        private static string NormalizeModifierName(Key key)
        {
            return key switch
            {
                Key.LeftCtrl or Key.RightCtrl => "Ctrl",
                Key.LeftAlt or Key.RightAlt => "Alt",
                Key.LeftShift or Key.RightShift => "Shift",
                Key.LWin or Key.RWin => "Win",
                _ => key.ToString()
            };
        }

        private static int GetModifierOrder(string name)
        {
            return name switch
            {
                "Ctrl" => 0,
                "Alt" => 1,
                "Shift" => 2,
                "Win" => 3,
                _ => 4
            };
        }
    }
}

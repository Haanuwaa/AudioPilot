using System.Collections.Concurrent;
using System.Collections.Frozen;
using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Services.Hotkeys;

public enum HotkeyMainInputKind
{
    None,
    Keyboard,
    MouseButton,
    MouseWheel,
}

public readonly record struct HotkeySyntaxMainInput
{
    private HotkeySyntaxMainInput(HotkeyMainInputKind kind, HotkeyKey key, HotkeyMouseButton mouseButton, int wheelDirection)
    {
        Kind = kind;
        Key = key;
        MouseButton = mouseButton;
        WheelDirection = wheelDirection;
    }

    public HotkeyMainInputKind Kind { get; }
    public HotkeyKey Key { get; }
    public HotkeyMouseButton MouseButton { get; }
    public int WheelDirection { get; }

    public bool HasValue => Kind != HotkeyMainInputKind.None;

    public bool AllowsStandaloneWithoutModifier => Kind switch
    {
        HotkeyMainInputKind.Keyboard => HotkeySyntax.IsStandaloneKeyboardKeyAllowed(Key),
        HotkeyMainInputKind.MouseButton => MouseButton is HotkeyMouseButton.XButton1 or HotkeyMouseButton.XButton2,
        _ => false,
    };

    public bool IsSupportedModifierCount(int modifierCount) =>
        modifierCount > 0 || AllowsStandaloneWithoutModifier;

    public string SerializationToken => Kind switch
    {
        HotkeyMainInputKind.Keyboard => HotkeySyntax.FormatKeyboardToken(Key),
        HotkeyMainInputKind.MouseButton => MouseButton switch
        {
            HotkeyMouseButton.Left => "MouseLeft",
            HotkeyMouseButton.Right => "MouseRight",
            HotkeyMouseButton.Middle => "MouseMiddle",
            HotkeyMouseButton.XButton1 => "MouseX1",
            HotkeyMouseButton.XButton2 => "MouseX2",
            _ => string.Empty,
        },
        HotkeyMainInputKind.MouseWheel => WheelDirection switch
        {
            1 => "WheelUp",
            -1 => "WheelDown",
            -2 => "WheelLeft",
            2 => "WheelRight",
            _ => string.Empty,
        },
        _ => string.Empty,
    };

    public static HotkeySyntaxMainInput None => default;

    public static HotkeySyntaxMainInput FromKeyboard(HotkeyKey key) =>
        key == HotkeyKey.None ? None : new HotkeySyntaxMainInput(HotkeyMainInputKind.Keyboard, key, default, 0);

    public static HotkeySyntaxMainInput FromMouseButton(HotkeyMouseButton mouseButton) =>
        new(HotkeyMainInputKind.MouseButton, HotkeyKey.None, mouseButton, 0);

    public static HotkeySyntaxMainInput WheelUp => new(HotkeyMainInputKind.MouseWheel, HotkeyKey.None, default, 1);
    public static HotkeySyntaxMainInput WheelDown => new(HotkeyMainInputKind.MouseWheel, HotkeyKey.None, default, -1);
    public static HotkeySyntaxMainInput WheelLeft => new(HotkeyMainInputKind.MouseWheel, HotkeyKey.None, default, -2);
    public static HotkeySyntaxMainInput WheelRight => new(HotkeyMainInputKind.MouseWheel, HotkeyKey.None, default, 2);
}

/// <summary>
/// WPF-free hotkey parser, canonicalizer, and reserved-shortcut policy shared by Core, CLI, and WPF UI.
/// </summary>
internal static class HotkeySyntax
{
    public static readonly FrozenDictionary<string, HotkeyKey> ModifierAliases = new Dictionary<string, HotkeyKey>(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = HotkeyKey.LeftCtrl,
        ["control"] = HotkeyKey.LeftCtrl,
        ["alt"] = HotkeyKey.LeftAlt,
        ["shift"] = HotkeyKey.LeftShift,
        ["win"] = HotkeyKey.LWin,
        ["windows"] = HotkeyKey.LWin,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenDictionary<string, HotkeyKey> MainKeyAliases = new Dictionary<string, HotkeyKey>(StringComparer.OrdinalIgnoreCase)
    {
        ["."] = HotkeyKey.OemPeriod,
        [","] = HotkeyKey.OemComma,
        ["period"] = HotkeyKey.OemPeriod,
        ["comma"] = HotkeyKey.OemComma,
        ["+"] = HotkeyKey.OemPlus,
        ["plus"] = HotkeyKey.OemPlus,
        ["equals"] = HotkeyKey.OemPlus,
        ["="] = HotkeyKey.OemPlus,
        ["-"] = HotkeyKey.OemMinus,
        ["minus"] = HotkeyKey.OemMinus,
        ["dash"] = HotkeyKey.OemMinus,
        ["_"] = HotkeyKey.OemMinus,
        ["underscore"] = HotkeyKey.OemMinus,
        ["/"] = HotkeyKey.Oem2,
        ["slash"] = HotkeyKey.Oem2,
        ["?"] = HotkeyKey.Oem2,
        ["question"] = HotkeyKey.Oem2,
        [";"] = HotkeyKey.OemSemicolon,
        ["semicolon"] = HotkeyKey.OemSemicolon,
        [":"] = HotkeyKey.OemSemicolon,
        ["colon"] = HotkeyKey.OemSemicolon,
        ["'"] = HotkeyKey.OemQuotes,
        ["quote"] = HotkeyKey.OemQuotes,
        ["\""] = HotkeyKey.OemQuotes,
        ["dblquote"] = HotkeyKey.OemQuotes,
        ["["] = HotkeyKey.OemOpenBrackets,
        ["openbracket"] = HotkeyKey.OemOpenBrackets,
        ["]"] = HotkeyKey.OemCloseBrackets,
        ["closebracket"] = HotkeyKey.OemCloseBrackets,
        ["\\"] = HotkeyKey.OemBackslash,
        ["backslash"] = HotkeyKey.OemBackslash,
        ["`"] = HotkeyKey.OemTilde,
        ["tilde"] = HotkeyKey.OemTilde,

        ["space"] = HotkeyKey.Space,
        ["enter"] = HotkeyKey.Enter,
        ["return"] = HotkeyKey.Enter,
        ["esc"] = HotkeyKey.Escape,
        ["escape"] = HotkeyKey.Escape,
        ["back"] = HotkeyKey.Back,
        ["backspace"] = HotkeyKey.Back,
        ["tab"] = HotkeyKey.Tab,
        ["del"] = HotkeyKey.Delete,
        ["delete"] = HotkeyKey.Delete,
        ["ins"] = HotkeyKey.Insert,
        ["insert"] = HotkeyKey.Insert,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenDictionary<HotkeyKey, string> MainKeyDisplayAliases = new Dictionary<HotkeyKey, string>
    {
        [HotkeyKey.OemPeriod] = ".",
        [HotkeyKey.OemComma] = ",",
        [HotkeyKey.OemPlus] = "+",
        [HotkeyKey.OemMinus] = "-",
        [HotkeyKey.Oem2] = "/",
        [HotkeyKey.OemSemicolon] = ";",
        [HotkeyKey.OemQuotes] = "'",
        [HotkeyKey.OemOpenBrackets] = "[",
        [HotkeyKey.OemCloseBrackets] = "]",
        [HotkeyKey.OemBackslash] = "\\",
        [HotkeyKey.OemTilde] = "`",
    }.ToFrozenDictionary();

    private const int MaxParseCacheEntries = 512;

    private static readonly ConcurrentDictionary<string, (HotkeySyntaxMainInput MainInput, HotkeyKey[] Modifiers)?> ParseCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void ClearCache() => ParseCache.Clear();

    public static (HotkeySyntaxMainInput MainInput, HotkeyKey[] Modifiers)? Parse(string? hotkeyString)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString))
        {
            return null;
        }

        string normalizedHotkey = hotkeyString.Trim();
        if (ParseCache.TryGetValue(normalizedHotkey, out var cached))
        {
            return cached;
        }

        var parsed = ParseCore(normalizedHotkey);
        if (ParseCache.Count >= MaxParseCacheEntries)
        {
            ParseCache.Clear();
        }

        ParseCache.TryAdd(normalizedHotkey, parsed);
        return parsed;
    }

    private static (HotkeySyntaxMainInput MainInput, HotkeyKey[] Modifiers)? ParseCore(string str)
    {
        string[] parts = SplitHotkeyParts(str);
        if (parts.Length == 0)
        {
            return null;
        }

        List<HotkeyKey> modifiers = [];
        HotkeySyntaxMainInput mainInput = HotkeySyntaxMainInput.None;

        for (int index = 0; index < parts.Length; index++)
        {
            if (index < parts.Length - 1)
            {
                HotkeyKey? modifier = ParseModifierKey(parts[index]);
                if (!modifier.HasValue)
                {
                    return null;
                }

                modifiers.Add(modifier.Value);
                continue;
            }

            mainInput = ParseMainInput(parts[index]);
        }

        return mainInput.HasValue ? (mainInput, [.. modifiers]) : null;
    }

    public static string BuildComboKey(IEnumerable<HotkeyKey> modifiers, HotkeySyntaxMainInput mainInput)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HotkeyKey modifier in modifiers)
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
            return byOrder != 0 ? byOrder : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        });

        string mainToken = mainInput.SerializationToken;
        return normalized.Count == 0 ? mainToken : $"{string.Join("+", normalized)}+{mainToken}";
    }

    public static bool TryNormalize(string? rawHotkey, out string normalized, bool canonicalModifierOrder = false)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawHotkey)) return false;
        var parsed = Parse(rawHotkey);
        if (!parsed.HasValue || !IsSupported(parsed.Value.MainInput, parsed.Value.Modifiers))
            return false;
        normalized = canonicalModifierOrder
            ? BuildComboKey(parsed.Value.Modifiers, parsed.Value.MainInput)
            : string.Join("+", parsed.Value.Modifiers.Select(NormalizeModifierName)
                .Distinct(StringComparer.Ordinal).Append(parsed.Value.MainInput.SerializationToken));
        return normalized.Length > 0;
    }

    public static bool IsSupported(HotkeySyntaxMainInput input, IEnumerable<HotkeyKey> modifiers)
    {
        List<HotkeyKey> normalizedModifiers = [.. NormalizeModifiers(modifiers)];
        return input.IsSupportedModifierCount(normalizedModifiers.Count)
            && !IsReserved(input, normalizedModifiers, out _);
    }

    public static bool IsReserved(HotkeySyntaxMainInput input, IEnumerable<HotkeyKey> modifiers, out string reservedShortcutName)
    {
        reservedShortcutName = string.Empty;
        if (input.Kind != HotkeyMainInputKind.Keyboard || !input.HasValue)
        {
            return false;
        }

        List<HotkeyKey> normalizedModifiers = [.. NormalizeModifiers(modifiers)];
        bool hasCtrl = normalizedModifiers.Contains(HotkeyKey.LeftCtrl);
        bool hasAlt = normalizedModifiers.Contains(HotkeyKey.LeftAlt);
        bool hasShift = normalizedModifiers.Contains(HotkeyKey.LeftShift);
        bool hasWin = normalizedModifiers.Contains(HotkeyKey.LWin);

        switch (input.Key)
        {
            case HotkeyKey.Tab when hasAlt && !hasCtrl && !hasWin:
                reservedShortcutName = "Alt+Tab";
                return true;
            case HotkeyKey.Tab when hasWin && !hasAlt && !hasCtrl && !hasShift:
                reservedShortcutName = "Win+Tab";
                return true;
            case HotkeyKey.Tab when hasWin && hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Ctrl+Win+Tab";
                return true;
            case HotkeyKey.Tab when hasWin && hasShift && !hasAlt && !hasCtrl:
                reservedShortcutName = "Win+Shift+Tab";
                return true;
            case HotkeyKey.Escape when hasAlt && !hasCtrl && !hasWin && !hasShift:
                reservedShortcutName = "Alt+Esc";
                return true;
            case HotkeyKey.Escape when hasCtrl && !hasAlt && !hasWin:
                reservedShortcutName = hasShift ? "Ctrl+Shift+Esc" : "Ctrl+Esc";
                return true;
            case HotkeyKey.Delete when hasCtrl && hasAlt:
                reservedShortcutName = "Ctrl+Alt+Delete";
                return true;
            case HotkeyKey.F4 when hasAlt && !hasCtrl && !hasWin && !hasShift:
                reservedShortcutName = "Alt+F4";
                return true;
            case HotkeyKey.Space when hasAlt && !hasCtrl && !hasWin && !hasShift:
                reservedShortcutName = "Alt+Space";
                return true;
            case HotkeyKey.L when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+L";
                return true;
            case HotkeyKey.D when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+D";
                return true;
            case HotkeyKey.R when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+R";
                return true;
            case HotkeyKey.K when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+K";
                return true;
            case HotkeyKey.S when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+S";
                return true;
            case HotkeyKey.A when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+A";
                return true;
            case HotkeyKey.P when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+P";
                return true;
            case HotkeyKey.Space when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+Space";
                return true;
            case HotkeyKey.S when hasWin && hasShift && !hasCtrl && !hasAlt:
                reservedShortcutName = "Win+Shift+S";
                return true;
            case HotkeyKey.V when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+V";
                return true;
            case HotkeyKey.G when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+G";
                return true;
            case HotkeyKey.Left when hasWin:
                reservedShortcutName = BuildWinArrowShortcutName("Left", hasCtrl, hasShift, hasAlt);
                return true;
            case HotkeyKey.Right when hasWin:
                reservedShortcutName = BuildWinArrowShortcutName("Right", hasCtrl, hasShift, hasAlt);
                return true;
            case HotkeyKey.Up when hasWin:
                reservedShortcutName = BuildWinArrowShortcutName("Up", hasCtrl, hasShift, hasAlt);
                return true;
            case HotkeyKey.Down when hasWin:
                reservedShortcutName = BuildWinArrowShortcutName("Down", hasCtrl, hasShift, hasAlt);
                return true;
            case HotkeyKey.X when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+X";
                return true;
            case HotkeyKey.I when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+I";
                return true;
            case HotkeyKey.E when hasWin && !hasCtrl && !hasAlt && !hasShift:
                reservedShortcutName = "Win+E";
                return true;
            default:
                return false;
        }
    }

    public static string FormatKeyboardToken(HotkeyKey key) =>
        MainKeyDisplayAliases.TryGetValue(key, out string? formatted) ? formatted : key.ToString();

    public static bool IsStandaloneKeyboardKeyAllowed(HotkeyKey key) =>
        key is >= HotkeyKey.F1 and <= HotkeyKey.F24 or
            HotkeyKey.Pause or HotkeyKey.Scroll or HotkeyKey.PrintScreen or HotkeyKey.Insert or HotkeyKey.NumLock or
            HotkeyKey.MediaPlayPause or
            HotkeyKey.MediaNextTrack or
            HotkeyKey.MediaPreviousTrack or
            HotkeyKey.MediaStop or
            HotkeyKey.VolumeMute or
            HotkeyKey.VolumeUp or
            HotkeyKey.VolumeDown;

    public static bool IsHoldable(HotkeySyntaxMainInput input) =>
        input.HasValue && input.Kind switch
        {
            HotkeyMainInputKind.Keyboard => input.Key is not (HotkeyKey.Pause or HotkeyKey.PrintScreen),
            HotkeyMainInputKind.MouseButton => true,
            _ => false,
        };

    public static string FormatActionName(int actionId) => actionId switch
    {
        AppConstants.Hotkeys.HotkeyId => "Switch audio device",
        AppConstants.Hotkeys.ToggleAppVisibilityHotkeyId => "Show/hide AudioPilot",
        AppConstants.Hotkeys.ShowAudioStatusHotkeyId => "Show audio status",
        AppConstants.Hotkeys.QuickDevicePickerHotkeyId => "Device picker",
        AppConstants.Hotkeys.VolumeMixerHotkeyId => "Quick mixer",
        AppConstants.Hotkeys.MediaPlayPauseId => "Play/pause",
        AppConstants.Hotkeys.MediaNextTrackId => "Next track",
        AppConstants.Hotkeys.MediaPrevTrackId => "Previous track",
        AppConstants.Hotkeys.MediaShowCurrentTrackId => "Current track",
        AppConstants.Hotkeys.MediaSeekForwardId => "Seek forward",
        AppConstants.Hotkeys.MediaSeekBackwardId => "Seek backward",
        AppConstants.Hotkeys.MuteMicId => "Mute microphone",
        AppConstants.Hotkeys.MuteSoundId => "Mute output",
        AppConstants.Hotkeys.DeafenId => "Deafen",
        AppConstants.Hotkeys.OutputSwitchHotkeyId => "Switch output",
        AppConstants.Hotkeys.InputSwitchHotkeyId => "Switch input",
        AppConstants.Hotkeys.OutputReverseSwitchHotkeyId => "Previous output device",
        AppConstants.Hotkeys.InputReverseSwitchHotkeyId => "Previous input device",
        AppConstants.Hotkeys.ListenToInputHotkeyId => "Listen to input",
        AppConstants.Hotkeys.MasterVolumeUpHotkeyId => "Output volume up",
        AppConstants.Hotkeys.MasterVolumeDownHotkeyId => "Output volume down",
        AppConstants.Hotkeys.MicVolumeUpHotkeyId => "Microphone volume up",
        AppConstants.Hotkeys.MicVolumeDownHotkeyId => "Microphone volume down",
        AppConstants.Hotkeys.ForegroundVolumeUpHotkeyId => "Foreground app volume up",
        AppConstants.Hotkeys.ForegroundVolumeDownHotkeyId => "Foreground app volume down",
        AppConstants.Hotkeys.ForegroundMuteHotkeyId => "Mute foreground app",
        AppConstants.Hotkeys.PushToTalkHotkeyId => "Push-to-talk",
        AppConstants.Hotkeys.HoldToMuteHotkeyId => "Hold-to-mute",
        _ when actionId >= AppConstants.Hotkeys.RoutineHotkeyIdBase
            && actionId < AppConstants.Hotkeys.RoutineHotkeyIdBase + AppConstants.Hotkeys.RoutineHotkeyIdMaxCount => $"Routine shortcut ({actionId})",
        _ => $"Shortcut ({actionId})",
    };

    public static bool TryParseMainInput(string token, out HotkeySyntaxMainInput input)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            input = HotkeySyntaxMainInput.None;
            return false;
        }

        if (Enum.TryParse(token, true, out HotkeyKey key))
        {
            input = HotkeySyntaxMainInput.FromKeyboard(key);
            return key != HotkeyKey.None;
        }

        input = token.Trim().ToLowerInvariant() switch
        {
            "mouseleft" or "leftmouse" or "lmb" => HotkeySyntaxMainInput.FromMouseButton(HotkeyMouseButton.Left),
            "mouseright" or "rightmouse" or "rmb" => HotkeySyntaxMainInput.FromMouseButton(HotkeyMouseButton.Right),
            "mousemiddle" or "middlemouse" or "mmb" => HotkeySyntaxMainInput.FromMouseButton(HotkeyMouseButton.Middle),
            "mousex1" or "mouse4" or "xbutton1" or "x1" => HotkeySyntaxMainInput.FromMouseButton(HotkeyMouseButton.XButton1),
            "mousex2" or "mouse5" or "xbutton2" or "x2" => HotkeySyntaxMainInput.FromMouseButton(HotkeyMouseButton.XButton2),
            "wheelup" or "mousewheelup" or "scrollup" => HotkeySyntaxMainInput.WheelUp,
            "wheeldown" or "mousewheeldown" or "scrolldown" => HotkeySyntaxMainInput.WheelDown,
            "wheelleft" or "mousewheelleft" or "scrollleft" => HotkeySyntaxMainInput.WheelLeft,
            "wheelright" or "mousewheelright" or "scrollright" => HotkeySyntaxMainInput.WheelRight,
            _ => HotkeySyntaxMainInput.None,
        };

        return input.HasValue;
    }

    public static string NormalizeModifierName(HotkeyKey key) => key switch
    {
        HotkeyKey.LeftCtrl or HotkeyKey.RightCtrl => "Ctrl",
        HotkeyKey.LeftAlt or HotkeyKey.RightAlt => "Alt",
        HotkeyKey.LeftShift or HotkeyKey.RightShift => "Shift",
        HotkeyKey.LWin or HotkeyKey.RWin => "Win",
        _ => key.ToString(),
    };

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

    private static HotkeyKey? ParseModifierKey(string name)
    {
        if (Enum.TryParse(name, true, out HotkeyKey key) && IsModifier(key))
        {
            return key;
        }

        return ModifierAliases.TryGetValue(name, out HotkeyKey mappedModifier) ? mappedModifier : null;
    }

    private static HotkeySyntaxMainInput ParseMainInput(string name)
    {
        if (MainKeyAliases.TryGetValue(name, out HotkeyKey mappedMain))
        {
            return HotkeySyntaxMainInput.FromKeyboard(mappedMain);
        }

        if (Enum.TryParse(name, true, out HotkeyKey key))
        {
            return HotkeySyntaxMainInput.FromKeyboard(key);
        }

        HotkeySyntaxMainInput keyboardAlias = name.ToLowerInvariant() switch
        {
            "space" or "spacebar" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Space),
            "enter" or "return" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Enter),
            "esc" or "escape" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Escape),
            "tab" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Tab),
            "back" or "backspace" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Back),
            "del" or "delete" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Delete),
            "ins" or "insert" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Insert),
            "home" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Home),
            "end" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.End),
            "pgup" or "pageup" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.PageUp),
            "pgdn" or "pagedown" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.PageDown),
            "up" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Up),
            "down" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Down),
            "left" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Left),
            "right" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Right),
            "prtsc" or "printscreen" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.PrintScreen),
            "scroll" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Scroll),
            "pause" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Pause),
            "numlock" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumLock),
            "caps" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.CapsLock),
            "lwin" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.LWin),
            "rwin" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.RWin),
            "apps" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Apps),
            "f1" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F1),
            "f2" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F2),
            "f3" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F3),
            "f4" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F4),
            "f5" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F5),
            "f6" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F6),
            "f7" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F7),
            "f8" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F8),
            "f9" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F9),
            "f10" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F10),
            "f11" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F11),
            "f12" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F12),
            "f13" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F13),
            "f14" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F14),
            "f15" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F15),
            "f16" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F16),
            "f17" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F17),
            "f18" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F18),
            "f19" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F19),
            "f20" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F20),
            "f21" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F21),
            "f22" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F22),
            "f23" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F23),
            "f24" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.F24),
            "num0" or "numpad0" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad0),
            "num1" or "numpad1" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad1),
            "num2" or "numpad2" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad2),
            "num3" or "numpad3" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad3),
            "num4" or "numpad4" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad4),
            "num5" or "numpad5" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad5),
            "num6" or "numpad6" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad6),
            "num7" or "numpad7" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad7),
            "num8" or "numpad8" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad8),
            "num9" or "numpad9" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.NumPad9),
            "num*" or "multiply" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Multiply),
            "num+" or "add" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Add),
            "num-" or "subtract" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Subtract),
            "num." or "decimal" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Decimal),
            "num/" or "divide" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.Divide),
            "mute" or "volumemute" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.VolumeMute),
            "voldown" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.VolumeDown),
            "volup" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.VolumeUp),
            "next" or "medianext" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.MediaNextTrack),
            "prev" or "mediaprev" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.MediaPreviousTrack),
            "play" or "pause" or "mediaplaypause" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.MediaPlayPause),
            "stop" or "mediastop" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.MediaStop),
            "browserback" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.BrowserBack),
            "browserforward" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.BrowserForward),
            "refresh" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.BrowserRefresh),
            "browserstop" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.BrowserStop),
            "search" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.BrowserSearch),
            "favorites" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.BrowserFavorites),
            "browserhome" => HotkeySyntaxMainInput.FromKeyboard(HotkeyKey.BrowserHome),
            _ => HotkeySyntaxMainInput.None,
        };

        if (keyboardAlias.HasValue)
        {
            return keyboardAlias;
        }

        return TryParseMainInput(name, out HotkeySyntaxMainInput parsedInput)
            ? parsedInput
            : HotkeySyntaxMainInput.None;
    }

    private static bool IsModifier(HotkeyKey key) =>
        key is HotkeyKey.LeftCtrl or HotkeyKey.RightCtrl or HotkeyKey.LeftAlt or HotkeyKey.RightAlt or
            HotkeyKey.LeftShift or HotkeyKey.RightShift or HotkeyKey.LWin or HotkeyKey.RWin;

    private static int GetModifierOrder(string name) => name switch
    {
        "Ctrl" => 0,
        "Alt" => 1,
        "Shift" => 2,
        "Win" => 3,
        _ => 4,
    };

    private static IEnumerable<HotkeyKey> NormalizeModifiers(IEnumerable<HotkeyKey> modifiers)
    {
        bool hasCtrl = false;
        bool hasAlt = false;
        bool hasShift = false;
        bool hasWin = false;

        foreach (HotkeyKey modifier in modifiers)
        {
            switch (modifier)
            {
                case HotkeyKey.LeftCtrl:
                case HotkeyKey.RightCtrl:
                    hasCtrl = true;
                    break;
                case HotkeyKey.LeftAlt:
                case HotkeyKey.RightAlt:
                case HotkeyKey.System:
                    hasAlt = true;
                    break;
                case HotkeyKey.LeftShift:
                case HotkeyKey.RightShift:
                    hasShift = true;
                    break;
                case HotkeyKey.LWin:
                case HotkeyKey.RWin:
                    hasWin = true;
                    break;
            }
        }

        if (hasCtrl) yield return HotkeyKey.LeftCtrl;
        if (hasAlt) yield return HotkeyKey.LeftAlt;
        if (hasShift) yield return HotkeyKey.LeftShift;
        if (hasWin) yield return HotkeyKey.LWin;
    }

    private static string BuildWinArrowShortcutName(string direction, bool hasCtrl, bool hasShift, bool hasAlt)
    {
        List<string> parts = ["Win"];
        if (hasCtrl) parts.Add("Ctrl");
        if (hasShift) parts.Add("Shift");
        if (hasAlt) parts.Add("Alt");
        parts.Add(direction);
        return string.Join("+", parts);
    }
}

using System.Windows.Input;

namespace AudioPilot.Services.Hotkeys
{
    public enum HotkeyMainInputKind
    {
        None,
        Keyboard,
        MouseButton,
        MouseWheel,
    }

    public readonly record struct HotkeyMainInput
    {
        private HotkeyMainInput(HotkeyMainInputKind kind, Key key, MouseButton mouseButton, int wheelDirection)
        {
            Kind = kind;
            Key = key;
            MouseButton = mouseButton;
            WheelDirection = wheelDirection;
        }

        public HotkeyMainInputKind Kind { get; }

        public Key Key { get; }

        public MouseButton MouseButton { get; }

        public int WheelDirection { get; }

        public bool HasValue => Kind != HotkeyMainInputKind.None;

        public bool CanUseOsRegistration => Kind == HotkeyMainInputKind.Keyboard;

        public bool IsMouseInput => Kind is HotkeyMainInputKind.MouseButton or HotkeyMainInputKind.MouseWheel;

        public bool AllowsStandaloneWithoutModifierByDefault => Kind switch
        {
            HotkeyMainInputKind.Keyboard => IsStandaloneKeyboardKeyAllowed(Key),
            HotkeyMainInputKind.MouseButton or HotkeyMainInputKind.MouseWheel => false,
            _ => false,
        };

        public bool IsSupportedModifierCount(int modifierCount, IEnumerable<string>? configuredStandaloneKeys = null)
            => modifierCount > 0 || HotkeyStandaloneKeyPolicy.IsStandaloneWithoutModifierAllowed(this, configuredStandaloneKeys);

        public string SerializationToken => Kind switch
        {
            HotkeyMainInputKind.Keyboard => FormatKeyboardToken(Key),
            HotkeyMainInputKind.MouseButton => MouseButton switch
            {
                MouseButton.Left => "MouseLeft",
                MouseButton.Right => "MouseRight",
                MouseButton.Middle => "MouseMiddle",
                MouseButton.XButton1 => "MouseX1",
                MouseButton.XButton2 => "MouseX2",
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

        public string DisplayText => Kind switch
        {
            HotkeyMainInputKind.Keyboard => HotkeyDisplayFormatter.FormatMainInput(FormatKeyboardToken(Key)),
            HotkeyMainInputKind.MouseButton or HotkeyMainInputKind.MouseWheel =>
                HotkeyDisplayFormatter.FormatMainInput(SerializationToken),
            _ => string.Empty,
        };

        public uint KeyboardVirtualKey => Kind == HotkeyMainInputKind.Keyboard && Key != Key.None
            ? (uint)KeyInterop.VirtualKeyFromKey(Key)
            : 0;

        public static HotkeyMainInput None => default;

        public static HotkeyMainInput FromKeyboard(Key key) =>
            key == Key.None ? None : new HotkeyMainInput(HotkeyMainInputKind.Keyboard, key, default, 0);

        public static HotkeyMainInput FromMouseButton(MouseButton mouseButton) =>
            new(HotkeyMainInputKind.MouseButton, Key.None, mouseButton, 0);

        public static HotkeyMainInput WheelUp => new(HotkeyMainInputKind.MouseWheel, Key.None, default, 1);

        public static HotkeyMainInput WheelDown => new(HotkeyMainInputKind.MouseWheel, Key.None, default, -1);

        public static HotkeyMainInput WheelLeft => new(HotkeyMainInputKind.MouseWheel, Key.None, default, -2);

        public static HotkeyMainInput WheelRight => new(HotkeyMainInputKind.MouseWheel, Key.None, default, 2);

        public static bool TryParse(string token, out HotkeyMainInput input)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                input = None;
                return false;
            }

            if (Enum.TryParse(token, true, out Key key))
            {
                input = FromKeyboard(key);
                return key != Key.None;
            }

            input = token.Trim().ToLowerInvariant() switch
            {
                "mouseleft" or "leftmouse" or "lmb" => FromMouseButton(MouseButton.Left),
                "mouseright" or "rightmouse" or "rmb" => FromMouseButton(MouseButton.Right),
                "mousemiddle" or "middlemouse" or "mmb" => FromMouseButton(MouseButton.Middle),
                "mousex1" or "mouse4" or "xbutton1" or "x1" => FromMouseButton(MouseButton.XButton1),
                "mousex2" or "mouse5" or "xbutton2" or "x2" => FromMouseButton(MouseButton.XButton2),
                "wheelup" or "mousewheelup" or "scrollup" => WheelUp,
                "wheeldown" or "mousewheeldown" or "scrolldown" => WheelDown,
                "wheelleft" or "mousewheelleft" or "scrollleft" => WheelLeft,
                "wheelright" or "mousewheelright" or "scrollright" => WheelRight,
                _ => None,
            };

            return input.HasValue;
        }

        private static string FormatKeyboardToken(Key key)
        {
            return Constants.AppConstants.Hotkeys.MainKeyDisplayAliases.TryGetValue(key, out string? formatted)
                ? formatted
                : key.ToString();
        }

        private static bool IsStandaloneKeyboardKeyAllowed(Key key)
        {
            return key is >= Key.F1 and <= Key.F24 or
                Key.MediaPlayPause or
                Key.MediaNextTrack or
                Key.MediaPreviousTrack or
                Key.MediaStop or
                Key.VolumeMute or
                Key.VolumeUp or
                Key.VolumeDown;
        }
    }
}

using System.Windows.Input;
using AudioPilot.Services.Hotkeys;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Services.Hotkeys;

public sealed class HotkeyDisplayFormatterTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Multiply", "Ctrl+Alt+Num *")]
    [InlineData("Ctrl+Add", "Ctrl+Num +")]
    [InlineData("Alt+Subtract", "Alt+Num -")]
    [InlineData("Shift+Divide", "Shift+Num /")]
    [InlineData("Win+Decimal", "Win+Num .")]
    [InlineData("Ctrl+Return", "Ctrl+Enter")]
    [InlineData("Ctrl+Prior", "Ctrl+Page Up")]
    [InlineData("Ctrl+Next", "Ctrl+Page Down")]
    [InlineData("Ctrl+D7", "Ctrl+7")]
    [InlineData("Alt+NumPad4", "Alt+Num 4")]
    [InlineData("Ctrl+Back", "Ctrl+Backspace")]
    [InlineData("Ctrl+Capital", "Ctrl+Caps Lock")]
    [InlineData("Ctrl+Escape", "Ctrl+Esc")]
    [InlineData("Ctrl+Snapshot", "Ctrl+Print Screen")]
    [InlineData("Ctrl+Scroll", "Ctrl+Scroll Lock")]
    [InlineData("Ctrl+NumLock", "Ctrl+Num Lock")]
    [InlineData("Ctrl+Apps", "Ctrl+Menu")]
    [InlineData("Ctrl+MediaPlayPause", "Ctrl+Play/Pause")]
    [InlineData("Ctrl+MediaNextTrack", "Ctrl+Next Track")]
    [InlineData("Ctrl+MediaPreviousTrack", "Ctrl+Previous Track")]
    [InlineData("Ctrl+MediaStop", "Ctrl+Media Stop")]
    [InlineData("Ctrl+VolumeMute", "Ctrl+Volume Mute")]
    [InlineData("Ctrl+VolumeUp", "Ctrl+Volume Up")]
    [InlineData("Ctrl+VolumeDown", "Ctrl+Volume Down")]
    [InlineData("Ctrl+BrowserBack", "Ctrl+Browser Back")]
    [InlineData("Ctrl+BrowserForward", "Ctrl+Browser Forward")]
    [InlineData("Ctrl+BrowserRefresh", "Ctrl+Browser Refresh")]
    [InlineData("Ctrl+BrowserStop", "Ctrl+Browser Stop")]
    [InlineData("Ctrl+BrowserSearch", "Ctrl+Browser Search")]
    [InlineData("Ctrl+BrowserFavorites", "Ctrl+Browser Favorites")]
    [InlineData("Ctrl+BrowserHome", "Ctrl+Browser Home")]
    [InlineData("Ctrl+MouseLeft", "Ctrl+Left Click")]
    [InlineData("Ctrl+MouseRight", "Ctrl+Right Click")]
    [InlineData("Ctrl+MouseMiddle", "Ctrl+Middle Click")]
    [InlineData("Ctrl+MouseX1", "Ctrl+Mouse 4")]
    [InlineData("Ctrl+MouseX2", "Ctrl+Mouse 5")]
    [InlineData("Ctrl+WheelUp", "Ctrl+Wheel Up")]
    [InlineData("Ctrl+WheelDown", "Ctrl+Wheel Down")]
    [InlineData("Ctrl+WheelLeft", "Ctrl+Wheel Left")]
    [InlineData("Ctrl+WheelRight", "Ctrl+Wheel Right")]
    [InlineData("Control+Windows+RightCtrl+F8", "Ctrl+Win+Ctrl+F8")]
    [InlineData("Ctrl+LaunchMail", "Ctrl+Mail")]
    [InlineData("Ctrl+SelectMedia", "Ctrl+Media Select")]
    public void FormatCompact_UsesFriendlyMainInputNames(string canonical, string expected)
    {
        Assert.Equal(expected, HotkeyDisplayFormatter.FormatCompact(canonical, ResolveReferenceLayoutKey));
    }

    [Theory]
    [InlineData(" Ctrl + Alt + Multiply ", "Ctrl + Alt + Num *")]
    [InlineData("Ctrl+PageUp", "Ctrl + Page Up")]
    [InlineData("Ctrl+PageDown", "Ctrl + Page Down")]
    public void FormatSpaced_NormalizesPresentationWithoutChangingMeaning(string canonical, string expected)
    {
        Assert.Equal(expected, HotkeyDisplayFormatter.FormatSpaced(canonical, ResolveReferenceLayoutKey));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("Ctrl+F8", "Ctrl+F8")]
    [InlineData("Ctrl+Num *", "Ctrl+Num *")]
    [InlineData("Ctrl+Num +", "Ctrl+Num +")]
    [InlineData("Ctrl+FutureKey", "Ctrl+FutureKey")]
    [InlineData("Ctrl++", "Ctrl+=")]
    public void FormatCompact_PreservesBlankFriendlyAndUnknownValues(string? value, string expected)
    {
        Assert.Equal(expected, HotkeyDisplayFormatter.FormatCompact(value, ResolveReferenceLayoutKey));
    }

    [Fact]
    public void FormatSpaced_PreservesLiteralPlusMainInput()
    {
        Assert.Equal("Ctrl + =", HotkeyDisplayFormatter.FormatSpaced("Ctrl++", ResolveReferenceLayoutKey));
    }

    [Fact]
    public void FormatSpaced_PreservesAlreadyFriendlyNumpadPlus()
    {
        Assert.Equal("Ctrl + Num +", HotkeyDisplayFormatter.FormatSpaced("Ctrl+Num +", ResolveReferenceLayoutKey));
    }

    [Fact]
    public void HotkeyViewModel_UsesFriendlyDisplayButRetainsCanonicalSerialization()
    {
        var viewModel = new HotkeyViewModel();

        bool loaded = viewModel.LoadFromString("Ctrl+Alt+Multiply");

        Assert.True(loaded);
        Assert.Equal("Ctrl + Alt + Num *", viewModel.DisplayText);
        Assert.Equal("Ctrl+Alt+Multiply", viewModel.ToHotkeyString());
    }

    [Fact]
    public void HotkeyViewModel_LiteralPlusRoundTripsThroughCanonicalSerialization()
    {
        var original = new HotkeyViewModel();
        original.AddModifier(System.Windows.Input.Key.LeftCtrl);
        original.SetMain(HotkeyMainInput.FromKeyboard(System.Windows.Input.Key.OemPlus));

        string serialized = original.ToHotkeyString();
        var restored = new HotkeyViewModel();

        Assert.Equal("Ctrl++", serialized);
        Assert.True(restored.LoadFromString(serialized));
        Assert.Equal(HotkeyDisplayFormatter.FormatSpaced(serialized), restored.DisplayText);
        Assert.Equal(serialized, restored.ToHotkeyString());
    }

    [Theory]
    [InlineData("Ctrl++", "Ctrl + =")]
    [InlineData("Ctrl+Shift++", "Ctrl + Shift + =")]
    [InlineData("Ctrl+-", "Ctrl + -")]
    [InlineData("Ctrl+/", "Ctrl + /")]
    [InlineData("Ctrl+Oem102", "Ctrl + §")]
    [InlineData("Ctrl+D1", "Ctrl + 1")]
    [InlineData("Ctrl+Decimal", "Ctrl + Num .")]
    public void FormatSpaced_UsesOneLayoutResolverForEveryLayoutSensitiveKey(string canonical, string expected)
    {
        Assert.Equal(expected, HotkeyDisplayFormatter.FormatSpaced(canonical, ResolveReferenceLayoutKey));
    }

    private static string ResolveReferenceLayoutKey(Key key, string fallback)
    {
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Key.Decimal => "Num .",
            Key.OemPlus => "=",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Oem102 => "§",
            _ => fallback,
        };
    }
}

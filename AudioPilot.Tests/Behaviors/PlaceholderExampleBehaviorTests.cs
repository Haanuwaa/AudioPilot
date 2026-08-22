using System.Windows;
using AudioPilot.Behaviors;

namespace AudioPilot.Tests.Behaviors;

public sealed class PlaceholderExampleBehaviorTests
{
    [Fact]
    public void EnsureStableText_ReusesCachedPlaceholderForSameControl()
    {
        DependencyObject control = new();

        string first = TextBoxPlaceholderBehavior.EnsureStableText(control, "Alpha|Beta", null, static _ => "Alpha");
        TextBoxPlaceholderBehavior.SetText(control, string.Empty);

        string second = TextBoxPlaceholderBehavior.EnsureStableText(control, "Alpha|Beta", null, static _ => "Beta");

        Assert.Equal("Alpha", first);
        Assert.Equal("Alpha", second);
        Assert.Equal("Alpha", TextBoxPlaceholderBehavior.GetText(control));
    }

    [Fact]
    public void PickDeterministicOption_ReturnsSameSample_ForSameIdentityAcrossControls()
    {
        string first = PlaceholderExampleBehavior.PickDeterministicOption("Alpha|Beta|Gamma", "output-hotkey");
        string second = PlaceholderExampleBehavior.PickDeterministicOption("Alpha|Beta|Gamma", "output-hotkey");

        Assert.Equal(first, second);
    }

    [Fact]
    public void EnsureStableText_UsesIdentityForCrossLaunchStyleDeterminism()
    {
        DependencyObject firstControl = new();
        DependencyObject secondControl = new();

        string first = TextBoxPlaceholderBehavior.EnsureStableText(firstControl, "Alpha|Beta|Gamma", "play-pause-hotkey");
        string second = TextBoxPlaceholderBehavior.EnsureStableText(secondControl, "Alpha|Beta|Gamma", "play-pause-hotkey");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("Alt+MouseX1", "Alt + Mouse 4")]
    [InlineData("Ctrl+PageUp", "Ctrl + Page Up")]
    [InlineData("Alt+WheelDown", "Alt + Wheel Down")]
    public void EnsureStableText_FormatsSelectedHotkeyWithFriendlyLabels(
        string selectedOption,
        string expectedDisplayText)
    {
        DependencyObject control = new();

        string placeholder = TextBoxPlaceholderBehavior.EnsureStableText(
            control,
            selectedOption,
            pickDefaultOption: _ => selectedOption);

        Assert.Equal(expectedDisplayText, placeholder);
        Assert.Equal(expectedDisplayText, TextBoxPlaceholderBehavior.GetText(control));
    }

    [Fact]
    public void PickDeterministicOption_FallsBackToInjectedPicker_WhenIdentityMissing()
    {
        string placeholder = PlaceholderExampleBehavior.PickDeterministicOption("Alpha|Beta", null, static _ => "Beta");

        Assert.Equal("Beta", placeholder);
    }

    [Fact]
    public void PickDeterministicOption_PrefersLetterFamily_WhenAvailable()
    {
        string placeholder = PlaceholderExampleBehavior.PickDeterministicOption(
            "Ctrl+Alt+M|Alt+MouseX1|Alt+WheelDown",
            "mute-mic-hotkey",
            preferredFamily: "letter");

        Assert.Equal("Ctrl+Alt+M", placeholder);
    }

    [Fact]
    public void PickDeterministicOption_PrefersDirectionalNextFamily_WhenAvailable()
    {
        string placeholder = PlaceholderExampleBehavior.PickDeterministicOption(
            "Ctrl+Alt+Right|Ctrl+Shift+N|Alt+MouseX2",
            "next-track-hotkey",
            preferredFamily: "directional-next");

        Assert.True(new[] { "Ctrl+Alt+Right", "Ctrl+Shift+N", "Alt+MouseX2" }.Contains(placeholder));
        Assert.DoesNotContain("Left", placeholder);
    }

    [Fact]
    public void PickDeterministicOption_FallsBackToAllCandidates_WhenPreferredFamilyMissing()
    {
        string placeholder = PlaceholderExampleBehavior.PickDeterministicOption(
            "Alt+MouseX1|Alt+WheelDown",
            "fallback-hotkey",
            preferredFamily: "letter");

        Assert.True(new[] { "Alt+MouseX1", "Alt+WheelDown" }.Contains(placeholder));
    }

    [Fact]
    public void PickDeterministicOption_PrefersMouseMiddle_WhenAvailable()
    {
        string placeholder = PlaceholderExampleBehavior.PickDeterministicOption(
            "Ctrl+Alt+P|Ctrl+Shift+P|Alt+MouseMiddle",
            "play-pause-hotkey",
            preferredFamily: "mouse-middle");

        Assert.Equal("Alt+MouseMiddle", placeholder);
    }

    [Fact]
    public void PickDeterministicOption_PrefersMouseLeft_WhenAvailable()
    {
        string placeholder = PlaceholderExampleBehavior.PickDeterministicOption(
            "Ctrl+Shift+O|Alt+Shift+O|Alt+MouseLeft",
            "output-reverse-hotkey",
            preferredFamily: "mouse-left");

        Assert.Equal("Alt+MouseLeft", placeholder);
    }

    [Fact]
    public void PickDeterministicOption_PrefersMouseRight_WhenAvailable()
    {
        string placeholder = PlaceholderExampleBehavior.PickDeterministicOption(
            "Ctrl+Shift+I|Alt+Shift+I|Alt+MouseRight",
            "input-reverse-hotkey",
            preferredFamily: "mouse-right");

        Assert.Equal("Alt+MouseRight", placeholder);
    }

    [Fact]
    public void PickDeterministicOption_PrefersWheelUp_WhenAvailable()
    {
        string placeholder = PlaceholderExampleBehavior.PickDeterministicOption(
            "Ctrl+PageUp|Ctrl+Alt+Up|Alt+WheelUp",
            "master-volume-up-hotkey",
            preferredFamily: "wheel-up");

        Assert.Equal("Alt+WheelUp", placeholder);
    }
}

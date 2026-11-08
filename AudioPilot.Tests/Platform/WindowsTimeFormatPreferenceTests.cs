namespace AudioPilot.Tests.Platform;

public sealed class WindowsTimeFormatPreferenceTests
{
    [Theory]
    [InlineData("h:mm tt", "HH:mm:ss", false, "h:mm tt")]
    [InlineData("h:mm tt", "HH:mm:ss", true, "HH:mm:ss")]
    [InlineData("HH:mm", "h:mm:ss tt", false, "HH:mm")]
    [InlineData("HH:mm", "h:mm:ss tt", true, "h:mm:ss tt")]
    public void SelectEffectiveTimePattern_MatchesTaskbarSecondsMode(
        string shortTimePattern,
        string longTimePattern,
        bool showSeconds,
        string expected)
    {
        string actual = WindowsTimeFormatPreference.SelectEffectiveTimePattern(
            shortTimePattern,
            longTimePattern,
            showSeconds);

        Assert.Equal(expected, actual);
    }
}

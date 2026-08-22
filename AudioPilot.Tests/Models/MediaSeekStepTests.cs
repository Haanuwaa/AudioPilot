using System.Globalization;
using AudioPilot.Models;

namespace AudioPilot.Tests.Models;

public sealed class MediaSeekStepTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("90s", 90)]
    [InlineData("1.5m", 90)]
    [InlineData("1m30s", 90)]
    [InlineData("1:30", 90)]
    [InlineData(" 1 M 30 S ", 90)]
    [InlineData(".5m", 30)]
    [InlineData("0.25m", 15)]
    [InlineData("0m1s", 1)]
    [InlineData("0:01", 1)]
    [InlineData("3600s", 3600)]
    [InlineData("60m", 3600)]
    [InlineData("60:00", 3600)]
    public void ParsesUnitsIntoExactWholeSeconds(string input, int expected)
    {
        Assert.True(MediaSeekStep.TryParse(input, out int seconds));
        Assert.Equal(expected, seconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1m")]
    [InlineData("+1m")]
    [InlineData("1.5")]
    [InlineData("0.01m")]
    [InlineData("0.01666666666666666666666666666666666667m")]
    [InlineData("1e2m")]
    [InlineData("NaNm")]
    [InlineData("1,5m")]
    [InlineData("61m")]
    [InlineData("60m1s")]
    [InlineData("60:01")]
    [InlineData("1:60")]
    [InlineData("1:2:30")]
    [InlineData("1:5")]
    [InlineData("1ms")]
    [InlineData("1m60s")]
    [InlineData("1.5m30s")]
    [InlineData("9999999999999999999999999999")]
    public void RejectsAmbiguousFractionalOrOutOfRangeDurations(string? input)
    {
        Assert.False(MediaSeekStep.TryParse(input, out int seconds));
        Assert.Equal(0, seconds);
    }

    [Theory]
    [InlineData(10, "10s")]
    [InlineData(60, "1m")]
    [InlineData(90, "1m 30s")]
    [InlineData(3600, "60m")]
    public void DisplayRoundTripsAcrossCultures(int seconds, string expected)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string display = MediaSeekStep.Format(seconds);
            Assert.Equal(expected, display);
            Assert.True(MediaSeekStep.TryParse(display, out int parsed));
            Assert.Equal(seconds, parsed);
            Assert.True(MediaSeekStep.TryParse("1.5m", out int minutes));
            Assert.Equal(90, minutes);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}

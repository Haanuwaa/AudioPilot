using System.Globalization;
using AudioPilot.Models;

namespace AudioPilot.Tests.Models;

public sealed class MediaSeekStepTests
{
    [Theory]
    [InlineData("90", "90")]
    [InlineData("1.5", "1.5")]
    [InlineData("0.1s", "0.1")]
    [InlineData("100ms", "0.1")]
    [InlineData("0.4s", "0.4")]
    [InlineData("500ms", "0.5")]
    [InlineData("1.5s", "1.5")]
    [InlineData("1m 30.5s", "90.5")]
    [InlineData("1:30.5", "90.5")]
    [InlineData("0.01m", "0.6")]
    [InlineData("90s", "90")]
    [InlineData("1.5m", "90")]
    [InlineData("1m30s", "90")]
    [InlineData("1:30", "90")]
    [InlineData(" 1 M 30 S ", "90")]
    [InlineData(".5m", "30")]
    [InlineData("0.25m", "15")]
    [InlineData("0m1s", "1")]
    [InlineData("0:01", "1")]
    [InlineData("3600s", "3600")]
    [InlineData("60m", "3600")]
    [InlineData("60:00", "3600")]
    public void ParsesUnitsIntoTenthsOfASecond(string input, string expected)
    {
        Assert.True(MediaSeekStep.TryParse(input, out decimal seconds));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), seconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1m")]
    [InlineData("+1m")]
    [InlineData("1.")]
    [InlineData("1.55")]
    [InlineData("0.09s")]
    [InlineData("99ms")]
    [InlineData("550ms")]
    [InlineData("0.01666666666666666666666666666666666667m")]
    [InlineData("1e2m")]
    [InlineData("NaNm")]
    [InlineData("1,5m")]
    [InlineData("61m")]
    [InlineData("60m1s")]
    [InlineData("60:00.1")]
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
        Assert.False(MediaSeekStep.TryParse(input, out decimal seconds));
        Assert.Equal(0, seconds);
    }

    [Theory]
    [InlineData(10, "10s")]
    [InlineData(0.5, "0.5s")]
    [InlineData(90.5, "1m 30.5s")]
    [InlineData(60, "1m")]
    [InlineData(90, "1m 30s")]
    [InlineData(3600, "60m")]
    public void DisplayRoundTripsAcrossCultures(double seconds, string expected)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string display = MediaSeekStep.Format((decimal)seconds);
            Assert.Equal(expected, display);
            Assert.True(MediaSeekStep.TryParse(display, out decimal parsed));
            Assert.Equal((decimal)seconds, parsed);
            Assert.True(MediaSeekStep.TryParse("1.5m", out decimal minutes));
            Assert.Equal(90, minutes);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}

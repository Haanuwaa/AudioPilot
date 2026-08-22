using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppIconImageProviderTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    public void NativeTrayIconSelectsEmbeddedSizeWithoutWpfReencoding(int pixels)
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var stream = AppIconImageProvider.OpenIconStream();
            using var icon = new System.Drawing.Icon(stream, pixels, pixels);
            Assert.Equal(pixels, icon.Width);
            Assert.Equal(pixels, icon.Height);
            Assert.NotEqual(0, icon.Handle);
        });
    }

}

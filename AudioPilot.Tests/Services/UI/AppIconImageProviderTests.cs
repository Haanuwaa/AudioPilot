using System.Windows.Media.Imaging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppIconImageProviderTests
{
    [Fact]
    public void GetSharedIconFrameForDpi_ReturnsSameFrozenBitmapFrameForSameScale()
    {
        TestExecutionGuards.RunSta(() =>
        {
            BitmapFrame first = AppIconImageProvider.GetSharedIconFrameForDpi();
            BitmapFrame second = AppIconImageProvider.GetSharedIconFrameForDpi();

            Assert.Same(first, second);
            Assert.True(first.IsFrozen);
        });
    }

    [Fact]
    public void GetSharedIconFrameForDpi_SelectsFrameClosestToRequestedDpiScale()
    {
        TestExecutionGuards.RunSta(() =>
        {
            BitmapFrame standardDpi = AppIconImageProvider.GetSharedIconFrameForDpi(1.0);
            BitmapFrame highDpi = AppIconImageProvider.GetSharedIconFrameForDpi(2.0);

            Assert.Equal(16, standardDpi.PixelWidth);
            Assert.Equal(32, highDpi.PixelWidth);
        });
    }
}

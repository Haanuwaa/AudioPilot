using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppIconImageProviderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(2)]
    [InlineData(16)]
    public void SharedIconRemainsUsableAfterItsCreatingStaShutsDown(double scale)
    {
        BitmapFrame? frame = null;
        byte[]? expected = null;
        TestExecutionGuards.RunSta(() =>
        {
            frame = AppIconImageProvider.GetSharedIconFrameForDpi(scale);
            expected = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            frame.CopyPixels(expected, frame.PixelWidth * 4, 0);
        });
        TestExecutionGuards.RunSta(() =>
        {
            BitmapFrame shared = Assert.IsType<BitmapFrame>(frame, exactMatch: false);
            Assert.True(shared.IsFrozen);
            byte[] actual = new byte[shared.PixelWidth * shared.PixelHeight * 4];
            shared.CopyPixels(actual, shared.PixelWidth * 4, 0);
            Assert.Equal(expected, actual);
            var window = new Window { Icon = shared };
            try { Assert.NotEqual(0, new WindowInteropHelper(window).EnsureHandle()); }
            finally { window.Close(); }
        });
    }

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

    [Theory]
    [InlineData(1.25, 20)]
    [InlineData(1.5, 24)]
    [InlineData(3, 48)]
    [InlineData(16, 256)]
    public void OriginalIconIncludesNativeFramesForFractionalAndHighDpi(double scale, int pixels)
    {
        TestExecutionGuards.RunSta(() =>
            Assert.Equal(pixels, AppIconImageProvider.GetSharedIconFrameForDpi(scale).PixelWidth));
    }

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

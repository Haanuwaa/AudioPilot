using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    [Fact]
    public async Task NativeTitleBarAndTaskbarIconsKeepFullSizedArtworkAcrossHideAndRestore()
    {
        await WithWindow(AppTheme.Dark, async (window, _) =>
        {
            for (int cycle = 0; cycle < 3; cycle++)
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                nint handle = new WindowInteropHelper(window).Handle;
                for (int kind = 0; kind < 2; kind++)
                {
                    nint icon = GetWindowIcon(handle, 0x007F, kind, 0);
                    Assert.NotEqual(0, icon);
                    BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    var pixels = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                    int stride = pixels.PixelWidth * 4;
                    byte[] data = new byte[stride * pixels.PixelHeight];
                    pixels.CopyPixels(data, stride, 0);
                    int left = pixels.PixelWidth, top = pixels.PixelHeight, right = -1, bottom = -1;
                    for (int y = 0; y < pixels.PixelHeight; y++)
                        for (int x = 0; x < pixels.PixelWidth; x++)
                            if (data[y * stride + x * 4 + 3] >= 64)
                            {
                                left = Math.Min(left, x); right = Math.Max(right, x);
                                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                            }
                    Assert.True(right - left + 1 >= pixels.PixelWidth * 0.8 && bottom - top + 1 >= pixels.PixelHeight * 0.8,
                        $"cycle={cycle}, kind={kind}, bitmap={pixels.PixelWidth}x{pixels.PixelHeight}, content={right - left + 1}x{bottom - top + 1}");
                }
                window.Hide();
            }
        });
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint GetWindowIcon(nint handle, uint message, nint kind, nint unused);
}

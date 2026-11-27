using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioPilot.Controls;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Controls;

public sealed class AudioLevelMeterRenderingTests
{
    [Fact]
    public void InactiveSegmentsRedrawWithCurrentBrushAndGeometry()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var meter = new AudioLevelMeter { InactiveBrush = Brushes.Gray, LowLevelBrush = Brushes.Green, Level = 35 };
            byte[] initial = Render(meter, 180);

            meter.InactiveBrush = Brushes.Red;
            byte[] recolored = Render(meter, 180);
            Assert.False(initial.SequenceEqual(recolored));

            meter.SegmentGap = 3;
            meter.SegmentCount = 16;
            var fresh = new AudioLevelMeter
            {
                InactiveBrush = Brushes.Red,
                LowLevelBrush = Brushes.Green,
                Level = 35,
                SegmentGap = 3,
                SegmentCount = 16,
            };
            Assert.Equal(Render(fresh, 240), Render(meter, 240));
        });
    }

    [Fact]
    public void ActiveGradientAndSegmentLockedPeakRenderDistinctPixels()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var meter = new AudioLevelMeter
            {
                InactiveBrush = Brushes.DimGray,
                LowLevelBrush = Brushes.LimeGreen,
                MidLevelBrush = Brushes.Gold,
                HighLevelBrush = Brushes.Crimson,
                PeakBrush = Brushes.White,
                SegmentCount = 24,
                SegmentGap = 1.5,
                Level = 0,
                Peak = 0,
            };

            byte[] idle = Render(meter, 200);
            meter.Level = 68;
            byte[] partialActive = Render(meter, 200);
            meter.Peak = 92;
            byte[] withPeak = Render(meter, 200);

            Assert.False(idle.SequenceEqual(partialActive));
            Assert.False(partialActive.SequenceEqual(withPeak));
        });
    }

    private static byte[] Render(AudioLevelMeter meter, int width)
    {
        const int height = 16;
        meter.Measure(new Size(width, height));
        meter.Arrange(new Rect(0, 0, width, height));
        meter.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(meter);
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}

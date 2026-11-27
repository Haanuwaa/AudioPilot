using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioPilot.Helpers;

/// <summary>
/// WPF image decoding and alpha-trim normalization for packaged app logos.
/// Kept in the WPF app project so <see cref="AudioDeviceHelper"/> stays WPF-free.
/// </summary>
internal static class PackagedAppIconDecoder
{
    internal static BitmapSource? Decode(byte[] iconBytes)
    {
        const int maximumSourceDimension = 4096;
        const long maximumSourcePixels = 4 * 1024 * 1024;
        const int maximumDecodedDimension = 64;
        if (iconBytes.Length == 0 || iconBytes.Length > 2 * 1024 * 1024) return null;

        using var stream = new MemoryStream(iconBytes, writable: false);
        // Read dimensions before allocating or normalizing pixels. Compressed size alone
        // does not bound decoded memory, and a width limit leaves portrait height unbounded.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        BitmapFrame frame = decoder.Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > maximumSourceDimension || height > maximumSourceDimension
            || (long)width * height > maximumSourcePixels) return null;

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, maximumDecodedDimension);
        else bitmap.DecodePixelHeight = Math.Min(height, maximumDecodedDimension);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        if (bitmap.PixelWidth > maximumDecodedDimension || bitmap.PixelHeight > maximumDecodedDimension
            || (long)bitmap.PixelWidth * bitmap.PixelHeight > maximumDecodedDimension * maximumDecodedDimension) return null;

        BitmapSource normalized = Normalize(bitmap);
        var converted = new FormatConvertedBitmap(normalized, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        BitmapSource detached = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            96, 96, PixelFormats.Bgra32, null, pixels, stride);
        detached.Freeze();
        return detached;
    }

    internal static BitmapSource NormalizeForTests(BitmapSource source) => Normalize(source);

    private static BitmapSource Normalize(BitmapSource source)
    {
        const byte visibleAlphaThreshold = 12;
        const double visibleContentPaddingRatio = 0.10;
        const double maximumScale = 2.5;

        if (source.PixelWidth <= 1 || source.PixelHeight <= 1)
        {
            return source;
        }

        BitmapSource pixelSource = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int stride = checked(pixelSource.PixelWidth * 4);
        var pixels = new byte[checked(stride * pixelSource.PixelHeight)];
        pixelSource.CopyPixels(pixels, stride, 0);

        int minX = pixelSource.PixelWidth;
        int minY = pixelSource.PixelHeight;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < pixelSource.PixelHeight; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < pixelSource.PixelWidth; x++)
            {
                if (pixels[rowOffset + (x * 4) + 3] <= visibleAlphaThreshold)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return source;
        }

        int contentWidth = maxX - minX + 1;
        int contentHeight = maxY - minY + 1;
        int minimumCropWidth = Math.Min(pixelSource.PixelWidth, (int)Math.Ceiling(pixelSource.PixelWidth / maximumScale));
        int minimumCropHeight = Math.Min(pixelSource.PixelHeight, (int)Math.Ceiling(pixelSource.PixelHeight / maximumScale));
        int desiredCropWidth = Math.Max(
            minimumCropWidth,
            contentWidth + (int)Math.Ceiling(contentWidth * visibleContentPaddingRatio * 2));
        int desiredCropHeight = Math.Max(
            minimumCropHeight,
            contentHeight + (int)Math.Ceiling(contentHeight * visibleContentPaddingRatio * 2));

        int cropWidth = Math.Min(pixelSource.PixelWidth, desiredCropWidth);
        int cropHeight = Math.Min(pixelSource.PixelHeight, desiredCropHeight);
        int cropX = Math.Clamp(minX - ((cropWidth - contentWidth) / 2), 0, pixelSource.PixelWidth - cropWidth);
        int cropY = Math.Clamp(minY - ((cropHeight - contentHeight) / 2), 0, pixelSource.PixelHeight - cropHeight);
        if (cropX == 0 && cropY == 0 && cropWidth == pixelSource.PixelWidth && cropHeight == pixelSource.PixelHeight)
        {
            return source;
        }

        var cropped = new CroppedBitmap(source, new Int32Rect(cropX, cropY, cropWidth, cropHeight));
        cropped.Freeze();
        return cropped;
    }
}

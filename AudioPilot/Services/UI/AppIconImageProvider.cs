using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioPilot.Services.UI
{
    internal static class AppIconImageProvider
    {
        private const string ResourceUri = "pack://application:,,,/AudioPilot;component/Images/audiopilot.ico";
        private static readonly Lock Sync = new();
        private static volatile BitmapFrame[]? _iconFrames;
        private static readonly ConcurrentDictionary<int, BitmapFrame> CachedIconsByPixelSize = new();

        public static BitmapFrame GetSharedIconFrameForDpi(double dpiScale = 1.0)
        {
            BitmapFrame[] frames = GetDecodedFrames();
            int targetPixelSize = GetTargetPixelSize(dpiScale);
            return CachedIconsByPixelSize.GetOrAdd(targetPixelSize, size =>
                frames.FirstOrDefault(frame => frame.PixelWidth >= size) ?? frames[^1]);
        }

        internal static Stream OpenIconStream()
        {
            RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
            return Application.GetResourceStream(new Uri(ResourceUri, UriKind.Absolute))?.Stream
                ?? throw new InvalidDataException("Embedded application icon is unavailable.");
        }

        /// <summary>Copies decoded pixels once so shared frames do not retain a thread-affine decoder.</summary>
        private static BitmapFrame[] GetDecodedFrames()
        {
            if (_iconFrames != null)
            {
                return _iconFrames;
            }

            lock (Sync)
            {
                if (_iconFrames != null)
                {
                    return _iconFrames;
                }

                using Stream stream = OpenIconStream();
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                BitmapFrame[] frames = [.. decoder.Frames
                    .OrderBy(frame => frame.PixelWidth)
                    .Select(DetachFrame)];

                if (frames.Length == 0)
                {
                    throw new InvalidDataException("Embedded application icon did not contain any frames.");
                }

                _iconFrames = frames;
                return frames;
            }
        }

        private static BitmapFrame DetachFrame(BitmapFrame frame)
        {
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            int stride = checked(frame.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * frame.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            BitmapSource source = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight,
                frame.DpiX, frame.DpiY, PixelFormats.Pbgra32, null, pixels, stride);
            source.Freeze();
            BitmapFrame detached = BitmapFrame.Create(source);
            detached.Freeze();
            return detached;
        }

        private static int GetTargetPixelSize(double dpiScale)
        {
            double sanitizedScale = double.IsFinite(dpiScale) && dpiScale > 0
                ? dpiScale
                : 1.0;

            return Math.Max(16, (int)Math.Round(16 * sanitizedScale, MidpointRounding.AwayFromZero));
        }

        internal static void ClearCache()
        {
            CachedIconsByPixelSize.Clear();
        }
    }
}

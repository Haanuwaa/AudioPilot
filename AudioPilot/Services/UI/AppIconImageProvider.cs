using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Resources;

namespace AudioPilot.Services.UI
{
    internal static class AppIconImageProvider
    {
        private static readonly Lock Sync = new();
        private static BitmapFrame[]? _iconFrames;
        private static readonly ConcurrentDictionary<int, BitmapFrame> CachedIconsByPixelSize = new();

        public static BitmapFrame GetSharedIconFrameForDpi(double dpiScale = 1.0)
        {
            BitmapFrame[] frames = GetDecodedFrames();
            int targetPixelSize = GetTargetPixelSize(dpiScale);
            return CachedIconsByPixelSize.GetOrAdd(targetPixelSize, _ => SelectBestFrame(frames, targetPixelSize));
        }

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

                StreamResourceInfo resource = Application.GetResourceStream(
                    new Uri("/AudioPilot;component/Images/sound.ico", UriKind.Relative))
                    ?? throw new FileNotFoundException("Embedded application icon resource was not found.");

                using Stream iconStream = resource.Stream;
                var decoder = new IconBitmapDecoder(
                    iconStream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                BitmapFrame[] frames = [.. decoder.Frames
                    .OrderBy(frame => frame.PixelWidth)
                    .Select(FreezeFrame)];

                if (frames.Length == 0)
                {
                    throw new InvalidDataException("Embedded application icon did not contain any frames.");
                }

                _iconFrames = frames;
                return frames;
            }
        }

        private static BitmapFrame FreezeFrame(BitmapFrame frame)
        {
            if (!frame.IsFrozen)
            {
                frame.Freeze();
            }

            return frame;
        }

        private static int GetTargetPixelSize(double dpiScale)
        {
            double sanitizedScale = double.IsFinite(dpiScale) && dpiScale > 0
                ? dpiScale
                : 1.0;

            return Math.Max(16, (int)Math.Round(16 * sanitizedScale, MidpointRounding.AwayFromZero));
        }

        private static BitmapFrame SelectBestFrame(BitmapFrame[] frames, int targetPixelSize)
        {
            BitmapFrame? selectedFrame = frames
                .FirstOrDefault(frame => frame.PixelWidth >= targetPixelSize)
                ?? frames[^1];

            return selectedFrame;
        }

        internal static void ClearCache()
        {
            CachedIconsByPixelSize.Clear();
        }
    }
}

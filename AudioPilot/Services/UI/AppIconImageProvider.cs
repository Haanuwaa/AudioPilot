using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AudioPilot.Services.UI
{
    internal static class AppIconImageProvider
    {
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

        /// <summary>Reuses WPF's URI decoder cache and initializes pack resources even before an Application instance exists.</summary>
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

                RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
                var decoder = BitmapDecoder.Create(
                    new Uri("pack://application:,,,/AudioPilot;component/Images/sound.ico", UriKind.Absolute),
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

        internal static void ClearCache()
        {
            CachedIconsByPixelSize.Clear();
        }
    }
}

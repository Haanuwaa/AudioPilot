using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Services.UI.Interop;

namespace AudioPilot.Services.UI
{
    /// <summary>
    /// Snapshot of the emoji renderer's cache and timing counters for tests and diagnostics.
    /// </summary>
    internal readonly record struct ColorEmojiRenderMetricsSnapshot(
        int RequestCount,
        int CacheHitCount,
        int RenderCount,
        int EvictionCount,
        double TotalRenderMs,
        double MaxRenderMs,
        int CacheSize,
        int FailureCount);

    /// <summary>
    /// Renders individual emoji into frozen bitmap sources so overlay text brushes do not tint them.
    /// </summary>
    internal sealed class ColorEmojiImageSourceFactory : IOverlayEmojiImageSourceFactory
    {
        private const string EmojiFontFamilyName = "Segoe UI Emoji";
        private const double BitmapPaddingDip = 2d;
        private const int MaxCacheEntries = AppConstants.Overlay.EmojiRenderCacheMaxEntries;
        private const int MaxEmojiLength = 128;
        private const int MaxBitmapDimension = 1024;
        private const int MaxBitmapPixels = 262144;
        private static readonly TimeSpan _failureRetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan _diagnosticsWindow = TimeSpan.FromSeconds(AppConstants.Timing.SessionDiagnosticsSummaryWindowSeconds);
        private readonly Lock _cacheLock = new();
        private readonly Dictionary<EmojiRenderCacheKey, CacheEntry> _cache = [];
        private readonly LinkedList<EmojiRenderCacheKey> _cacheLru = [];
        private readonly Lock _metricsLock = new();
        private readonly ILogger _logger;
        private readonly Func<string, double, double, BitmapSource?> _renderer;
        private readonly TimeProvider _timeProvider = TimeProvider.System;
        private DateTime _metricsWindowStartUtc = DateTime.UtcNow;
        private int _metricsRequestCount;
        private int _metricsCacheHitCount;
        private int _metricsRenderCount;
        private int _metricsEvictionCount;
        private int _metricsFailureCount;
        private string? _lastRenderFailure;
        private long? _lastFailureLogTimestamp;
        private double _metricsRenderTotalMs;
        private double _metricsRenderMaxMs;

        internal string? LastRenderFailureForTests => _lastRenderFailure;
        internal static int MaxCacheEntriesForTests => MaxCacheEntries;

        public ColorEmojiImageSourceFactory()
        {
            _renderer = TryRender;
            _logger = Logger.Instance;
        }

        internal ColorEmojiImageSourceFactory(Func<string, double, double, BitmapSource?> renderer, ILogger? logger = null, TimeProvider? timeProvider = null)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _logger = logger ?? Logger.Instance;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Bounds native bitmap work and briefly caches failures so layout cannot retry a broken renderer
        /// on every frame. Failed entries share the bounded LRU and expire to allow recovery.
        /// </summary>
        public ImageSource? Create(string emoji, double fontSize, double pixelsPerDip)
        {
            if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > MaxEmojiLength
                || !double.IsFinite(fontSize) || !double.IsFinite(pixelsPerDip)
                || fontSize <= 0 || pixelsPerDip <= 0)
            {
                return null;
            }

            double clampedFontSize = Math.Max(1d, fontSize);
            double clampedPixelsPerDip = Math.Max(1d, pixelsPerDip);
            EmojiRenderCacheKey cacheKey = new(emoji, Math.Round(clampedFontSize, 2), Math.Round(clampedPixelsPerDip, 2));
            if (TryGetCachedSource(cacheKey, out ImageSource? cached))
            {
                RecordRequestMetric(cacheHit: true, renderMs: 0d, evictionCountDelta: 0);
                return cached;
            }

            Stopwatch renderStopwatch = Stopwatch.StartNew();
            BitmapSource? rendered = _renderer(emoji, cacheKey.FontSize, cacheKey.PixelsPerDip);
            renderStopwatch.Stop();

            if (rendered?.CanFreeze == true)
            {
                rendered.Freeze();
            }

            ImageSource? cachedResult = CacheRenderedSource(cacheKey, rendered, out int evictionCountDelta);
            RecordRequestMetric(cacheHit: false, renderStopwatch.Elapsed.TotalMilliseconds, evictionCountDelta, renderFailed: rendered is null);
            return cachedResult;
        }

        internal int CachedEntryCountForTests
        {
            get
            {
                lock (_cacheLock)
                {
                    return _cache.Count;
                }
            }
        }

        internal ColorEmojiRenderMetricsSnapshot GetMetricsSnapshotForTests()
        {
            int cacheSize;
            lock (_cacheLock)
            {
                cacheSize = _cache.Count;
            }

            lock (_metricsLock)
            {
                return new ColorEmojiRenderMetricsSnapshot(
                    _metricsRequestCount,
                    _metricsCacheHitCount,
                    _metricsRenderCount,
                    _metricsEvictionCount,
                    _metricsRenderTotalMs,
                    _metricsRenderMaxMs,
                    cacheSize,
                    _metricsFailureCount);
            }
        }

        private bool TryGetCachedSource(EmojiRenderCacheKey cacheKey, out ImageSource? cached)
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(cacheKey, out CacheEntry? entry))
                {
                    cached = null;
                    return false;
                }

                if (entry.Source is null && _timeProvider.GetElapsedTime(entry.CreatedTimestamp) >= _failureRetryDelay)
                {
                    _cache.Remove(cacheKey);
                    _cacheLru.Remove(entry.Node);
                    cached = null;
                    return false;
                }

                TouchCacheEntry(entry);
                cached = entry.Source;
                return true;
            }
        }

        private ImageSource? CacheRenderedSource(EmojiRenderCacheKey cacheKey, ImageSource? rendered, out int evictionCountDelta)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(cacheKey, out CacheEntry? existing))
                {
                    if (existing.Source is not null || rendered is null)
                    {
                        TouchCacheEntry(existing);
                        evictionCountDelta = 0;
                        return existing.Source;
                    }

                    _cacheLru.Remove(existing.Node);
                }

                LinkedListNode<EmojiRenderCacheKey> node = _cacheLru.AddLast(cacheKey);
                _cache[cacheKey] = new CacheEntry(rendered, node, _timeProvider.GetTimestamp());
                evictionCountDelta = TrimCacheIfNeeded();
                return rendered;
            }
        }

        private int TrimCacheIfNeeded()
        {
            int evictions = 0;
            while (_cache.Count > MaxCacheEntries && _cacheLru.First is LinkedListNode<EmojiRenderCacheKey> oldestNode)
            {
                _cacheLru.RemoveFirst();
                _cache.Remove(oldestNode.Value);
                evictions++;
            }

            return evictions;
        }

        private void TouchCacheEntry(CacheEntry entry)
        {
            if (entry.Node.List != _cacheLru || entry.Node == _cacheLru.Last)
            {
                return;
            }

            _cacheLru.Remove(entry.Node);
            _cacheLru.AddLast(entry.Node);
        }

        private void RecordRequestMetric(bool cacheHit, double renderMs, int evictionCountDelta, bool renderFailed = false)
        {
            lock (_metricsLock)
            {
                _metricsRequestCount++;
                _metricsEvictionCount += evictionCountDelta;
                if (renderFailed)
                {
                    _metricsFailureCount++;
                    if (!_lastFailureLogTimestamp.HasValue || _timeProvider.GetElapsedTime(_lastFailureLogTimestamp.Value) >= _diagnosticsWindow)
                    {
                        _lastFailureLogTimestamp = _timeProvider.GetTimestamp();
                        _logger.Warning("OverlayWindow", $"overlay-emoji-render-failed | fallback=text retryAfterSeconds={_failureRetryDelay.TotalSeconds:F0} reason={_lastRenderFailure ?? "no-bitmap"}");
                    }
                }

                if (cacheHit)
                {
                    _metricsCacheHitCount++;
                }
                else
                {
                    _metricsRenderCount++;
                    _metricsRenderTotalMs += renderMs;
                    _metricsRenderMaxMs = Math.Max(_metricsRenderMaxMs, renderMs);
                }

                DateTime now = DateTime.UtcNow;
                TimeSpan windowElapsed = now - _metricsWindowStartUtc;
                if (windowElapsed < _diagnosticsWindow)
                {
                    return;
                }

                if (_metricsRequestCount > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    int cacheSize;
                    lock (_cacheLock)
                    {
                        cacheSize = _cache.Count;
                    }

                    double avgRenderMs = _metricsRenderCount == 0 ? 0d : _metricsRenderTotalMs / _metricsRenderCount;
                    double hitRate = (_metricsCacheHitCount * 100d) / _metricsRequestCount;
                    _logger.Debug(
                        "OverlayWindow",
                        () => $"{AppConstants.Audio.LogEvents.Diagnostics.OverlayEmojiRenderDiagnostics} | requests={_metricsRequestCount} cacheHits={_metricsCacheHitCount} hitRate={hitRate:F1} renders={_metricsRenderCount} failures={_metricsFailureCount} avgRenderMs={avgRenderMs:F1} maxRenderMs={_metricsRenderMaxMs:F1} evictions={_metricsEvictionCount} cacheSize={cacheSize} windowSeconds={windowElapsed.TotalSeconds:F0}");
                }

                _metricsWindowStartUtc = now;
                _metricsRequestCount = 0;
                _metricsCacheHitCount = 0;
                _metricsRenderCount = 0;
                _metricsEvictionCount = 0;
                _metricsFailureCount = 0;
                _metricsRenderTotalMs = 0d;
                _metricsRenderMaxMs = 0d;
            }
        }

        private BitmapSource? TryRender(string emoji, double fontSize, double pixelsPerDip)
        {
            _lastRenderFailure = null;
            string stage = "start";
            ColorEmojiComHandle<IColorEmojiWicImagingFactory>? wicFactory = null;
            ColorEmojiComHandle<IColorEmojiWicBitmap>? wicBitmap = null;
            ColorEmojiComHandle<IColorEmojiD2DFactory>? d2dFactory = null;
            ColorEmojiComHandle<IColorEmojiD2DRenderTarget>? renderTarget = null;
            IntPtr textBrushPointer = IntPtr.Zero;
            ColorEmojiComHandle<IColorEmojiDWriteFactory>? dwriteFactory = null;
            ColorEmojiComHandle<IColorEmojiDWriteTextFormat>? textFormat = null;

            try
            {
                stage = "measure";
                Size measuredSize = MeasureEmoji(emoji, fontSize, pixelsPerDip);
                double paddedWidthDip = measuredSize.Width + (BitmapPaddingDip * 2d);
                double paddedHeightDip = measuredSize.Height + (BitmapPaddingDip * 2d);
                double pixelWidth = Math.Ceiling(paddedWidthDip * pixelsPerDip);
                double pixelHeight = Math.Ceiling(paddedHeightDip * pixelsPerDip);
                if (!double.IsFinite(pixelWidth) || !double.IsFinite(pixelHeight)
                    || pixelWidth > MaxBitmapDimension || pixelHeight > MaxBitmapDimension
                    || pixelWidth * pixelHeight > MaxBitmapPixels)
                {
                    _lastRenderFailure = "bitmap-size-limit";
                    return null;
                }

                int width = Math.Max(1, (int)pixelWidth);
                int height = Math.Max(1, (int)pixelHeight);

                stage = "create-wic-factory";
                wicFactory = ColorEmojiWicInterop.CreateFactory();

                stage = "create-wic-bitmap";
                Guid pixelFormat = ColorEmojiWicInterop.PixelFormat32bppPbgra;
                wicFactory.Interface.CreateBitmap(
                    (uint)width,
                    (uint)height,
                    ref pixelFormat,
                    ColorEmojiWicBitmapCreateCacheOption.CacheOnLoad,
                    out IntPtr wicBitmapPointer);
                wicBitmap = ColorEmojiComRuntime.Wrap<IColorEmojiWicBitmap>(wicBitmapPointer);

                stage = "create-d2d-factory";
                d2dFactory = ColorEmojiD2DInterop.CreateFactory();

                ColorEmojiD2DRenderTargetProperties renderTargetProperties = new()
                {
                    Type = ColorEmojiD2DRenderTargetType.Default,
                    PixelFormat = new ColorEmojiD2DPixelFormat
                    {
                        Format = ColorEmojiDxgiFormat.Unknown,
                        AlphaMode = ColorEmojiD2DAlphaMode.Unknown
                    },
                    DpiX = 96f * (float)pixelsPerDip,
                    DpiY = 96f * (float)pixelsPerDip,
                    Usage = ColorEmojiD2DRenderTargetUsage.None,
                    MinLevel = ColorEmojiD2DFeatureLevel.Default
                };

                stage = "create-wic-render-target";
                d2dFactory.Interface.CreateWicBitmapRenderTarget(wicBitmap.Pointer, ref renderTargetProperties, out IntPtr renderTargetPointer);
                renderTarget = ColorEmojiComRuntime.Wrap<IColorEmojiD2DRenderTarget>(renderTargetPointer);

                stage = "create-dwrite-factory";
                dwriteFactory = ColorEmojiDWriteInterop.CreateFactory();

                stage = "create-text-format";
                dwriteFactory.Interface.CreateTextFormat(
                    EmojiFontFamilyName,
                    IntPtr.Zero,
                    ColorEmojiDWriteFontWeight.Regular,
                    ColorEmojiDWriteFontStyle.Normal,
                    ColorEmojiDWriteFontStretch.Normal,
                    (float)fontSize,
                    CultureInfo.CurrentUICulture.Name,
                    out IntPtr textFormatPointer);
                textFormat = ColorEmojiComRuntime.Wrap<IColorEmojiDWriteTextFormat>(textFormatPointer);

                stage = "configure-text-format";
                ArgumentNullException.ThrowIfNull(textFormat);
                ArgumentNullException.ThrowIfNull(renderTarget);
                textFormat.Interface.SetTextAlignment(ColorEmojiDWriteTextAlignment.Leading);
                textFormat.Interface.SetParagraphAlignment(ColorEmojiDWriteParagraphAlignment.Near);

                stage = "begin-draw";
                ColorEmojiD2DInterop.BeginDraw(renderTarget.Pointer);
                stage = "clear";
                ColorEmojiD2DColorF transparent = ColorEmojiD2DColorF.Transparent;
                ColorEmojiD2DInterop.Clear(renderTarget.Pointer, ref transparent);
                stage = "create-brush";
                ColorEmojiD2DColorF black = ColorEmojiD2DColorF.Black;
                textBrushPointer = ColorEmojiD2DInterop.CreateSolidColorBrush(renderTarget.Pointer, ref black);

                ColorEmojiD2DRectF layoutRect = new(
                    (float)BitmapPaddingDip,
                    (float)BitmapPaddingDip,
                    (float)(measuredSize.Width + BitmapPaddingDip),
                    (float)(measuredSize.Height + BitmapPaddingDip));

                stage = "draw-text";
                ColorEmojiD2DInterop.DrawText(
                    renderTarget.Pointer,
                    emoji,
                    textFormat.Pointer,
                    ref layoutRect,
                    textBrushPointer,
                    ColorEmojiD2DDrawTextOptions.EnableColorFont,
                    ColorEmojiDWriteMeasuringMode.Natural);

                stage = "end-draw";
                ColorEmojiD2DInterop.EndDraw(renderTarget.Pointer, out _, out _);

                stage = "copy-bitmap";
                return CopyToBitmapSource(wicBitmap.Interface, width, height, 96d * pixelsPerDip, 96d * pixelsPerDip);
            }
            catch (Exception ex)
            {
                _lastRenderFailure = $"{stage}: {ex.GetType().Name} hresult=0x{ex.HResult:X8}";
                return null;
            }
            finally
            {
                textFormat?.Dispose();
                dwriteFactory?.Dispose();
                ColorEmojiComRuntime.ReleasePointer(textBrushPointer);
                renderTarget?.Dispose();
                d2dFactory?.Dispose();
                wicBitmap?.Dispose();
                wicFactory?.Dispose();
            }
        }

        private static Size MeasureEmoji(string emoji, double fontSize, double pixelsPerDip)
        {
            Typeface typeface = new(new FontFamily(EmojiFontFamilyName), FontStyles.Normal, FontWeights.Regular, FontStretches.Normal);
            FormattedText formattedText = new(
                emoji,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.Black,
                pixelsPerDip);

            return new Size(
                Math.Max(1d, formattedText.WidthIncludingTrailingWhitespace),
                Math.Max(1d, formattedText.Height));
        }

        private static BitmapSource CopyToBitmapSource(IColorEmojiWicBitmapSource source, int width, int height, double dpiX, double dpiY)
        {
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            GCHandle pixelsHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                source.CopyPixels(IntPtr.Zero, (uint)stride, (uint)pixels.Length, pixelsHandle.AddrOfPinnedObject());
            }
            finally
            {
                pixelsHandle.Free();
            }

            if (TryTrimTransparentBounds(pixels, width, height, out byte[] trimmedPixels, out int trimmedWidth, out int trimmedHeight))
            {
                return BitmapSource.Create(trimmedWidth, trimmedHeight, dpiX, dpiY, PixelFormats.Pbgra32, null, trimmedPixels, trimmedWidth * 4);
            }

            return BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Pbgra32, null, pixels, stride);
        }

        private static bool TryTrimTransparentBounds(byte[] pixels, int width, int height, out byte[] trimmedPixels, out int trimmedWidth, out int trimmedHeight)
        {
            trimmedPixels = [];
            trimmedWidth = 0;
            trimmedHeight = 0;

            int left = width;
            int top = height;
            int right = -1;
            int bottom = -1;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int alphaIndex = rowOffset + (x * 4) + 3;
                    if (pixels[alphaIndex] == 0)
                    {
                        continue;
                    }

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            if (right < left || bottom < top)
            {
                return false;
            }

            if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1)
            {
                return false;
            }

            trimmedWidth = right - left + 1;
            trimmedHeight = bottom - top + 1;
            trimmedPixels = new byte[trimmedWidth * trimmedHeight * 4];

            for (int y = 0; y < trimmedHeight; y++)
            {
                int sourceOffset = ((top + y) * width * 4) + (left * 4);
                int targetOffset = y * trimmedWidth * 4;
                Buffer.BlockCopy(pixels, sourceOffset, trimmedPixels, targetOffset, trimmedWidth * 4);
            }

            return true;
        }

        private sealed class CacheEntry(ImageSource? source, LinkedListNode<EmojiRenderCacheKey> node, long createdTimestamp)
        {
            public ImageSource? Source { get; } = source;
            public LinkedListNode<EmojiRenderCacheKey> Node { get; } = node;
            public long CreatedTimestamp { get; } = createdTimestamp;
        }

        private readonly record struct EmojiRenderCacheKey(string Emoji, double FontSize, double PixelsPerDip);
    }
}

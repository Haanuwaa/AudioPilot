using System.Buffers;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AudioPilot.Services.UI.MediaOverlay;

internal static class MediaOverlayArtwork
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxSourceDimension = 4096;
    private const int DecodeDimension = 192;
    private static readonly SemaphoreSlim ReaderSlots = new(2);
    // Keep artwork scratch independent of thread-pool caches. Unusually large inputs are still accepted,
    // but only arrays up to 2 MiB can be kept for reuse, with one array per bucket.
    private static readonly ArrayPool<byte> Buffers = ArrayPool<byte>.Create(2 * 1024 * 1024, maxArraysPerBucket: 1);

    internal static IRandomAccessStreamReference? GetThumbnail(GlobalSystemMediaTransportControlsSessionMediaProperties? properties) =>
        MediaOverlayThumbnail.Get(properties);

    internal static Task<BitmapSource?> LoadAsync(IRandomAccessStreamReference thumbnail, CancellationToken token) => Task.Run(async () =>
    {
        await ReaderSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await ReadThumbnailAsync(thumbnail, token).ConfigureAwait(false);
        }
        finally { ReaderSlots.Release(); }
    }, token);

    private static async Task<BitmapSource?> ReadThumbnailAsync(IRandomAccessStreamReference thumbnail, CancellationToken token)
    {
        using var providerCancellation = new CancellationTokenSource();
        Task providerCallbacks = Task.CompletedTask;
        // WinRT's AsTask calls the provider synchronously on cancellation. The UI token
        // only schedules that work so the overlay can stop waiting independently.
        var registration = token.UnsafeRegister(_ => providerCallbacks = providerCancellation.CancelAsync(), null);
        CancellationToken providerToken = providerCancellation.Token;
        IRandomAccessStreamWithContentType? stream = null;
        IInputStream? input = null;
        DataReader? reader = null;
        try
        {
            token.ThrowIfCancellationRequested();
            stream = await thumbnail.OpenReadAsync().AsTask(providerToken).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ulong size = stream.Size;
            if (size is 0 or > MaxBytes) return null;
            uint length = (uint)size;
            input = stream.GetInputStreamAt(0);
            reader = new DataReader(input);
            if (await reader.LoadAsync(length).AsTask(providerToken).ConfigureAwait(false) != length) return null;
            token.ThrowIfCancellationRequested();
            byte[] bytes = Buffers.Rent((int)length);
            try
            {
                reader.ReadBuffer(length).CopyTo(bytes.AsSpan(0, (int)length));
                token.ThrowIfCancellationRequested();
                return Decode(bytes, (int)length);
            }
            finally { Buffers.Return(bytes, clearArray: true); }
        }
        finally
        {
            using (stream)
            using (input)
            using (reader)
            {
                // Settle the forwarder first so it cannot race publishing the callback task.
                // Keep the streams, token, and reader slot until any provider callbacks return.
                await registration.DisposeAsync().ConfigureAwait(false);
                await providerCallbacks.ConfigureAwait(false);
            }
        }
    }

    internal static BitmapSource? Decode(byte[] bytes, int length)
    {
        if (length is <= 0 or > MaxBytes) return null;

        using var stream = new MemoryStream(bytes, 0, length, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) return null;
        BitmapFrame frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width is <= 0 or > MaxSourceDimension || height is <= 0 or > MaxSourceDimension) return null;

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, DecodeDimension);
        else bitmap.DecodePixelHeight = Math.Min(height, DecodeDimension);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        if (bitmap.PixelWidth is <= 0 or > DecodeDimension || bitmap.PixelHeight is <= 0 or > DecodeDimension) return null;

        // Keep only the displayed pixels alive. BitmapImage retains its input stream.
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = Buffers.Rent(stride * converted.PixelHeight);
        try
        {
            converted.CopyPixels(pixels, stride, 0);
            var image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
                PixelFormats.Pbgra32, null, pixels, stride);
            image.Freeze();
            return image;
        }
        finally { Buffers.Return(pixels, clearArray: true); }
    }
}

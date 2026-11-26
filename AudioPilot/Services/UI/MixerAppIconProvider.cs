using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.Services.UI;

// Window-scoped, bounded work/cache. Icons never execute the target, and network
// paths and user-supplied session IconPath strings are deliberately not opened.
internal sealed partial class MixerAppIconProvider : IDisposable
{
    // Native extraction cannot be interrupted. Closing/reopening must not create
    // additional native readers behind an earlier slow shell/drive call.
    private static readonly SemaphoreSlim ReaderSlots = new(2);
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Request> _queue = Channel.CreateBounded<Request>(32);
    private readonly Dictionary<IconKey, Request> _pending = [];
    private readonly Dictionary<IconKey, ImageSource?> _cache = [];
    private readonly Queue<IconKey> _cacheOrder = [];
    private readonly Func<uint, long?, ImageSource?> _readIcon;
    private bool _disposed;
    private int _waitingReaderCount;
    internal Task Completion { get; }
    internal int WaitingReaderCount => Volatile.Read(ref _waitingReaderCount);

    internal MixerAppIconProvider(Func<uint, long?, ImageSource?>? readIcon = null)
    {
        _readIcon = readIcon ?? ReadIcon;
        CancellationToken token = _lifetime.Token;
        Completion = Task.WhenAll(Task.Run(() => WorkAsync(token)), Task.Run(() => WorkAsync(token)));
    }

    internal Task<ImageSource?>? GetProcessIconAsync(uint pid, long started) => GetAsync(pid, string.Empty, started);

    // A null task means backpressure, not a cached "no icon" result.
    internal Task<ImageSource?>? GetAsync(uint pid, string session, long? started)
    {
        lock (_sync)
        {
            if (_disposed) return Task.FromResult<ImageSource?>(null);
            var key = new IconKey(pid, session, started);
            if (_cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);
            if (_pending.TryGetValue(key, out var existing)) return existing.Result.Task;
            var request = new Request(key);
            if (!_queue.Writer.TryWrite(request)) return null;
            _pending.Add(key, request);
            return request.Result.Task;
        }
    }

    private async Task WorkAsync(CancellationToken token)
    {
        // Dispose completes the channel and settles queued requests. Cancellation
        // interrupts slot waits; draining that completed channel is successful cleanup.
        await foreach (Request request in _queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (request.Result.Task.IsCompleted) continue;
            Interlocked.Increment(ref _waitingReaderCount);
            try { await ReaderSlots.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            finally { Interlocked.Decrement(ref _waitingReaderCount); }
            ImageSource? image = null;
            try { if (!token.IsCancellationRequested) image = _readIcon(request.Key.Pid, request.Key.Started); }
            catch { /* An unavailable icon uses the theme glyph. */ }
            finally { ReaderSlots.Release(); }
            lock (_sync)
            {
                var key = request.Key;
                _pending.Remove(key);
                if (!_disposed)
                {
                    _cache.Add(key, image);
                    _cacheOrder.Enqueue(key);
                    while (_cacheOrder.Count > 64) _cache.Remove(_cacheOrder.Dequeue());
                }
                request.Result.TrySetResult(image);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _queue.Writer.TryComplete();
            foreach (var request in _pending.Values) request.Result.TrySetResult(null);
            _pending.Clear();
            _cache.Clear();
            _cacheOrder.Clear();
        }
        _ = Completion.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), _lifetime,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private readonly record struct IconKey(uint Pid, string Session, long? Started);

    private sealed record Request(IconKey Key)
    {
        internal TaskCompletionSource<ImageSource?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static unsafe BitmapSource? ReadIcon(uint pid, long? expectedStarted)
    {
        using SafeProcessHandle process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) return null;
        if (expectedStarted.HasValue && (!GetProcessTimes(process, out long created, out _, out _, out _)
            || DateTime.FromFileTimeUtc(created).Ticks != expectedStarted.Value)) return null;
        char* buffer = stackalloc char[1024];
        uint length = 1024;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length)) return null;
        string path = new(buffer, 0, checked((int)length));
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) return null;
        nint large = 0, small = 0;
        try
        {
            if (ExtractIconEx(path, 0, out large, out small, 1) == 0) return null;
            nint icon = large != 0 ? large : small;
            if (icon == 0) return null;
            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (small != 0) _ = DestroyIcon(small);
            if (large != 0 && large != small) _ = DestroyIcon(large);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* name, ref uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string path, int index, out nint large, out nint small, uint count);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}

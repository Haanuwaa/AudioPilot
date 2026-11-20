using System.Runtime.InteropServices;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Audio.Metering;

// Opt-in foundation for future mixer presentations; there is no application-wide
// polling until a visible consumer acquires a lease. No callbacks run on the UI thread.
internal sealed partial class AudioMeterSampler : IDisposable
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(1);
    private readonly Lock _sync = new();
    private readonly MixerVisibilityDemand _demand = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<IAudioMeterBackend> _backendFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly Logger? _logger;
    private Thread? _worker;
    private bool _disposed;
    private long _refreshRevision;
    private AudioMeterSnapshot? _latest;
    private CancellationTokenSource? _readCancellation;
    private long _readingVersion = -1;
    private bool _idle = true;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, int flags);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    internal AudioMeterSampler(Func<IAudioMeterBackend>? backendFactory = null, TimeProvider? timeProvider = null, TimeSpan? interval = null,
        Logger? logger = null)
    {
        _backendFactory = backendFactory ?? (() => new CoreAudioMeterBackend());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? AudioMeterPresentation.RefreshInterval;
        _logger = logger;
        if (_interval < TimeSpan.FromMilliseconds(16) || _interval > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval));
        _demand.Changed += Wake;
    }

    internal Task Completion => _completion.Task;
    internal bool IsIdle { get { lock (_sync) return _idle; } }
    internal IDisposable AcquireVisibility(AudioMixerMode mode) => _demand.Acquire(mode);

    internal bool TryGetLatest(AudioMixerMode mode, out AudioMeterSnapshot? snapshot)
    {
        var demand = _demand.Current;
        AudioMeterModes requested = mode switch
        {
            AudioMixerMode.Output => AudioMeterModes.Output,
            AudioMixerMode.Input => AudioMeterModes.Input,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        snapshot = Volatile.Read(ref _latest);
        if ((demand.Modes & requested) != 0 && snapshot != null && snapshot.DemandVersion == demand.Version
            && _timeProvider.GetElapsedTime(snapshot.SampleStarted) <= TimeSpan.FromMilliseconds(250)) return true;
        snapshot = null;
        return false;
    }

    // Device/session notifications can request discovery without enqueuing a read per event.
    internal void RequestRefresh()
    {
        Interlocked.Increment(ref _refreshRevision);
        if (_demand.Current.Modes != AudioMeterModes.None) Wake();
    }

    private void Wake()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_demand.Current.Modes != AudioMeterModes.None) _idle = false;
            if (_readingVersion != _demand.Current.Version) _readCancellation?.Cancel();
            _wake.Set();
            if (_worker == null && _demand.Current.Modes != AudioMeterModes.None)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "AudioPilot.MeterCOM" };
                _worker.SetApartmentState(ApartmentState.MTA);
                _worker.Start();
            }
        }
    }

    private void Run()
    {
        IAudioMeterBackend? backend = null;
        ITimer? timer = null;
        Exception? failure = null;
        bool apartmentInitialized = false;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); // MTA, balanced on this thread.
            apartmentInitialized = true;
            timer = _timeProvider.CreateTimer(_ => Wake(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            long lastDiscovery = 0, lastVersion = -1, lastRefresh = -1;
            long lastAttemptVersion = -1, lastCompleted = 0;
            TimeSpan delay = TimeSpan.Zero;
            while (true)
            {
                _wake.Reset();
                if (_lifetime.IsCancellationRequested) break;
                var demand = _demand.Current;
                if (demand.Modes == AudioMeterModes.None)
                {
                    Volatile.Write(ref _latest, null);
                    var retiring = backend;
                    backend = null;
                    try { retiring?.Dispose(); }
                    catch (Exception ex)
                    {
                        _logger?.Warning("QuickMixer", "quick-mixer-meter-idle-dispose-failed", nameof(Run), ex);
                    }
                    lock (_sync) { if (_demand.Current.Modes == AudioMeterModes.None) _idle = true; }
                    timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _wake.WaitOne();
                    continue;
                }

                long started = _timeProvider.GetTimestamp();
                TimeSpan remaining = delay - _timeProvider.GetElapsedTime(lastCompleted, started);
                if (lastAttemptVersion == demand.Version && remaining > TimeSpan.Zero)
                {
                    // A delayed wake or notification burst cannot bypass cadence
                    // or failure backoff, and never creates a backlog of reads.
                    // System timers round to milliseconds; a sub-ms remainder
                    // must not become a zero-delay callback spin.
                    timer.Change(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, Timeout.InfiniteTimeSpan);
                    _wake.WaitOne();
                    continue;
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long refreshRevision = Volatile.Read(ref _refreshRevision);
                bool refresh = backend == null || lastVersion != demand.Version || lastRefresh != refreshRevision
                    || _timeProvider.GetElapsedTime(lastDiscovery, started) >= DiscoveryInterval;
                CancellationToken readToken;
                lock (_sync)
                {
                    if (_lifetime.IsCancellationRequested || _demand.Current != demand) continue;
                    if (_readingVersion != demand.Version)
                    {
                        _readCancellation?.Dispose();
                        _readCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        _readingVersion = demand.Version;
                    }
                    readToken = _readCancellation!.Token;
                }
                AudioMeterBatch batch;
                int? error = null;
                try
                {
                    backend ??= _backendFactory();
                    batch = backend.Read(demand.Modes, refresh, readToken);
                    if (refresh) lastDiscovery = started;
                    lastVersion = demand.Version;
                    lastRefresh = refreshRevision;
                }
                catch (OperationCanceledException) when (readToken.IsCancellationRequested) { continue; }
                catch (Exception ex)
                {
                    error = ex.HResult;
                    batch = new([], AudioMeterModes.None);
                    // The backend can retry discovery on the next (backed-off) read.
                    lastVersion = -1;
                }

                var snapshot = new AudioMeterSnapshot(demand.Version, started, batch,
                    _timeProvider.GetElapsedTime(started), GC.GetAllocatedBytesForCurrentThread() - allocated, refresh, error);
                lastAttemptVersion = demand.Version;
                lastCompleted = _timeProvider.GetTimestamp();
                delay = error.HasValue ? FailureRetryInterval : _interval;
                if (_demand.Current == demand && !_lifetime.IsCancellationRequested) Volatile.Write(ref _latest, snapshot);
                if (_demand.Current != demand) continue; // Never publish a hidden or previous-mode result.
                timer.Change(delay, Timeout.InfiniteTimeSpan);
                _wake.WaitOne(); // One worker, one read, one wake bit; no tick backlog.
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { timer?.Dispose(); } catch (Exception ex) { failure ??= ex; }
            try { backend?.Dispose(); } catch (Exception ex) { failure ??= ex; }
            if (apartmentInitialized) CoUninitialize();
            Volatile.Write(ref _latest, null);
            lock (_sync)
            {
                _disposed = true;
                _idle = true;
                _demand.Dispose();
                _readCancellation?.Dispose();
                _wake.Dispose();
                _lifetime.Dispose();
            }
            if (failure == null) _completion.TrySetResult(); else _completion.TrySetException(failure);
        }
    }

    // Never block a closing window on a driver. Native cleanup stays on its owner
    // thread after an in-flight read returns; StopAsync lets an owner await it safely.
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _demand.Dispose();
            _lifetime.Cancel();
            _wake.Set();
            if (_worker == null)
            {
                _wake.Dispose();
                _lifetime.Dispose();
                _completion.TrySetResult();
            }
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

using System.Diagnostics;
using System.Management;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Platform;

internal readonly record struct ProcessLifecycleMonitorStartResult(bool Success, string Status, string? FailureReason = null);

internal readonly record struct ProcessLifecycleMonitorTransition(
    IReadOnlyList<int> StartedProcessIds,
    IReadOnlyList<int> StoppedProcessIds);

internal interface IProcessLifecycleMonitor : IDisposable
{
    event Action<int>? ProcessStarted;
    event Action<int>? ProcessStopped;

    bool IsRunning { get; }

    ProcessLifecycleMonitorStartResult Start();
    void Stop();
}

internal interface IRawProcessLifecycleEventSource : IDisposable
{
    event Action<int>? ProcessStarted;
    event Action<int>? ProcessStopped;

    bool IsRunning { get; }

    void Start();
    void Stop();
}

internal static class ProcessLifecycleMonitorFactory
{
    internal static IProcessLifecycleMonitor Create(Logger logger)
    {
        return new FallbackProcessLifecycleMonitor(
            logger,
            primaryFactory: () => new EventDrivenProcessLifecycleMonitor(logger),
            fallbackFactory: () => new PollingProcessLifecycleMonitor(logger));
    }
}

internal sealed class FallbackProcessLifecycleMonitor(
    Logger logger,
    Func<IProcessLifecycleMonitor> primaryFactory,
    Func<IProcessLifecycleMonitor> fallbackFactory) : IProcessLifecycleMonitor
{
    private readonly Logger _logger = logger;
    private readonly Func<IProcessLifecycleMonitor> _primaryFactory = primaryFactory;
    private readonly Func<IProcessLifecycleMonitor> _fallbackFactory = fallbackFactory;
    private readonly Lock _sync = new();
    private IProcessLifecycleMonitor? _activeMonitor;
    private bool _preferFallback;
    private bool _disposed;

    public event Action<int>? ProcessStarted;
    public event Action<int>? ProcessStopped;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _activeMonitor?.IsRunning == true;
            }
        }
    }

    /// <summary>
    /// Starts the active process lifecycle monitor, preferring the event-driven implementation and degrading to polling
    /// when the primary monitor cannot be activated.
    /// </summary>
    /// <remarks>
    /// Repeated calls are idempotent while the current monitor is healthy. If the primary monitor fails to start, this
    /// method tears it down under the same lock, records that fallback is now preferred, and then attempts to promote
    /// the polling monitor instead. Once fallback is selected, later calls preserve that mode until the instance is
    /// rebuilt.
    /// </remarks>
    public ProcessLifecycleMonitorStartResult Start()
    {
        lock (_sync)
        {
            string primaryFailureReason = "not-attempted";
            double primaryStartMs = 0;

            if (_disposed)
            {
                return new ProcessLifecycleMonitorStartResult(false, "inactive", "monitor-disposed");
            }

            if (_activeMonitor != null)
            {
                ProcessLifecycleMonitorStartResult existingResult = _activeMonitor.Start();
                if (existingResult.Success || _preferFallback)
                {
                    return existingResult;
                }

                CleanupMonitorUnderLock(_activeMonitor);
                _activeMonitor = null;
                _preferFallback = true;
            }

            if (!_preferFallback)
            {
                IProcessLifecycleMonitor primaryMonitor = _primaryFactory();
                AttachMonitor(primaryMonitor);
                Stopwatch primaryStartStopwatch = Stopwatch.StartNew();
                ProcessLifecycleMonitorStartResult primaryResult = primaryMonitor.Start();
                primaryStartMs = primaryStartStopwatch.Elapsed.TotalMilliseconds;
                if (primaryResult.Success)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug(
                            "FallbackProcessLifecycleMonitor",
                            () => $"{AppConstants.Audio.LogEvents.Diagnostics.ProcessLifecycleMonitorFallback} | mode=event-driven result=primary-active primaryStartMs={primaryStartMs:F1}");
                    }

                    _activeMonitor = primaryMonitor;
                    return primaryResult;
                }

                CleanupMonitorUnderLock(primaryMonitor);
                _preferFallback = true;
                primaryFailureReason = primaryResult.FailureReason ?? primaryResult.Status;
            }

            IProcessLifecycleMonitor fallbackMonitor = _fallbackFactory();
            AttachMonitor(fallbackMonitor);
            Stopwatch fallbackStartStopwatch = Stopwatch.StartNew();
            ProcessLifecycleMonitorStartResult fallbackResult = fallbackMonitor.Start();
            double fallbackStartMs = fallbackStartStopwatch.Elapsed.TotalMilliseconds;
            if (fallbackResult.Success)
            {
                _logger.Info(
                    "FallbackProcessLifecycleMonitor",
                    () => $"{AppConstants.Audio.LogEvents.Diagnostics.ProcessLifecycleMonitorFallback} | mode=polling result=fallback-active reason={primaryFailureReason} primaryStartMs={primaryStartMs:F1} fallbackStartMs={fallbackStartMs:F1}");
                _activeMonitor = fallbackMonitor;
                return fallbackResult;
            }

            CleanupMonitorUnderLock(fallbackMonitor);
            _logger.Warning(
                "FallbackProcessLifecycleMonitor",
                () => $"{AppConstants.Audio.LogEvents.Diagnostics.ProcessLifecycleMonitorFallback} | mode=polling result=fallback-failed reason={primaryFailureReason} primaryStartMs={primaryStartMs:F1} fallbackFailureReason={fallbackResult.FailureReason ?? fallbackResult.Status} fallbackStartMs={fallbackStartMs:F1}");
            return fallbackResult;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _activeMonitor?.Stop();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_activeMonitor != null)
            {
                CleanupMonitorUnderLock(_activeMonitor);
                _activeMonitor = null;
            }
        }
    }

    private void AttachMonitor(IProcessLifecycleMonitor monitor)
    {
        monitor.ProcessStarted += OnProcessStarted;
        monitor.ProcessStopped += OnProcessStopped;
    }

    private void CleanupMonitorUnderLock(IProcessLifecycleMonitor monitor)
    {
        monitor.ProcessStarted -= OnProcessStarted;
        monitor.ProcessStopped -= OnProcessStopped;
        monitor.Stop();
        monitor.Dispose();
    }

    private void OnProcessStarted(int processId)
    {
        bool shouldRaise;
        lock (_sync)
        {
            shouldRaise = !_disposed && _activeMonitor?.IsRunning == true;
        }

        if (shouldRaise)
        {
            ProcessStarted?.Invoke(processId);
        }
    }

    private void OnProcessStopped(int processId)
    {
        bool shouldRaise;
        lock (_sync)
        {
            shouldRaise = !_disposed && _activeMonitor?.IsRunning == true;
        }

        if (shouldRaise)
        {
            ProcessStopped?.Invoke(processId);
        }
    }
}

internal sealed class EventDrivenProcessLifecycleMonitor(
    Logger logger,
    Func<IRawProcessLifecycleEventSource>? eventSourceFactory = null,
    Func<HashSet<int>>? captureRunningProcessIds = null) : IProcessLifecycleMonitor
{
    private readonly Logger _logger = logger;
    private readonly Lock _sync = new();
    private readonly Func<IRawProcessLifecycleEventSource> _eventSourceFactory = eventSourceFactory ?? (() => new WmiProcessLifecycleEventSource());
    private readonly Func<HashSet<int>> _captureRunningProcessIds = captureRunningProcessIds ?? PollingProcessLifecycleMonitor.CaptureRunningProcessIds;
    private IRawProcessLifecycleEventSource? _eventSource;
    private HashSet<int> _knownProcessIds = [];
    private bool _started;
    private bool _disposed;

    public event Action<int>? ProcessStarted;
    public event Action<int>? ProcessStopped;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _started;
            }
        }
    }

    public ProcessLifecycleMonitorStartResult Start()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new ProcessLifecycleMonitorStartResult(false, "inactive", "monitor-disposed");
            }

            if (_started)
            {
                return new ProcessLifecycleMonitorStartResult(true, "active");
            }

            try
            {
                _knownProcessIds = _captureRunningProcessIds();
                _eventSource = _eventSourceFactory();
                _started = true;
                _eventSource.ProcessStarted += OnRawProcessStarted;
                _eventSource.ProcessStopped += OnRawProcessStopped;
                _eventSource.Start();
                return new ProcessLifecycleMonitorStartResult(true, "active");
            }
            catch (Exception ex)
            {
                _logger.Info(
                    "EventDrivenProcessLifecycleMonitor",
                    () => $"event-driven-process-lifecycle-monitor-unavailable | reason={ex.GetType().Name} fallbackEligible=true",
                    nameof(Start));
                StopEventSourceUnderLock();
                _knownProcessIds.Clear();
                return new ProcessLifecycleMonitorStartResult(false, "inactive", ex.GetType().Name);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopEventSourceUnderLock();
            _knownProcessIds.Clear();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopEventSourceUnderLock();
            _knownProcessIds.Clear();
        }
    }

    private void StopEventSourceUnderLock()
    {
        if (_eventSource != null)
        {
            _eventSource.ProcessStarted -= OnRawProcessStarted;
            _eventSource.ProcessStopped -= OnRawProcessStopped;
            _eventSource.Stop();
            _eventSource.Dispose();
            _eventSource = null;
        }

        _started = false;
    }

    private void OnRawProcessStarted(int processId)
    {
        bool shouldRaise;
        lock (_sync)
        {
            shouldRaise = _started && !_disposed && processId > 0 && _knownProcessIds.Add(processId);
        }

        if (shouldRaise)
        {
            ProcessStarted?.Invoke(processId);
        }
    }

    private void OnRawProcessStopped(int processId)
    {
        bool shouldRaise;
        lock (_sync)
        {
            shouldRaise = _started && !_disposed && processId > 0 && _knownProcessIds.Remove(processId);
        }

        if (shouldRaise)
        {
            ProcessStopped?.Invoke(processId);
        }
    }
}

internal sealed class WmiProcessLifecycleEventSource : IRawProcessLifecycleEventSource
{
    private const string ProcessIdPropertyName = "ProcessID";
    internal const string ProcessStartTraceQueryText = "SELECT * FROM Win32_ProcessStartTrace";
    internal const string ProcessStopTraceQueryText = "SELECT * FROM Win32_ProcessStopTrace";

    private readonly Lock _sync = new();
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private bool _running;
    private bool _disposed;

    public event Action<int>? ProcessStarted;
    public event Action<int>? ProcessStopped;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                return;
            }

            _startWatcher = CreateWatcher(ProcessStartTraceQueryText);
            _stopWatcher = CreateWatcher(ProcessStopTraceQueryText);
            _startWatcher.EventArrived += OnStartEventArrived;
            _stopWatcher.EventArrived += OnStopEventArrived;

            try
            {
                _startWatcher.Start();
                _stopWatcher.Start();
                _running = true;
            }
            catch
            {
                DisposeWatchersUnderLock();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            DisposeWatchersUnderLock();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeWatchersUnderLock();
        }
    }

    internal static bool TryConvertProcessId(object? value, out int processId)
    {
        processId = 0;

        switch (value)
        {
            case int intValue when intValue > 0:
                processId = intValue;
                return true;
            case uint uintValue when uintValue > 0 && uintValue <= int.MaxValue:
                processId = (int)uintValue;
                return true;
            case long longValue when longValue > 0 && longValue <= int.MaxValue:
                processId = (int)longValue;
                return true;
            case ulong ulongValue when ulongValue > 0 && ulongValue <= int.MaxValue:
                processId = (int)ulongValue;
                return true;
            case short shortValue when shortValue > 0:
                processId = shortValue;
                return true;
            case ushort ushortValue when ushortValue > 0:
                processId = ushortValue;
                return true;
            case string stringValue when int.TryParse(stringValue, out int parsedProcessId) && parsedProcessId > 0:
                processId = parsedProcessId;
                return true;
            default:
                return false;
        }
    }

    private static ManagementEventWatcher CreateWatcher(string queryText)
    {
        return new ManagementEventWatcher(CreateEventQuery(queryText));
    }

    internal static WqlEventQuery CreateEventQuery(string queryText)
    {
        return new WqlEventQuery(queryText);
    }

    private void DisposeWatchersUnderLock()
    {
        DisposeWatcher(ref _startWatcher, OnStartEventArrived);
        DisposeWatcher(ref _stopWatcher, OnStopEventArrived);
        _running = false;
    }

    private static void DisposeWatcher(ref ManagementEventWatcher? watcher, EventArrivedEventHandler handler)
    {
        if (watcher == null)
        {
            return;
        }

        watcher.EventArrived -= handler;
        try
        {
            watcher.Stop();
        }
        catch
        {
        }

        watcher.Dispose();
        watcher = null;
    }

    private void OnStartEventArrived(object sender, EventArrivedEventArgs e)
    {
        if (TryConvertProcessId(e.NewEvent.Properties[ProcessIdPropertyName]?.Value, out int processId))
        {
            PublishProcessStarted(processId);
        }
    }

    private void OnStopEventArrived(object sender, EventArrivedEventArgs e)
    {
        if (TryConvertProcessId(e.NewEvent.Properties[ProcessIdPropertyName]?.Value, out int processId))
        {
            PublishProcessStopped(processId);
        }
    }

    private void PublishProcessStarted(int processId)
    {
        bool shouldRaise;
        lock (_sync)
        {
            shouldRaise = _running && !_disposed && processId > 0;
        }

        if (shouldRaise)
        {
            ProcessStarted?.Invoke(processId);
        }
    }

    private void PublishProcessStopped(int processId)
    {
        bool shouldRaise;
        lock (_sync)
        {
            shouldRaise = _running && !_disposed && processId > 0;
        }

        if (shouldRaise)
        {
            ProcessStopped?.Invoke(processId);
        }
    }
}

/// <summary>
/// Polls the local process table and emits started/stopped transitions for app-start routine coordination.
/// </summary>
/// <remarks>
/// The monitor captures a baseline snapshot before starting its timer so already-running processes are treated as
/// existing state rather than false-positive launches. Timer ticks are also single-flight to avoid overlapping scans
/// when enumeration is slower than the polling interval.
/// </remarks>
internal sealed class PollingProcessLifecycleMonitor(Logger logger, TimeSpan? pollInterval = null) : IProcessLifecycleMonitor
{
    private readonly Logger _logger = logger;
    private readonly Lock _sync = new();
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private Timer? _timer;
    private HashSet<int> _knownProcessIds = [];
    private bool _started;
    private volatile bool _disposed;
    private int _scanInProgress;

    public event Action<int>? ProcessStarted;
    public event Action<int>? ProcessStopped;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _started;
            }
        }
    }

    /// <summary>
    /// Starts polling if the monitor is still active and returns a stable status result for callers.
    /// </summary>
    /// <remarks>
    /// Starting is idempotent. A successful second call reports the existing active state, while startup failures tear
    /// down any partially-created timer state before returning an inactive result with the failure type.
    /// </remarks>
    public ProcessLifecycleMonitorStartResult Start()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new ProcessLifecycleMonitorStartResult(false, "inactive", "monitor-disposed");
            }

            if (_started)
            {
                return new ProcessLifecycleMonitorStartResult(true, "active");
            }

            try
            {
                _knownProcessIds = CaptureRunningProcessIds();
                _timer = new Timer(OnTimerTick, null, _pollInterval, _pollInterval);
                _started = true;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "PollingProcessLifecycleMonitor",
                        () => $"{AppConstants.Audio.LogEvents.Diagnostics.ProcessLifecycleMonitorFallback} | mode=polling-start pollIntervalMs={_pollInterval.TotalMilliseconds:F0} baselineCount={_knownProcessIds.Count}");
                }

                return new ProcessLifecycleMonitorStartResult(true, "active");
            }
            catch (Exception ex)
            {
                _logger.Warning("PollingProcessLifecycleMonitor", "Failed to start process lifecycle monitor", nameof(Start), ex);
                StopTimerUnderLock();
                return new ProcessLifecycleMonitorStartResult(false, "inactive", ex.GetType().Name);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopTimerUnderLock();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopTimerUnderLock();
        }
    }

    internal static ProcessLifecycleMonitorTransition CalculateTransition(
        IReadOnlySet<int> previousProcessIds,
        IReadOnlySet<int> currentProcessIds)
    {
        List<int> startedProcessIds = [.. currentProcessIds.Except(previousProcessIds).OrderBy(static id => id)];
        List<int> stoppedProcessIds = [.. previousProcessIds.Except(currentProcessIds).OrderBy(static id => id)];
        return new ProcessLifecycleMonitorTransition(startedProcessIds, stoppedProcessIds);
    }

    private void OnTimerTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _scanInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            HashSet<int> previousProcessIds;
            lock (_sync)
            {
                if (!_started || _disposed)
                {
                    return;
                }

                previousProcessIds = [.. _knownProcessIds];
            }

            HashSet<int> currentProcessIds = CaptureRunningProcessIds();
            ProcessLifecycleMonitorTransition transition = CalculateTransition(previousProcessIds, currentProcessIds);

            lock (_sync)
            {
                if (_started && !_disposed)
                {
                    _knownProcessIds = currentProcessIds;
                }
            }

            PublishTransition(transition);
        }
        catch (Exception ex)
        {
            _logger.Warning("PollingProcessLifecycleMonitor", "Failed to scan process lifecycle state", nameof(OnTimerTick), ex);
        }
        finally
        {
            Interlocked.Exchange(ref _scanInProgress, 0);
        }
    }

    private void StopTimerUnderLock()
    {
        _timer?.Dispose();
        _timer = null;
        _knownProcessIds.Clear();
        _started = false;
    }

    private void PublishTransition(ProcessLifecycleMonitorTransition transition)
    {
        foreach (int processId in transition.StartedProcessIds)
        {
            if (!CanPublishTickEvents())
            {
                return;
            }

            ProcessStarted?.Invoke(processId);
        }

        foreach (int processId in transition.StoppedProcessIds)
        {
            if (!CanPublishTickEvents())
            {
                return;
            }

            ProcessStopped?.Invoke(processId);
        }
    }

    private bool CanPublishTickEvents()
    {
        lock (_sync)
        {
            return _started && !_disposed;
        }
    }

    internal static HashSet<int> CaptureRunningProcessIds() => ProcessEnumerationHelper.CaptureRunningProcessIds();
}

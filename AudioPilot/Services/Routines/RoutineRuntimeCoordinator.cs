using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Routines;

internal sealed record RoutineRuntimeCallbacks(
    Func<IReadOnlyList<AudioRoutine>> GetRoutines,
    Func<bool, IReadOnlyList<CycleDevice>> GetDevices,
    Action<AudioRoutine, string> ExecuteScheduled,
    Action<AudioRoutine, string> ExecuteNetwork,
    Func<AudioRoutine, int, Task> ExecuteFocused,
    Func<AudioRoutine, int, Task> DeactivateFocused,
    Action<IReadOnlyList<ScheduledRoutineReminder>> NotifyUpcoming,
    Func<nint, bool> IgnoreFocusWindow,
    Action<int> ProcessStarted,
    Action<int> ProcessStopped,
    Action<SteamBigPictureSignal> SteamSignaled,
    Func<AudioRoutine, CancellationToken, Task> ExecuteSystemEvent);

internal enum RoutineRuntimeWork
{
    RoutingRefresh,
    SteamSignal,
    SteamConfirmation,
    SteamMaintenance,
}

/// <summary>Owns routine monitoring, configuration snapshots, and cancellable background work.</summary>
internal sealed class RoutineRuntimeCoordinator : IAsyncDisposable
{
    private readonly Logger _logger;
    private readonly AudioDeviceService? _audio;
    private readonly RoutineRuntimeCallbacks _callbacks;
    private readonly CancellationTokenSource _shutdown;
    private readonly BackgroundWorkQueue _workQueue;
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private readonly Dictionary<RoutineRuntimeWork, CancellationTokenSource> _pendingWork = [];
    private readonly Lazy<ScheduleTriggerCoordinator> _scheduleTriggerCoordinator;
    private readonly Lazy<NetworkTriggerCoordinator> _networkTriggerCoordinator;
    private readonly Lazy<ApplicationTriggerCoordinator> _applicationTriggerCoordinator;
    private readonly Lazy<ISteamBigPictureSignalMonitor> _steamMonitor;
    private ISteamBigPictureSignalMonitor? _unmaterializedSteamMonitor;
    private RoutineSystemEventCoordinator? _systemEvents;
    private volatile bool _stopping;
    private bool _processMonitorStartPending;
    private bool _outputSessionMonitoring;
    private bool _inputSessionMonitoring;
    private int _routingSignals;
    private Task? _disposeTask;

    internal RoutineRuntimeCoordinator(
        ObservableCollection<AudioRoutine> routines,
        RoutineRuntimeCallbacks callbacks,
        Logger logger,
        AudioDeviceService? audio = null,
        IProcessLifecycleMonitor? processMonitor = null,
        IRoutineProcessSnapshotProvider? processSnapshots = null,
        ISteamBigPictureSignalMonitor? steamMonitor = null,
        CancellationToken shutdownToken = default)
    {
        _logger = logger;
        _audio = audio;
        _callbacks = callbacks;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        _workQueue = new(logger, () => _stopping);
        ProcessMonitor = processMonitor ?? ProcessLifecycleMonitorFactory.Create(logger);
        ProcessSnapshots = processSnapshots ?? new RoutineProcessSnapshotProvider(logger);
        Availability = new(logger);
        Lifetime.AttachProcessMonitor(ProcessMonitor,
            id => { if (!_stopping) callbacks.ProcessStarted(id); }, id => { if (!_stopping) callbacks.ProcessStopped(id); });
        _unmaterializedSteamMonitor = steamMonitor;
        _steamMonitor = new(() =>
        {
            ISteamBigPictureSignalMonitor monitor = Interlocked.Exchange(ref _unmaterializedSteamMonitor, null) ?? new WinEventSteamBigPictureSignalMonitor();
            try { monitor.Signaled += OnSteamSignal; return monitor; }
            catch { monitor.Dispose(); throw; }
        });
        _scheduleTriggerCoordinator = new(() => new(routines, (routine, source) => { if (!_stopping) callbacks.ExecuteScheduled(routine, source); }, logger,
            routineSnapshotProvider: callbacks.GetRoutines, notifyUpcomingRoutines: callbacks.NotifyUpcoming));
        _networkTriggerCoordinator = new(() => new(routines, (routine, source) => { if (!_stopping) callbacks.ExecuteNetwork(routine, source); }, logger, routineSnapshotProvider: callbacks.GetRoutines));
        _applicationTriggerCoordinator = new(() => new(routines, (routine, id) => _stopping ? Task.CompletedTask : callbacks.ExecuteFocused(routine, id), logger,
            deactivateRoutine: (routine, id) => _stopping ? Task.CompletedTask : callbacks.DeactivateFocused(routine, id), routineSnapshotProvider: callbacks.GetRoutines,
            ignoreFocusWindow: callbacks.IgnoreFocusWindow));
    }

    internal Lock Sync { get; } = new();
    internal RoutineLifetimeService Lifetime { get; } = new();
    internal RoutineDeviceAvailabilityCoordinator Availability { get; }
    internal IProcessLifecycleMonitor ProcessMonitor { get; }
    internal IRoutineProcessSnapshotProvider ProcessSnapshots { get; }
    internal IReadOnlyList<AudioRoutine> ApplicationRoutines { get; set; } = [];
    internal IReadOnlyList<AudioRoutine> RoutingRoutines { get; private set; } = [];
    internal IReadOnlyList<AudioRoutine> SteamRoutines { get; set; } = [];
    internal bool MonitoringEnabled { get; set; }

    internal int DeferredWorkCount => _workQueue.DeferredOperationCount;
    internal Task[] SnapshotPendingTasks() => BackgroundTaskHelper.SnapshotPendingTasks(_tasks);

    internal RoutineWorkSnapshot CaptureWorkSnapshot()
    {
        lock (Sync) return new(MonitoringEnabled, _stopping, _tasks.Values.Count(static task => !task.IsCompleted),
            _workQueue.DeferredOperationCount, [.. _pendingWork.Keys.Order().Select(static kind => kind.ToString())]);
    }

    internal ISteamBigPictureSignalMonitor SteamMonitor
    {
        get
        {
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(_stopping && !_steamMonitor.IsValueCreated, this);
                return _steamMonitor.Value;
            }
        }
    }

    internal RoutineSystemEventCoordinator SystemEvents
    {
        get
        {
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                return _systemEvents ??= new(_callbacks.GetRoutines, _callbacks.GetDevices, _callbacks.ExecuteSystemEvent, _logger, _shutdown.Token);
            }
        }
    }

    internal long? SystemRecoveryVersion { get { lock (Sync) return _systemEvents?.RecoveryVersion; } }
    internal void CompleteSystemRecovery(long? version) { lock (Sync) _systemEvents?.CompleteRecovery(version); }

    internal void EnableMonitoring() { lock (Sync) { if (!_stopping) MonitoringEnabled = true; } }

    internal IReadOnlyList<RoutineLifetimeService.Deactivation> ApplyConfiguration()
    {
        lock (Sync) { if (_stopping) return []; }
        IReadOnlyList<AudioRoutine> configured = _callbacks.GetRoutines();
        Availability.Observe(configured, _callbacks.GetDevices, evaluateTransitions: false);
        AudioRoutine[] expanded = [.. configured.Where(static routine => routine.Enabled)
            .SelectMany(static routine => routine.ExpandAutomaticTriggers(static trigger => trigger.Kind is RoutineTriggerKind.Application or RoutineTriggerKind.SteamBigPicture))];
        lock (Sync)
        {
            if (_stopping) return [];
            ApplicationRoutines = [.. expanded.Where(static routine => routine.HasApplicationTrigger && RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TriggerAppPath))];
            SteamRoutines = [.. expanded.Where(static routine => routine.TriggerKind == RoutineTriggerKind.SteamBigPicture && routine.HasExecutionTarget)];
            RoutingRoutines = [.. configured.Where(static routine => routine.Enabled && routine.RouteToApplication)];
            Lifetime.SynchronizeLeases(RoutingRoutines);
            return Lifetime.Synchronize(ApplicationRoutines, SteamRoutines);
        }
    }

    internal void UpdateAutomaticTriggers()
    {
        AudioRoutine[] automatic = [.. _callbacks.GetRoutines().SelectMany(static routine => routine.ExpandAutomaticTriggers()).Where(static routine => routine.Enabled)];
        ScheduleTriggerCoordinator? schedule;
        NetworkTriggerCoordinator? network;
        ApplicationTriggerCoordinator? focus;
        bool scheduled = automatic.Any(static routine => routine.TriggerKind == RoutineTriggerKind.Scheduled);
        bool networkTriggered = automatic.Any(static routine => routine.TriggerKind == RoutineTriggerKind.Network);
        bool focused = automatic.Any(static routine => routine.TriggerKind == RoutineTriggerKind.Application && routine.ApplicationTriggerMode == ApplicationTriggerMode.ProcessFocus);
        lock (Sync)
        {
            if (_stopping) return;
            schedule = scheduled || _scheduleTriggerCoordinator.IsValueCreated ? _scheduleTriggerCoordinator.Value : null;
            network = networkTriggered || _networkTriggerCoordinator.IsValueCreated ? _networkTriggerCoordinator.Value : null;
            focus = focused || _applicationTriggerCoordinator.IsValueCreated ? _applicationTriggerCoordinator.Value : null;
        }
        if (scheduled) schedule!.Start(); else schedule?.Stop();
        if (networkTriggered) network!.Start(); else network?.Stop();
        focus?.RefreshRoutines();
        if (focused) focus!.Start(); else focus?.Stop();
    }

    internal void UpdateProcessMonitoring()
    {
        bool running = ProcessMonitor.IsRunning;
        RoutineAppStartMonitorDecision decision;
        lock (Sync)
        {
            decision = AppRoutineAppStartCoordinator.ResolveMonitorDecision(MonitoringEnabled, _stopping,
                ApplicationRoutines.Count, Lifetime.LeaseCount,
                Lifetime.Sessions.Any(static session => session.TriggerKind == RoutineTriggerKind.Application),
                running || _processMonitorStartPending);
            if (decision.Action == RoutineAppStartMonitorAction.Start) _processMonitorStartPending = true;
        }
        if (decision.Action == RoutineAppStartMonitorAction.Stop) { ProcessMonitor.Stop(); return; }
        if (decision.Action != RoutineAppStartMonitorAction.Start) return;
        ProcessLifecycleMonitorStartResult result = ProcessMonitor.Start();
        bool keep;
        lock (Sync)
        {
            _processMonitorStartPending = false;
            keep = !_stopping && MonitoringEnabled && (ApplicationRoutines.Count > 0 || Lifetime.LeaseCount > 0 ||
                Lifetime.Sessions.Any(static session => session.TriggerKind == RoutineTriggerKind.Application));
        }
        if (!keep) { ProcessMonitor.Stop(); return; }
        if (result.Success) _logger.Info("RoutineRuntimeCoordinator", "routine-application-trigger-monitor-active");
        else _logger.Warning("RoutineRuntimeCoordinator", () => $"routine-application-trigger-monitor-start-failed | reason={result.FailureReason ?? result.Status} appStartRoutinesAvailable=false");
    }

    internal void UpdateRoutingSessionMonitoring()
    {
        if (_audio == null) return;
        bool acquireOutput;
        bool releaseOutput;
        bool acquireInput;
        bool releaseInput;
        lock (Sync)
        {
            (int output, int input) = Lifetime.PendingLeaseCounts;
            bool desiredOutput = !_stopping && output > 0;
            bool desiredInput = !_stopping && input > 0;
            acquireOutput = desiredOutput && !_outputSessionMonitoring;
            releaseOutput = !desiredOutput && _outputSessionMonitoring;
            acquireInput = desiredInput && !_inputSessionMonitoring;
            releaseInput = !desiredInput && _inputSessionMonitoring;
            _outputSessionMonitoring = desiredOutput;
            _inputSessionMonitoring = desiredInput;
        }
        if (acquireOutput) _audio.AcquireSessionMonitoring(AudioMixerMode.Output); else if (releaseOutput) _audio.ReleaseSessionMonitoring(AudioMixerMode.Output);
        if (acquireInput) _audio.AcquireSessionMonitoring(AudioMixerMode.Input); else if (releaseInput) _audio.ReleaseSessionMonitoring(AudioMixerMode.Input);
    }

    internal bool QueueWork(Func<CancellationToken, Task> work, string name)
    {
        lock (Sync) return !_stopping && _workQueue.TryQueue(_tasks, _shutdown, work, name);
    }

    internal bool HasPendingWork(RoutineRuntimeWork kind) { lock (Sync) return _pendingWork.ContainsKey(kind); }

    internal bool QueueDebounced(RoutineRuntimeWork kind, int delayMs, Func<CancellationToken, Task> work, string name, bool replace = true)
    {
        CancellationTokenSource source;
        lock (Sync)
        {
            if (_stopping || (!replace && _pendingWork.ContainsKey(kind))) return false;
            CancelWork(kind);
            source = new();
            _pendingWork.Add(kind, source);
            bool queued = QueueWork(token => AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(source,
                owned => ReleaseWork(kind, owned), delayMs, work, token), name);
            if (!queued) CancelWork(kind);
            return queued;
        }
    }

    internal void QueueRoutingRefresh(Func<string, int, CancellationToken, Task> refresh)
    {
        lock (Sync)
        {
            if (_stopping || Lifetime.LeaseCount == 0) return;
            Interlocked.Increment(ref _routingSignals);
            QueueDebounced(RoutineRuntimeWork.RoutingRefresh, RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                token => refresh($"app-start-lease-refresh:{Guid.NewGuid():N}", Interlocked.Exchange(ref _routingSignals, 0), token), nameof(QueueRoutingRefresh));
        }
    }

    internal void CancelWork(RoutineRuntimeWork kind)
    {
        lock (Sync)
        {
            if (!_pendingWork.Remove(kind, out CancellationTokenSource? source)) return;
            try { source.Cancel(); }
            finally { source.Dispose(); }
        }
    }

    private void ReleaseWork(RoutineRuntimeWork kind, CancellationTokenSource source)
    {
        lock (Sync)
        {
            if (_pendingWork.TryGetValue(kind, out CancellationTokenSource? current) && ReferenceEquals(source, current)) _pendingWork.Remove(kind);
            source.Dispose();
        }
    }

    internal void BeginShutdown()
    {
        lock (Sync)
        {
            if (_stopping) return;
            _stopping = true;
            MonitoringEnabled = false;
            Lifetime.Stop();
            _workQueue.ClearDeferredOperations();
            _shutdown.Cancel();
            foreach (RoutineRuntimeWork kind in _pendingWork.Keys.ToArray()) CancelWork(kind);
            _systemEvents?.Dispose();
            if (_steamMonitor.IsValueCreated) _steamMonitor.Value.Signaled -= OnSteamSignal;
        }
        StopTriggers();
    }

    internal void StopTriggers()
    {
        if (_scheduleTriggerCoordinator.IsValueCreated) _scheduleTriggerCoordinator.Value.Dispose();
        if (_networkTriggerCoordinator.IsValueCreated) _networkTriggerCoordinator.Value.Dispose();
        if (_applicationTriggerCoordinator.IsValueCreated) _applicationTriggerCoordinator.Value.Dispose();
    }

    internal async Task DrainWorkAsync()
    {
        Task[] tasks = SnapshotPendingTasks();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (Sync) return new(_disposeTask ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        BeginShutdown();
        await DrainWorkAsync().ConfigureAwait(false);
        await Lifetime.DrainAsync().ConfigureAwait(false);
        UpdateRoutingSessionMonitoring();
        await Task.Factory.StartNew(() =>
        {
            try { ProcessMonitor.Dispose(); }
            finally
            {
                if (_steamMonitor.IsValueCreated)
                {
                    _steamMonitor.Value.Signaled -= OnSteamSignal;
                    _steamMonitor.Value.Dispose();
                }
                else Interlocked.Exchange(ref _unmaterializedSteamMonitor, null)?.Dispose();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void OnSteamSignal(SteamBigPictureSignal signal) { if (!_stopping) _callbacks.SteamSignaled(signal); }
}

using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators;

/// <summary>Coalesces unlock/resume triggers by wake cycle and waits for recovery and target enumeration before execution.</summary>
internal sealed class RoutineSystemEventCoordinator(
    Func<IReadOnlyList<AudioRoutine>> getRoutines,
    Func<bool, IReadOnlyList<CycleDevice>> getDevices,
    Func<AudioRoutine, CancellationToken, Task> execute,
    Logger logger,
    CancellationToken shutdownToken,
    Func<int, CancellationToken, Task>? delay = null,
    AudioDeviceService? audio = null,
    TimeProvider? timeProvider = null) : IDisposable
{
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReadinessWaitBudget = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Lock _sync = new();
    private readonly HashSet<RoutineTriggerKind> _pending = [];
    private readonly HashSet<string> _executed = [with(StringComparer.OrdinalIgnoreCase)];
    private CancellationTokenSource _cycle = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _suspended;
    private bool _recovering;
    private bool _unlocked;
    private bool _disposed;
    private bool _automaticPaused;
    private long _pauseGeneration;
    private long? _wakeId;

    internal void SetAutomaticPaused(bool paused)
    {
        lock (_sync)
        {
            _automaticPaused = paused;
            if (paused) { _pauseGeneration++; _pending.Clear(); }
        }
    }

    internal Task DrainAsync() { lock (_sync) return _worker; }

    internal void Suspend()
    {
        lock (_sync)
        {
            if (_disposed) return;
            ResetCycle();
            _suspended = true;
            _recovering = true;
            _wakeId = null;
            _unlocked = false;
        }
    }

    internal void LockSession()
    {
        lock (_sync)
        {
            if (_disposed) return;
            ResetCycle();
            _unlocked = false;
        }
    }

    internal void BeginResume(long wakeId)
    {
        lock (_sync)
        {
            if (_disposed || _wakeId == wakeId) return;
            _wakeId = wakeId;
            _suspended = false;
            _recovering = true;
            if (!_automaticPaused) _pending.Add(RoutineTriggerKind.SystemResume);
        }
    }

    internal void CompleteRecovery(long wakeId)
    {
        lock (_sync)
        {
            if (_disposed || _suspended || _wakeId != wakeId) return;
            _recovering = false;
            QueueWorker();
        }
    }

    internal void Unlock()
    {
        lock (_sync)
        {
            if (_disposed || _unlocked) return;
            _unlocked = true;
            if (!_automaticPaused) _pending.Add(RoutineTriggerKind.SessionUnlock);
            QueueWorker();
        }
    }

    private void ResetCycle()
    {
        _cycle.Cancel();
        _cycle.Dispose();
        _cycle = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        _pending.Clear();
        _executed.Clear();
    }

    private void QueueWorker()
    {
        if (_running || _disposed || _automaticPaused || _suspended || _recovering || _pending.Count == 0 || _cycle.IsCancellationRequested) return;
        _running = true;
        CancellationToken token = _cycle.Token;
        long pauseGeneration = _pauseGeneration;
        _worker = Task.Run(() => RunAsync(pauseGeneration, token), CancellationToken.None);
    }

    private async Task RunAsync(long pauseGeneration, CancellationToken token)
    {
        try
        {
            await DelayAsync(750, token);
            HashSet<RoutineTriggerKind> kinds;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (_recovering || _suspended || _automaticPaused || pauseGeneration != _pauseGeneration) return;
                kinds = [.. _pending];
                _pending.Clear();
            }
            IReadOnlyList<AudioRoutine>? configured = await ReadRoutinesAsync(pauseGeneration, token);
            if (configured == null) return;
            var routines = configured.Where(static routine => routine.Enabled && routine.HasExecutionTarget)
                .SelectMany(routine => routine.ExpandAutomaticTriggers(trigger => kinds.Contains(trigger.Kind)))
                .DistinctBy(static routine => routine.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            TimeSpan remainingReadinessWait = ReadinessWaitBudget;
            using var change = audio == null || routines.Length == 0 ? null : new DeviceStateChangeSignal(audio, _clock);
            foreach (AudioRoutine routine in routines)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync) if (_executed.Contains(routine.Id)) continue;
                try
                {
                    while (true)
                    {
                        long probeStarted = _clock.GetTimestamp();
                        try
                        {
                            if (TargetsAvailable(routine, token) || remainingReadinessWait <= TimeSpan.Zero) break;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) when (remainingReadinessWait > TimeSpan.Zero)
                        {
                            logger.Debug("RoutineSystemEvent", () => $"routine-target-discovery-retry | reason={ex.GetType().Name}");
                        }
                        TimeSpan interval = remainingReadinessWait < ReadinessPollInterval ? remainingReadinessWait : ReadinessPollInterval;
                        if (change == null)
                        {
                            await DelayAsync((int)interval.TotalMilliseconds, token);
                            remainingReadinessWait -= interval;
                        }
                        else
                        {
                            bool notified = await change.WaitAsync(interval, token);
                            TimeSpan elapsed = _clock.GetElapsedTime(probeStarted);
                            remainingReadinessWait -= notified || elapsed >= interval ? elapsed : interval;
                        }
                        lock (_sync) if (_automaticPaused || pauseGeneration != _pauseGeneration) return;
                    }
                    token.ThrowIfCancellationRequested();
                    string fingerprint = AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine);
                    IReadOnlyList<AudioRoutine>? currentRoutines = await ReadRoutinesAsync(pauseGeneration, token);
                    if (currentRoutines == null) return;
                    bool current = currentRoutines.Where(item => item.Enabled && string.Equals(item.Id, routine.Id, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(item => item.ExpandAutomaticTriggers(trigger => trigger.Id == routine.TriggerIdentity)).Any(item =>
                        item.RuntimeTriggerKey == routine.RuntimeTriggerKey && AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(item) == fingerprint);
                    if (!current) continue;
                    lock (_sync)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_automaticPaused || pauseGeneration != _pauseGeneration) return;
                        if (_recovering || _suspended) { _pending.UnionWith(kinds); return; }
                        if (!_executed.Add(routine.Id)) continue;
                    }
                    logger.Info("RoutineSystemEvent", () => $"routine-system-event | routineId={LogPrivacy.Id(routine.Id)} trigger={routine.TriggerKind}");
                    await execute(routine, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.Warning("RoutineSystemEvent", "routine-system-event-failed", nameof(RunAsync), ex); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { logger.Warning("RoutineSystemEvent", "routine-system-event-worker-failed", nameof(RunAsync), ex); }
        finally
        {
            lock (_sync) { _running = false; QueueWorker(); }
        }
    }

    private async Task<IReadOnlyList<AudioRoutine>?> ReadRoutinesAsync(long pauseGeneration, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync) if (_automaticPaused || pauseGeneration != _pauseGeneration) return null;
            try { return getRoutines(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < 2)
            {
                logger.Debug("RoutineSystemEvent", () => $"routine-snapshot-retry | attempt={attempt + 1} reason={ex.GetType().Name}");
                await DelayAsync(500, token);
            }
        }
    }

    private Task DelayAsync(int milliseconds, CancellationToken token) =>
        delay?.Invoke(milliseconds, token) ?? Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _clock, token);

    private bool TargetsAvailable(AudioRoutine routine, CancellationToken token)
    {
        var targets = new List<RoutineDeviceReference>();
        if (routine.HasOutputTarget) targets.Add(new() { Id = routine.OutputDeviceId, StableId = routine.OutputDeviceStableId });
        if (routine.HasInputTarget) targets.Add(new() { Id = routine.InputDeviceId, StableId = routine.InputDeviceStableId, Playback = false });
        if (routine.CommunicationsOutput != null) targets.Add(routine.CommunicationsOutput);
        if (routine.CommunicationsInput != null) targets.Add(routine.CommunicationsInput);
        if (routine.Conditions.DeviceAvailable && routine.Conditions.Device != null) targets.Add(routine.Conditions.Device);
        return targets.GroupBy(static target => target.Playback).All(group =>
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<CycleDevice> devices = getDevices(group.Key);
            return group.All(target => target.GetAvailability(devices) == true);
        });
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cycle.Cancel();
            _cycle.Dispose();
            _pending.Clear();
            _executed.Clear();
        }
    }
}

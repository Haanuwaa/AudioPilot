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
    Func<long>? getTickCount = null) : IDisposable
{
    private readonly Lock _sync = new();
    private readonly HashSet<RoutineTriggerKind> _pending = [];
    private readonly HashSet<string> _executed = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cycle = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _suspended;
    private bool _recovering;
    private bool _unlocked;
    private bool _resumeSeen;
    private bool _disposed;
    private long _recoveryVersion;
    private long _lastResumeTick;

    internal long RecoveryVersion { get { lock (_sync) return _recoveryVersion; } }

    internal Task DrainAsync() { lock (_sync) return _worker; }

    internal void Suspend()
    {
        lock (_sync)
        {
            if (_disposed) return;
            ResetCycle();
            _suspended = true;
            _recovering = true;
            _resumeSeen = false;
            _recoveryVersion++;
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

    internal void BeginResume()
    {
        lock (_sync)
        {
            if (_disposed) return;
            long now = (getTickCount ?? (() => Environment.TickCount64))();
            if (_resumeSeen && (_recovering || now - _lastResumeTick < 1000)) return;
            if (_resumeSeen) { ResetCycle(); _recoveryVersion++; _unlocked = false; }
            _lastResumeTick = now;
            _suspended = false;
            _recovering = true;
            _resumeSeen = true;
            _pending.Add(RoutineTriggerKind.SystemResume);
        }
    }

    internal void CompleteRecovery(long? recoveryVersion = null)
    {
        lock (_sync)
        {
            if (_disposed || _suspended || (recoveryVersion.HasValue && recoveryVersion.Value != _recoveryVersion)) return;
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
            _pending.Add(RoutineTriggerKind.SessionUnlock);
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
        if (_running || _disposed || _suspended || _recovering || _pending.Count == 0 || _cycle.IsCancellationRequested) return;
        _running = true;
        CancellationToken token = _cycle.Token;
        _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await (delay ?? Task.Delay)(750, token);
            HashSet<RoutineTriggerKind> kinds;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (_recovering || _suspended) return;
                kinds = [.. _pending];
                _pending.Clear();
            }
            var routines = getRoutines().Where(static routine => routine.Enabled && routine.HasExecutionTarget)
                .SelectMany(routine => routine.ExpandAutomaticTriggers(trigger => kinds.Contains(trigger.Kind)))
                .DistinctBy(static routine => routine.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            int readinessWaits = 0;
            foreach (AudioRoutine routine in routines)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync) if (_executed.Contains(routine.Id)) continue;
                try
                {
                    while (readinessWaits < 20 && !TargetsAvailable(routine))
                    {
                        readinessWaits++;
                        await (delay ?? Task.Delay)(500, token);
                    }
                    token.ThrowIfCancellationRequested();
                    string fingerprint = AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine);
                    bool current = getRoutines().Where(item => item.Enabled && string.Equals(item.Id, routine.Id, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(item => item.ExpandAutomaticTriggers(trigger => trigger.Id == routine.TriggerIdentity)).Any(item =>
                        item.RuntimeTriggerKey == routine.RuntimeTriggerKey && AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(item) == fingerprint);
                    if (!current) continue;
                    lock (_sync)
                    {
                        token.ThrowIfCancellationRequested();
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

    private bool TargetsAvailable(AudioRoutine routine)
    {
        var targets = new List<RoutineDeviceReference>();
        if (routine.HasOutputTarget) targets.Add(new() { Id = routine.OutputDeviceId, StableId = routine.OutputDeviceStableId });
        if (routine.HasInputTarget) targets.Add(new() { Id = routine.InputDeviceId, StableId = routine.InputDeviceStableId, Playback = false });
        if (routine.CommunicationsOutput != null) targets.Add(routine.CommunicationsOutput);
        if (routine.CommunicationsInput != null) targets.Add(routine.CommunicationsInput);
        if (routine.Conditions.DeviceAvailable && routine.Conditions.Device != null) targets.Add(routine.Conditions.Device);
        return targets.GroupBy(static target => target.Playback).All(group =>
        {
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

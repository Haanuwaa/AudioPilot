using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private Task _routineExitBaseline = Task.CompletedTask;

    private void QueueRoutineExitBaselineRefresh()
    {
        if (_isCleaningUp || !_routineRuntime.MonitoringEnabled || _routineRuntime.AutomaticPaused || !_routineRuntime.ApplicationExits.NeedsBaseline) return;
        long version = _routineRuntime.ApplicationExits.Generation;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _routineExitBaseline, completed.Task);
        var options = GetCaptureOptionsForTriggerTargets(_routineRuntime.ApplicationRoutines.Where(static routine => routine.ApplicationTriggerMode == ApplicationTriggerMode.AppExit).Select(static routine => routine.TriggerAppPath));
        if (!_routineRuntime.QueueWork(async token =>
        {
            try
            {
                var processes = await Task.Run(() => CaptureProcessSnapshots(options), token);
                if (!_isCleaningUp && !_routineRuntime.AutomaticPaused) _routineRuntime.ApplicationExits.Seed(processes, version);
            }
            finally { completed.TrySetResult(); }
        }, nameof(QueueRoutineExitBaselineRefresh))) completed.TrySetResult();
    }

    private async Task ExecuteRoutineExitTriggersAsync(int processId, IReadOnlyList<RoutineProcessSnapshot> processes,
        (long Configuration, long Automation) context, long observationVersion, CancellationToken token)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (_isCleaningUp || _routineRuntime.AutomaticPaused || context.Automation != _routineRuntime.AutomationGeneration || context.Configuration != _routineRuntime.ApplicationExits.Generation) return;
            var (routines, retry) = _routineRuntime.ApplicationExits.ObserveStop(processId, processes, context.Configuration, observationVersion);
            foreach (var routine in routines)
            {
                if (!_routineRuntime.ApplicationExits.TryClaim(routine)) continue;
                try
                {
                    await ExecuteRoutineOnDispatcherAsync(routine, showOverlay: true, applicationProcessId: null,
                        executionSource: "application-exit", cancellationToken: token);
                }
                finally { _routineRuntime.ApplicationExits.Complete(routine); }
            }
            if (!retry) return;
            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), token);
                var options = GetCaptureOptionsForTriggerTargets(_routineRuntime.ApplicationRoutines.Where(static routine => routine.ApplicationTriggerMode == ApplicationTriggerMode.AppExit).Select(static routine => routine.TriggerAppPath));
                observationVersion = _routineRuntime.ApplicationExits.ObservationVersion;
                processes = await Task.Run(() => CaptureProcessSnapshots(options), token);
            }
        }
        if (_routineRuntime.ApplicationExits.ForgetUnverifiedStop(processId, context.Configuration, observationVersion))
            _logger.Warning("RoutineApplicationExit", "routine-exit-discovery-unavailable");
    }
}

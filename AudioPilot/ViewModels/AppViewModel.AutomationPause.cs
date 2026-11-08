namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    public Cli.CliExecutionResult SetAutomaticRoutinePauseFromCli(bool paused, int? minutes, bool jsonOutput)
    {
        if (IsRuntimeShutdownRequested || _isCleaningUp || ShutdownToken.IsCancellationRequested)
            return Cli.CliCommandExecutor.BuildRuntimeUnavailableResult(jsonOutput);
        if (paused && minutes is < 1 or > Constants.AppConstants.Routines.AutomaticPauseMaxMinutes)
            return Cli.CliCommandExecutor.BuildExecutionFailureResult(2, "invalid-routine-pause-duration", "Pause duration must be a whole number from 1 to 1440 minutes.", jsonOutput);
        if (paused) PauseAutomaticRoutines(minutes.HasValue ? TimeSpan.FromMinutes(minutes.Value) : null);
        else ResumeAutomaticRoutines();
        var state = _routineRuntime.CaptureWorkSnapshot();
        return new(0, Cli.CliOutputFormatter.FormatAutomaticRoutineState(state.AutomaticPaused, state.AutomaticResumeUtc, HasAutomaticRoutines, jsonOutput));
    }

    private bool _lastNotifiedAutomaticPause;
    public bool AutomaticRoutinesPaused => _routineRuntime.AutomaticPaused;
    internal bool HasAutomaticRoutines
    {
        get
        {
            lock (_settingsLock)
                return _cachedSettings?.Routines.Items.Any(static routine => routine.Enabled &&
                    routine.Triggers.Any(static trigger => trigger != null && trigger.Kind != Models.RoutineTriggerKind.Hotkey)) == true;
        }
    }
    public System.Windows.Input.ICommand ResumeAutomaticRoutinesCommand { get; private set; } = null!;
    internal void PauseAutomaticRoutines(TimeSpan? duration)
    {
        _routineRuntime.PauseAutomatic(duration);
        NotifyAutomaticPauseState();
    }
    internal void ResumeAutomaticRoutines()
    {
        _routineRuntime.ResumeAutomatic();
        NotifyAutomaticPauseState();
    }

    private void NotifyAutomaticPauseState()
    {
        void Publish()
        {
            if (_isCleaningUp || _lastNotifiedAutomaticPause == AutomaticRoutinesPaused) return;
            _lastNotifiedAutomaticPause = AutomaticRoutinesPaused;
            OnPropertyChanged(nameof(AutomaticRoutinesPaused));
            OnPropertyChanged(nameof(SelectedRoutineSchedulePreview));
        }
        if (_dispatcher.CheckAccess()) Publish();
        else if (!AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
        {
            try { _ = _dispatcher.BeginInvoke((Action)Publish); }
            catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher)) { }
        }
    }

    private void OnAutomaticRoutinesResumed()
    {
        NotifyAutomaticPauseState();
        QueueRoutineExitBaselineRefresh();
        long generation = _routineRuntime.AutomationGeneration;
        _routineRuntime.QueueWork(async token =>
        {
            await InvokeOnDispatcherAsync(() =>
            {
                if (_isCleaningUp) return;
                if (_routineRuntime.AutomaticPaused || generation != _routineRuntime.AutomationGeneration) return;
                _routineRuntime.ReevaluateAutomaticState();
                if (!_routineRuntime.Lifetime.Sessions.Any(static session => session.TriggerKind == Models.RoutineTriggerKind.SteamBigPicture))
                    lock (_routineRuntime.Sync) _steamBigPictureDetected = false;
                QueueSteamBigPictureSignalEvaluation();
            });
            if (_isCleaningUp || _routineRuntime.AutomaticPaused || generation != _routineRuntime.AutomationGeneration) return;
            Models.AudioRoutine[] watched;
            lock (_routineRuntime.Sync) watched = [.. _routineRuntime.ApplicationRoutines.Where(static routine => routine.ApplicationTriggerMode == Models.ApplicationTriggerMode.AppLaunch)];
            if (watched.Length == 0) return;
            var processes = await Task.Run(() => CaptureProcessSnapshots(GetCaptureOptionsForTriggerTargets(watched.Select(static routine => routine.TriggerAppPath))), token);
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                if (_routineRuntime.AutomaticPaused || generation != _routineRuntime.AutomationGeneration) return;
                if (Services.Routines.RoutineApplicationRouting.EvaluateRoutineAppStartMatchesForProcess(watched, process).Count > 0)
                    OnRoutineAppProcessStarted(process.ProcessId);
            }
        }, nameof(OnAutomaticRoutinesResumed));
    }
}

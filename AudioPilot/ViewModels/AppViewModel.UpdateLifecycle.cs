using AudioPilot.Coordinators;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private int _updatePreparationBlocked;
    internal bool IsUpdatePreparationBlocked => Volatile.Read(ref _updatePreparationBlocked) != 0;
    internal bool IsUpdateAdmissionBlocked => IsUpdatePreparationBlocked || Services.Updates.AppUpdateLifecycleSession.Current?.StartupBlocked == true;

    internal string? GetUpdateBusyReason()
    {
        if (Volatile.Read(ref _runtimeShutdownStarted) != 0 || _isCleaningUp) return "stopping";
        if (_isApplyingSettings || _isSaving || IsSavingRoutines || IsResettingSettings) return "settings-busy";
        if (_isResettingPerAppAudio) return "interaction-active";
        if (AudioTesting.IsInputTestRunning || AudioTesting.IsOutputTestRunning) return "audio-test-active";
        if (_routineRuntime.Lifetime.HasPendingActivations || _routineRuntime.Lifetime.Sessions.Count != 0 || _routineRuntime.Lifetime.LeaseCount != 0 || _routineRuntime.CaptureWorkSnapshot().ActiveTasks != 0)
            return "routine-work-active";
        return null;
    }

    internal void SetUpdatePreparationBlocked(bool blocked) => Volatile.Write(ref _updatePreparationBlocked, blocked ? 1 : 0);

    internal bool HasPendingUpdateEdits => HasUiSettingsDivergedFromCachedSettings() || HasSettingsDraftDivergedFromCachedSettings() || HasRoutineEdits();

    internal async Task<bool> FlushForUpdateAsync(CancellationToken token)
    {
        await WaitForActiveSettingsSavesAsync(token);
        if (!IsPersistedAutoSaveEnabled())
        {
            return await InvokeOnDispatcherAsync(() => !HasPendingUpdateEdits, fallback: false);
        }

        AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _autoSaveDebounceCts)?.Dispose();
        AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts)?.Dispose();
        await InvokeOnDispatcherAsync(() => RunAutoSaveAsync("update-preparation")).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        return await InvokeOnDispatcherAsync(
            () => !HasPendingUpdateEdits && !_isSaving && !_isApplyingSettings && !IsSavingRoutines,
            fallback: false);
    }
}

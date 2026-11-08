using System.Windows;

namespace AudioPilot;

internal sealed partial class AppRuntimeHost
{
    private IDisposable? _updateMicrophoneSuspension;
    private readonly List<Window> _updateDisabledWindows = [];
    private bool _updatePrepared;
    private volatile bool _updatePreparationActive;
    private bool _lastShutdownDrained;
    internal bool IsUpdateAdmissionBlocked => _updatePreparationActive || Services.Updates.AppUpdateLifecycleSession.Current?.StartupBlocked == true;
    private bool IsUserWorkBlocked() => IsShutdownRequested() || IsUpdateAdmissionBlocked;
    internal UpdateProtocol.UpdateRuntimeInfo GetUpdateRuntimeInfo() => new(AppContext.BaseDirectory,
        _runtimeServices.SettingsService.GetSettingsPath(), Platform.AppDataPaths.GetLocalStateRoot());

    internal async Task<string?> PrepareForUpdateAsync(CancellationToken token)
    {
        if (_updatePrepared) return null;
        if (System.Windows.Input.Mouse.Captured != null) return "interaction-active";
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal || _application.Windows.Cast<Window>().Any(window => window.IsVisible && !ReferenceEquals(window, _windowManager.CurrentWindow)))
            return "dialog-active";
        if (!_startupResumeCoordinator.DrainResumeAsync().IsCompleted) return "resume-in-progress";
        string? busy = _appVm.GetUpdateBusyReason();
        if (busy != null) return busy;
        if (_microphoneHold.IsControlling || _appVm.CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled == true) return "microphone-owned";
        _updateMicrophoneSuspension = _microphoneHold.TrySuspendIdleForUpdate();
        if (_updateMicrophoneSuspension == null) return "microphone-owned";
        _updatePreparationActive = true;
        try
        {
            _appVm.SetUpdatePreparationBlocked(true);
            _trayService.SetUpdateActionsSuspended(true);
            foreach (Window window in _application.Windows)
            {
                if (!window.IsEnabled) continue;
                _updateDisabledWindows.Add(window);
                window.IsEnabled = false;
            }
            if (!await _appVm.FlushForUpdateAsync(token)) return "unsaved-changes";
            if (_appVm.CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled == true) return "microphone-owned";
            _updatePrepared = true;
            return null;
        }
        finally
        {
            if (!_updatePrepared) CancelUpdatePreparation();
        }
    }

    internal void CancelUpdatePreparation()
    {
        _updatePrepared = false;
        _updateMicrophoneSuspension?.Dispose();
        _updateMicrophoneSuspension = null;
        _appVm.SetUpdatePreparationBlocked(false);
        _updatePreparationActive = false;
        if (IsShutdownRequested()) { _updateDisabledWindows.Clear(); return; }
        _trayService.SetUpdateActionsSuspended(false);
        foreach (Window window in _updateDisabledWindows) window.IsEnabled = true;
        _updateDisabledWindows.Clear();
        OnPushToTalkModeChanged(_appVm.CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled ?? false);
    }

    internal async Task<bool> ShutdownForUpdateAsync(CancellationToken token)
    {
        if (!_updatePrepared || !await _appVm.FlushForUpdateAsync(token))
        {
            CancelUpdatePreparation();
            return false;
        }
        await RequestShutdownAsync("update-restart", exitApplication: false);
        return _lastShutdownDrained;
    }
}

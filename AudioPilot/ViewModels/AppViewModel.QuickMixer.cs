using System.Windows;
using System.Windows.Interop;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Metering;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private QuickMixerWindow? _quickMixerWindow;
    private nint _quickMixerHandle;
    private MixerVisibilityDemand? _mixerPresentationDemand;
    private AudioMeterSampler? _quickMixerMeters;

    private AudioMixerMode _lastQuickMixerMode;
    private long _lastQuickMixerClosedTicks;
    internal QuickPanelLayoutStore? QuickPanelLayouts { private get; set; }

    private void OnMeterDevicesChanged()
    {
        Volatile.Write(ref _skipNextVisibleWindowRestoreRefresh, 0);
        _quickMixerMeters?.RequestRefresh();
    }

    internal void ToggleQuickMixerFromTray()
    {
        _dispatcher.VerifyAccess();
        if (_isCleaningUp || IsRuntimeShutdownRequested) return;
        if (_quickMixerWindow == null && _lastQuickMixerClosedTicks > 0
            && Environment.TickCount64 - _lastQuickMixerClosedTicks < 250)
        {
            return;
        }
        ToggleQuickMixer();
    }

    internal void ToggleQuickMixer(Func<bool>? canPresent = null)
    {
        _dispatcher.VerifyAccess();
        if (_isCleaningUp || IsRuntimeShutdownRequested) return;
        if (_quickMixerWindow != null)
        {
            if (_quickMixerWindow.IsPinned) ShowQuickMixer(); else _quickMixerWindow.Dismiss();
            return;
        }
        ShowQuickMixer(canPresent);
    }

    internal void ShowQuickMixer(Func<bool>? canPresent = null)
    {
        _dispatcher.VerifyAccess();
        if (_isCleaningUp || IsRuntimeShutdownRequested) return;
        if (_quickMixerWindow != null)
        {
            if (_quickMixerWindow.WindowState == WindowState.Minimized) _quickMixerWindow.WindowState = WindowState.Normal;
            _ = _quickMixerWindow.Activate(); return;
        }
        bool mainActive = Application.Current?.MainWindow is MainWindow { IsActive: true, IsVisible: true } main
            && ReferenceEquals(main.AppViewModel, this);
        AudioMixerMode mode = mainActive && SelectedSettingsTabIndex is 0 or 1
            ? (SelectedSettingsTabIndex == 0 ? AudioMixerMode.Output : AudioMixerMode.Input) : _lastQuickMixerMode;
        if (_quickMixerMeters is { Completion.IsCompleted: true } retired)
        {
            // Restart only after the previous owner has exited; never accumulate
            // replacement workers behind a stalled native call.
            if (retired.Completion.Exception is { } error)
                _logger.Warning("QuickMixer", "quick-mixer-meter-worker-restarting", nameof(ShowQuickMixer), error);
            _quickMixerMeters = null;
        }
        _quickMixerMeters ??= new AudioMeterSampler(logger: _logger);
        Window? previousMainWindow = Application.Current?.MainWindow;
        var window = new QuickMixerWindow(
            selected => selected == AudioMixerMode.Output ? Mixer : InputMixer,
            AcquireMixerPresentation,
            RefreshQuickMixerAsync,
            _quickMixerMeters,
            QuickDevicePickerWindow.GetForegroundWindow(), mode, layouts: QuickPanelLayouts, muteControls: this, dialogs: _dialogs, canPresent: canPresent);
        // WPF implicitly chooses its first window as MainWindow. A tray-first
        // flyout must not become the application's main window or retain itself.
        if (Application.Current is { } application && ReferenceEquals(application.MainWindow, window))
            application.MainWindow = previousMainWindow;
        _quickMixerWindow = window;
        window.SourceInitialized += (_, _) => Volatile.Write(ref _quickMixerHandle, new WindowInteropHelper(window).Handle);
        window.Closed += (_, _) => { _lastQuickMixerMode = window.Mode; _lastQuickMixerClosedTicks = Environment.TickCount64; _quickMixerWindow = null; Volatile.Write(ref _quickMixerHandle, 0); };
        try { window.Show(); }
        catch { window.Close(); _quickMixerWindow = null; throw; }
    }

    internal IDisposable AcquireMixerPresentation(AudioMixerMode mode)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_isCleaningUp || IsRuntimeShutdownRequested, this);
        IDisposable lease = (_mixerPresentationDemand ??= new()).Acquire(mode);
        TryGetMixer(mode)?.CancelIdleTrim();
        UpdateMixerSessionMonitoringState("quick-mixer-visible");
        if (_audio != null) RunBackgroundWork(_ => _audio.ResumeIdleSessionCacheCleanupAsync(), "resume-quick-mixer-cache-cleanup");
        return new MixerPresentationLease(() =>
        {
            _dispatcher.VerifyAccess();
            lease.Dispose();
            if (_isCleaningUp) return;
            UpdateMixerSessionMonitoringState("quick-mixer-hidden");
            if (!_isWindowVisible)
            {
                TrimMixersForIdleState("quick-mixer-hidden");
                if (!TryGetVisibleMixerRefreshTarget(out _) && _audio != null)
                    RunBackgroundWork(_ => _audio.PauseIdleSessionCacheCleanupAsync(), "pause-quick-mixer-cache-cleanup");
            }
        });
    }

    private AudioMeterModes GetVisibleMixerModes()
    {
        AudioMeterModes modes = _mixerPresentationDemand?.Current.Modes ?? AudioMeterModes.None;
        if (_isWindowVisible && TryGetSelectedMixerMode(SelectedSettingsTabIndex, out AudioMixerMode mode))
            modes |= mode == AudioMixerMode.Output ? AudioMeterModes.Output : AudioMeterModes.Input;
        return modes;
    }

    private async Task RefreshQuickMixerAsync(AudioMixerMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isCleaningUp || IsRuntimeShutdownRequested) return;
        Task refresh = RefreshVisibleMixerAsync(GetMixerRefreshTarget(mode), true);
        try { await refresh.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = refresh.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private Task RefreshVisibleMixerAsync(MixerRefreshTarget requested, bool interactive)
    {
        if (_isCleaningUp || IsRuntimeShutdownRequested || !TryGetVisibleMixerRefreshTarget(out var visible)) return Task.CompletedTask;
        if (requested == MixerRefreshTarget.Both) return RefreshMixerAsync(visible, interactive);
        return visible == MixerRefreshTarget.Both || visible == requested ? RefreshMixerAsync(requested, interactive) : Task.CompletedTask;
    }

    internal async Task StopQuickMixerMetersAsync(CancellationToken cancellationToken)
    {
        var meters = Interlocked.Exchange(ref _quickMixerMeters, null);
        if (meters == null) return;
        try { await meters.StopAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = meters.Completion.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private sealed class MixerPresentationLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

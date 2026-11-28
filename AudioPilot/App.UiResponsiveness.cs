using System.Diagnostics;
using System.Windows.Threading;

namespace AudioPilot;

public partial class App
{
    private UiResponsivenessMonitor? _uiResponsiveness;
    private Mutex? _uiRecoveryLease;

    internal Task StopUiResponsivenessMonitorAsync() => _uiResponsiveness?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    internal void NotifyUiResponsivenessPowerMode(Microsoft.Win32.PowerModes mode) => _uiResponsiveness?.NotifyFallbackPowerModeChanged(mode);

    private void StartUiResponsivenessMonitor(AppRuntimeHost host)
    {
        using Process current = Process.GetCurrentProcess();
        var owner = new SingleInstanceOwnerIdentity(current.Id, current.StartTime.ToUniversalTime().Ticks);
        _uiResponsiveness = new UiResponsivenessMonitor(
            token => Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Input, token).Task,
            () => _startupCancellation.IsCancellationRequested || !IsRuntimeReady || Debugger.IsAttached
                || host.AppViewModel.IsRuntimeShutdownRequested || host.IsUpdateAdmissionBlocked
                || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished,
            (_, token) =>
            {
                // Recheck immediately before launch; state may have changed since the heartbeat sample.
                token.ThrowIfCancellationRequested();
                if (_startupCancellation.IsCancellationRequested || Debugger.IsAttached
                    || host.AppViewModel.IsRuntimeShutdownRequested || host.IsUpdateAdmissionBlocked) throw new OperationCanceledException(token);
                using Process recovery = Process.Start(UiStallRecoveryLaunch.CreateStartInfo(Environment.ProcessPath!, owner))
                    ?? throw new InvalidOperationException("The recovery process did not start.");
            }, _logger, subscribePowerChanges: SystemPowerNotifications.RegisterCallback);
        _uiResponsiveness.Start();
    }

    private bool TryEnterUiRecovery(SingleInstanceOwnerIdentity owner)
    {
        // Arguments grant no authority: verify path, Windows user/session, PID and creation time first.
        using ISingleInstanceRecoveryTarget? target = SingleInstanceProcessRecoveryHelper.TryOpen(owner, _logger);
        if (target is not { HasExited: false }) return false;
        var lease = new Mutex(false, SingleInstanceScope.Current.Qualify("AudioPilot.UiRecovery"), out bool created);
        if (!created) { lease.Dispose(); return false; }
        _uiRecoveryLease = lease;
        return true;
    }
}

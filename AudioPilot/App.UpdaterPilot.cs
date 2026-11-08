using System.Windows;
using AudioPilot.Services.Updates;

namespace AudioPilot;

public partial class App
{
    private Task? _pilotLaunchTask;
    internal async Task OpenUpdaterPilotAsync(Window owner)
    {
        if (_pilotLaunchTask is { IsCompleted: false } || _runtimeHost?.AppViewModel.IsRuntimeShutdownRequested != false || !AppUpdateLauncher.IsAvailable) return;
        try
        {
            string session = AppUpdateLifecycleSession.AttachmentSession ?? throw new InvalidOperationException("No authorized pilot attachment session.");
            string theme = _runtimeHost.AppViewModel.Theme.ToString().ToLowerInvariant();
            _pilotLaunchTask = Task.Run(() => AppUpdateLauncher.Launch(session, theme, _startupCancellation.Token));
            await _pilotLaunchTask;
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_startupCancellation.IsCancellationRequested || _runtimeHost?.AppViewModel.IsRuntimeShutdownRequested != false) return;
            _logger.Warning("UpdatePilot", "The updater could not open.", nameof(OpenUpdaterPilotAsync), error);
            try
            {
                await AppDialogServiceProvider.Current.ShowAsync(new("The updater could not open. The installed app has not been changed.\n\n" + error.Message,
                    "AudioPilot Update", AppDialogKind.Error, [new("Close", AppDialogResult.Acknowledged, isDefault: true, isCancel: true)], owner), _startupCancellation.Token);
            }
            catch (Exception closing) when (_startupCancellation.IsCancellationRequested && closing is OperationCanceledException or ObjectDisposedException) { }
        }
    }
}

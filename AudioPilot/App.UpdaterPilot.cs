using System.ComponentModel;
using System.Windows;
using AudioPilot.Localization;
using AudioPilot.Services.Updates;

namespace AudioPilot;

public partial class App
{
    private Task? _pilotLaunchTask;
    private Task? _pilotLanguageTask;
    private LocalizationService? _pilotLocalization;
    internal async Task OpenUpdaterPilotAsync(Window owner)
    {
        if (_pilotLaunchTask is { IsCompleted: false } || _runtimeHost?.AppViewModel.IsRuntimeShutdownRequested != false || !AppUpdateLauncher.IsAvailable) return;
        try
        {
            string session = AppUpdateLifecycleSession.AttachmentSession ?? throw new InvalidOperationException("No authorized pilot attachment session.");
            string theme = _runtimeHost.AppViewModel.Theme.ToString().ToLowerInvariant();
            if (_pilotLocalization == null)
            {
                _pilotLocalization = _runtimeHost.AppViewModel.Localization;
                PropertyChangedEventManager.AddHandler(_pilotLocalization, OnPilotLanguageChanged, nameof(LocalizationService.Revision));
            }
            _pilotLaunchTask = Task.Run(() => AppUpdateLauncher.LaunchAsync(session, theme, _startupCancellation.Token));
            await _pilotLaunchTask;
            OnPilotLanguageChanged(null, null);
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

    private void OnPilotLanguageChanged(object? sender, PropertyChangedEventArgs? args)
    {
        if (_pilotLocalization != null && !_startupCancellation.IsCancellationRequested && _pilotLanguageTask is not { IsCompleted: false })
            _pilotLanguageTask = SyncPilotLanguageAsync(_pilotLocalization);
    }

    private async Task SyncPilotLanguageAsync(LocalizationService localization)
    {
        try
        {
            int revision;
            do
            {
                revision = localization.Revision;
                await AppUpdateLauncher.RefreshLanguageAsync(localization.Language, _startupCancellation.Token);
            } while (revision != localization.Revision && !_startupCancellation.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.Debug("UpdatePilot", $"The updater language could not be refreshed ({error.GetType().Name}).", nameof(SyncPilotLanguageAsync));
        }
    }
}

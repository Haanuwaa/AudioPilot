using System.ComponentModel;
using System.Windows;
using AudioPilot.Localization;
using AudioPilot.Services.Updates;
using AudioPilot.UpdateProtocol;
using AudioPilot.ViewModels;

namespace AudioPilot;

public partial class App
{
    private Task? _pilotLaunchTask;
    private Task? _pilotPresentationTask;
    private int _pilotPresentationRevision;
    private AppViewModel? _pilotViewModel;

    internal async Task OpenUpdaterPilotAsync(Window owner)
    {
        if (_pilotLaunchTask is { IsCompleted: false } || _runtimeHost?.AppViewModel.IsRuntimeShutdownRequested != false || !AppUpdateLauncher.IsAvailable) return;
        _pilotLaunchTask = OpenUpdaterWindowAsync(owner);
        try { await _pilotLaunchTask; }
        finally { _pilotLaunchTask = null; }
    }

    private async Task OpenUpdaterWindowAsync(Window owner)
    {
        var viewModel = _runtimeHost?.AppViewModel;
        if (viewModel == null || viewModel.IsRuntimeShutdownRequested) return;
        try
        {
            if (_pilotViewModel == null)
            {
                _pilotViewModel = viewModel;
                PropertyChangedEventManager.AddHandler(viewModel.Localization, OnPilotPresentationChanged, nameof(LocalizationService.Revision));
                PropertyChangedEventManager.AddHandler(viewModel, OnPilotPresentationChanged, nameof(AppViewModel.Theme));
            }
            if (await AppUpdateLauncher.TryShowExistingAsync(viewModel.Theme, _startupCancellation.Token))
            {
                QueuePilotPresentationSync();
                return;
            }
            string session = AppUpdateLifecycleSession.AttachmentSession ?? throw new InvalidOperationException("No authorized pilot attachment session.");
            await AppUpdateLauncher.LaunchAsync(
                session,
                viewModel.Theme,
                static () => Task.FromResult<UpdaterWindowPlacement?>(null),
                _startupCancellation.Token);
            QueuePilotPresentationSync();
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

    private void OnPilotPresentationChanged(object? sender, PropertyChangedEventArgs args) => QueuePilotPresentationSync();

    private void DetachPilotPresentationHandlers()
    {
        if (_pilotViewModel is not { } viewModel) return;
        _pilotViewModel = null;
        PropertyChangedEventManager.RemoveHandler(viewModel.Localization, OnPilotPresentationChanged, nameof(LocalizationService.Revision));
        PropertyChangedEventManager.RemoveHandler(viewModel, OnPilotPresentationChanged, nameof(AppViewModel.Theme));
    }

    private void QueuePilotPresentationSync()
    {
        if (_pilotViewModel is not { IsRuntimeShutdownRequested: false } viewModel || _startupCancellation.IsCancellationRequested) return;
        _pilotPresentationRevision++;
        if (_pilotPresentationTask is not { IsCompleted: false })
            _pilotPresentationTask = SyncPilotPresentationAsync(viewModel, _startupCancellation.Token);
    }

    private async Task SyncPilotPresentationAsync(AppViewModel viewModel, CancellationToken token)
    {
        try
        {
            var localization = viewModel.Localization;
            int revision;
            do
            {
                revision = _pilotPresentationRevision;
                await AppUpdateLauncher.RefreshPresentationAsync(localization.Language, viewModel.Theme, token);
            } while (revision != _pilotPresentationRevision && !token.IsCancellationRequested && !viewModel.IsRuntimeShutdownRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.Debug("UpdatePilot", $"The updater presentation could not be refreshed ({error.GetType().Name}).", nameof(SyncPilotPresentationAsync));
        }
    }
}

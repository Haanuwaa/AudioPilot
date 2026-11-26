using System.ComponentModel;
using System.Windows;
using AudioPilot.Localization;
using AudioPilot.Services.Updates;
using AudioPilot.ViewModels;

namespace AudioPilot;

public partial class App
{
    private Task? _pilotLaunchTask;
    private Task? _pilotPresentationTask;
    private int _pilotPresentationRevision;
    private AppViewModel? _pilotViewModel;
    private UpdateLaunchWindow? _pilotPreparation;
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
        UpdateLaunchWindow? opening = null;
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
            _pilotPreparation?.Close();
            using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(_startupCancellation.Token);
            Task launchTask = AppUpdateLauncher.LaunchAsync(
                session,
                viewModel.Theme,
                () => Dispatcher.InvokeAsync(() => opening is { IsClosed: false, IsRevealed: true } ? opening.CapturePlacement() : null).Task,
                launchCts.Token);
            Task delayTask = Task.Delay(250, _startupCancellation.Token);
            Task completed = await Task.WhenAny(launchTask, delayTask);
            if (completed == launchTask || _startupCancellation.IsCancellationRequested)
            {
                await launchTask;
            }
            else
            {
                var preparation = new UpdateLaunchWindow(owner, viewModel.Localization, _startupCancellation.Token);
                opening = preparation;
                _pilotPreparation = preparation;
                preparation.Closed += (_, _) => { if (ReferenceEquals(_pilotPreparation, preparation)) _pilotPreparation = null; };
                await preparation.TrackAsync(launchTask, () => { try { launchCts.Cancel(); } catch (ObjectDisposedException) { } });
            }
            QueuePilotPresentationSync();
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (opening?.IsClosed == true || _startupCancellation.IsCancellationRequested || _runtimeHost?.AppViewModel.IsRuntimeShutdownRequested != false) return;
            _logger.Warning("UpdatePilot", "The updater could not open.", nameof(OpenUpdaterPilotAsync), error);
            if (_pilotPreparation is { } preparation) { preparation.ShowFailure(); return; }
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

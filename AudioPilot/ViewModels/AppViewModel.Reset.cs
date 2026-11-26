using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private TaskCompletionSource? _settingsResetCompletion;

    private async Task ResetToDefaultsAsync()
    {
        if (IsRuntimeShutdownRequested || IsResettingSettings || _isApplyingSettings || _isSaving || IsSavingRoutines || _isCleaningUp) return;

        CancellationToken shutdownToken = ShutdownToken;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsResetCompletion = completion;
        bool clearingStartupRegistration = false;
        try
        {
            IsResettingSettings = true;
            using (SuppressAutoSave())
            {
                AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _autoSaveDebounceCts)?.Dispose();
                Settings? current = GetCachedSettingsSnapshot();
                ResetDefaultsPromptPlan promptPlan = AppResetCoordinator.BuildResetDefaultsPromptPlan(
                    _settings.SettingsFileExists(),
                    OutputCycleDevices.Count > 0 || InputCycleDevices.Count > 0,
                    Routines.Count > 0 || current?.Routines.Items.Count > 0,
                    Hotkey.HasMainInput || InputHotkey.HasMainInput || OutputReverseHotkey.HasMainInput || InputReverseHotkey.HasMainInput,
                    RunAtStartup || current?.Startup.Enabled == true,
                    hasUnsavedChanges: HasPendingLocalEditsForRefresh());

                if (promptPlan.ShouldSkip)
                {
                    _logger.Info("AppViewModel", AppResetCoordinator.BuildResetSkipLogMessage());
                    await _dialogs.ShowInformationAsync(promptPlan.DialogMessage ?? string.Empty, promptPlan.DialogCaption, cancellationToken: shutdownToken);
                    return;
                }

                AppDialogResult resetResult = await _dialogs.ShowAsync(new AppDialogRequest(
                    promptPlan.DialogMessage ?? string.Empty,
                    promptPlan.DialogCaption,
                    promptPlan.DialogKind,
                    [
                        new AppDialogAction("_Reset", AppDialogResult.Confirmed, AppDialogActionStyle.Destructive),
                        new AppDialogAction("_Cancel", AppDialogResult.Declined, AppDialogActionStyle.Secondary, isDefault: true, isCancel: true),
                    ]), shutdownToken);
                if (IsRuntimeShutdownRequested || shutdownToken.IsCancellationRequested) return;
                if (!AppResetCoordinator.ShouldProceed(resetResult))
                {
                    _logger.Info("AppViewModel", "reset-cancelled");
                    return;
                }

                bool reset = await ExecuteSettingsWriteAsync(async () =>
                {
                    if (IsRuntimeShutdownRequested || shutdownToken.IsCancellationRequested) return false;
                    AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts)?.Dispose();
                    bool committed = await Task.Run(() =>
                    {
                        if (IsRuntimeShutdownRequested || shutdownToken.IsCancellationRequested) return false;
                        _logger.Info("AppViewModel", "reset-start");
                        clearingStartupRegistration = true;
                        _startup.ApplyRegistration(enabled: false, useScheduledTask: false, persistSettings: () =>
                        {
                            clearingStartupRegistration = false;
                            _settings.DeleteSettingsFiles();
                        });
                        return true;
                    }, shutdownToken);
                    if (!committed) return false;

                    await InvokeOnDispatcherAsync(() =>
                    {
                        _hotkeys.UnregisterAllHotkeys();
                        ApplyExternallyReloadedSettings(new Settings(), context: "reset-defaults");
                        SelectedOutputCycleDevices.Clear();
                        SelectedInputCycleDevices.Clear();
                        SelectedOutputCycleIndex = -1;
                        SelectedInputCycleIndex = -1;
                        SelectedAvailableOutputIndex = -1;
                        SelectedAvailableInputIndex = -1;
                        _disconnectedOutputSelection = null;
                        _disconnectedInputSelection = null;
                        _windowState.ShowBalloonOnFirstMinimize = true;
                        ShowBalloonAfterSave = false;
                        OnPropertyChanged(nameof(RunAtStartup));
                        UpdateLastSettingsWriteTime();
                    });
                    return true;
                }, cancellationToken: shutdownToken);
                if (reset) _logger.Info("AppViewModel", "reset-complete");
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Error("AppViewModel", "reset-defaults-failed", nameof(ResetToDefaultsAsync), ex);
            if (!IsRuntimeShutdownRequested)
            {
                string messageKey = clearingStartupRegistration ? "CommandError_SettingsResetStartupFailed" : "CommandError_SettingsResetFailed";
                await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(BuildCommandFailureMessage(messageKey),
                    DialogText.Captions.Error, AppDialogKind.Error), shutdownToken);
            }
        }
        finally
        {
            IsResettingSettings = false;
            completion.TrySetResult();
            _settingsResetCompletion = null;
            if (!IsRuntimeShutdownRequested && IsPersistedAutoSaveEnabled() && HasPendingLocalEditsForRefresh())
            {
                QueueAutoSave(nameof(ResetToDefaultsAsync));
            }
        }
    }
}

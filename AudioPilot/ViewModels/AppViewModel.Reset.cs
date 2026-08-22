using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private async Task ResetToDefaultsAsync()
    {
        if (IsResettingSettings || _isApplyingSettings || _isSaving || IsSavingRoutines || _isCleaningUp) return;

        IsResettingSettings = true;
        try
        {
            using (SuppressAutoSave())
            {
                AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _autoSaveDebounceCts)?.Dispose();
                Settings? current = GetCachedSettingsSnapshot();
                ResetDefaultsPromptPlan promptPlan = AppResetCoordinator.BuildResetDefaultsPromptPlan(
                    _settings.SettingsFileExists(),
                    OutputCycleDevices.Count > 0 || InputCycleDevices.Count > 0,
                    Routines.Count > 0 || current?.Routines.Items.Count > 0,
                    Hotkey.HasMainInput || InputHotkey.HasMainInput || OutputReverseHotkey.HasMainInput || InputReverseHotkey.HasMainInput,
                    RunAtStartup || current?.RunAtStartup == true,
                    AppSettingsWorkflowCoordinator.BuildResetSummary,
                    hasUnsavedChanges: HasPendingLocalEditsForRefresh());

                if (promptPlan.ShouldSkip)
                {
                    _logger.Info("AppViewModel", AppResetCoordinator.BuildResetSkipLogMessage());
                    await _dialogs.ShowInformationAsync(promptPlan.DialogMessage ?? string.Empty, promptPlan.DialogCaption);
                    return;
                }

                AppDialogResult resetResult = await _dialogs.ShowAsync(AppDialogRequest.Confirm(
                    promptPlan.DialogMessage ?? string.Empty,
                    promptPlan.DialogCaption,
                    promptPlan.DialogKind,
                    "_Reset",
                    "_Cancel",
                    AppDialogActionStyle.Destructive));
                if (!AppResetCoordinator.ShouldProceed(resetResult))
                {
                    _logger.Info("AppViewModel", "reset-cancelled");
                    return;
                }

                _logger.Info("AppViewModel", "reset-start");
                AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _startupDebounceCts)?.Dispose();
                await ExecuteSettingsWriteAsync(async () =>
                {
                    await Task.Run(() => _startup.ApplyRegistration(
                        enabled: false,
                        useScheduledTask: false,
                        persistSettings: _settings.DeleteSettingsFiles));

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
                        _windowState.ShowBalloonOnFirstMinimize = true;
                        ShowBalloonAfterSave = false;
                        OnPropertyChanged(nameof(RunAtStartup));
                        UpdateLastSettingsWriteTime();
                    });
                });
                _logger.Info("AppViewModel", "reset-complete");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("AppViewModel", "reset-defaults-failed", nameof(ResetToDefaultsAsync), ex);
            await _dialogs.ShowErrorAsync("Could not complete the settings reset. Check the log for details and try again.");
        }
        finally
        {
            IsResettingSettings = false;
            if (IsPersistedAutoSaveEnabled() && HasPendingLocalEditsForRefresh())
            {
                QueueAutoSave(nameof(ResetToDefaultsAsync));
            }
        }
    }
}

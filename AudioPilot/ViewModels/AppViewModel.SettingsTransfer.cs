using System.IO;
using System.Text.Json;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Models;
using Microsoft.Win32;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        internal readonly record struct SettingsExportDialogOptions(string InitialDirectory, string FileName);
        internal readonly record struct SettingsImportDialogOptions(string InitialDirectory);
        internal static Func<SettingsExportDialogOptions, (bool Accepted, string FileName)>? ExportSettingsDialogForTests { get; set; }
        internal static Func<SettingsImportDialogOptions, (bool Accepted, string FileName)>? ImportSettingsDialogForTests { get; set; }

        internal static void ResetSettingsTransferDialogsForTests()
        {
            ExportSettingsDialogForTests = null;
            ImportSettingsDialogForTests = null;
        }

        private async Task ExportSettingsAsync()
        {
            if (IsExportingSettings || IsResettingSettings || _isApplyingSettings || _isSaving || IsSavingRoutines || _isCleaningUp || IsRuntimeShutdownRequested)
            {
                return;
            }

            IsExportingSettings = true;
            CancellationToken token = GetDialogShutdownToken();
            string selectedFileName = string.Empty;
            try
            {
                if (HasPendingLocalEditsForRefresh())
                {
                    var request = AppDialogRequest.Confirm(
                        Localization["Dialog_ExportUnsavedEditsMessage"],
                        DialogText.Captions.ExportSettings,
                        AppDialogKind.Warning,
                        Localization["Dialog_ExportAnyway"],
                        "_Cancel") with
                    {
                        MessagePresentation = language => language["Dialog_ExportUnsavedEditsMessage"],
                    };
                    if (await _dialogs.ShowAsync(request, token) != AppDialogResult.Confirmed || IsRuntimeShutdownRequested || _isCleaningUp)
                    {
                        return;
                    }
                }

                string initialDirectory = AppSettingsTransferCoordinator.ResolveInitialDirectory(GetSettingsPath());
                string fileName = AppSettingsTransferCoordinator.BuildDefaultExportFileName(DateTime.Now);

                (bool Accepted, string FileName) = ExportSettingsDialogForTests != null
                    ? ExportSettingsDialogForTests(new SettingsExportDialogOptions(initialDirectory, fileName))
                    : ShowExportSettingsDialog(initialDirectory, fileName);

                if (!Accepted || IsRuntimeShutdownRequested || _isCleaningUp)
                {
                    return;
                }

                selectedFileName = FileName;
                await AppSettingsTransferCoordinator.ExportAsync(_settings, CurrentSettings, selectedFileName);
                await _dialogs.ShowSuccessAsync(
                    DialogText.Messages.BuildSettingsExportedSuccessfully(selectedFileName),
                    DialogText.Captions.ExportSettings,
                    cancellationToken: token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "export-settings-failed", nameof(ExportSettingsAsync), ex);
                if (!IsRuntimeShutdownRequested && !_isCleaningUp)
                {
                    await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(
                        SettingsFailure.ForDialog(ex, new("Message_FailedToExportSettingsTo0", selectedFileName), includeContext: true),
                        DialogText.Captions.ExportSettings, AppDialogKind.Error), token);
                }
            }
            finally
            {
                IsExportingSettings = false;
            }
        }

        private async Task ImportSettingsAsync()
        {
            if (IsExportingSettings || IsResettingPerAppAudio || IsResettingSettings || _isApplyingSettings || _isSaving || IsSavingRoutines || _isCleaningUp || IsRuntimeShutdownRequested) return;

            using var activeSave = BeginActiveSettingsSave();
            IsApplyingSettings = true;
            string fileName = string.Empty;

            try
            {
                using var suppression = SuppressAutoSave();
                AppDebouncedBackgroundWorkCoordinator.CancelAndDetach(ref _autoSaveDebounceCts)?.Dispose();
                string initialDirectory = AppSettingsTransferCoordinator.ResolveInitialDirectory(GetSettingsPath());
                var (accepted, selectedFileName) = ImportSettingsDialogForTests != null
                    ? ImportSettingsDialogForTests(new SettingsImportDialogOptions(initialDirectory))
                    : ShowImportSettingsDialog(initialDirectory);
                if (!accepted) return;
                fileName = selectedFileName;
                CancellationToken token = ShutdownToken;
                Settings ReadBaseline() => _settings.SettingsFileExists()
                    ? _settings.LoadSettingsReadOnly()
                    : (GetCachedSettingsSnapshot() ?? _settings.LoadSettingsReadOnly()).Clone();
                var (previewBaseline, prepared) = await Task.Run(() => (ReadBaseline(),
                    AppSettingsTransferCoordinator.PrepareImport(
                        SettingsTransferService.ReadImportText(fileName, _settings.ReadTextFileWithSettingsLock), null, replaceImport: true)), token);
                bool settingsChanged = false;
                while (!_isCleaningUp && !IsRuntimeShutdownRequested)
                {
                    var describeImport = await Task.Run(() => AppSettingsTransferCoordinator.DescribeImport(previewBaseline, prepared), token);
                    token.ThrowIfCancellationRequested();
                    bool hasUnsavedEdits = HasPendingLocalEditsForRefresh();
                    bool refreshedPreview = settingsChanged;
                    var confirmation = new LocalizedText("Message_Importing0WillReplaceYourCurrentSaved", Path.GetFileName(fileName));
                    string PresentImport(LocalizationService language) => (refreshedPreview ? language["Import_SettingsChangedReviewAgain"] + "\n\n" : "")
                        + language.Format(confirmation) + (hasUnsavedEdits
                            ? "\n\n" + language["Message_YouAlsoHaveUnsavedLocalEditsContinuing"] : "")
                        + "\n\n" + describeImport(language);
                    var request = AppDialogRequest.Confirm(
                        PresentImport(LocalizationService.English),
                        DialogText.Captions.ImportSettings,
                        AppDialogKind.Warning,
                        "_Import",
                        "_Cancel",
                        AppDialogActionStyle.Destructive) with
                    {
                        MessagePresentation = PresentImport,
                    };
                    if (await _dialogs.ShowAsync(request, token) != AppDialogResult.Confirmed || IsRuntimeShutdownRequested) return;

                    StartupRegistrationOutcome startupOutcome = StartupRegistrationOutcome.Applied;
                    Settings? changedBaseline = await AppSettingsTransferCoordinator.CommitPreparedImportAsync(
                        previewBaseline, prepared, ReadBaseline, _settingsWriteSemaphore,
                        settings => startupOutcome = SaveSettingsWithStartupRegistration(settings),
                        settings =>
                        {
                            ApplyExternallyReloadedSettings(settings);
                            UpdateLastSettingsWriteTime();
                        }, token);
                    if (changedBaseline == null)
                    {
                        if (startupOutcome == StartupRegistrationOutcome.Applied)
                        {
                            await _dialogs.ShowSuccessAsync(
                                DialogText.Messages.BuildSettingsImportedSuccessfully(fileName), DialogText.Captions.ImportSettings,
                                cancellationToken: token);
                        }
                        else
                        {
                            await _dialogs.ShowWarningAsync(
                                $"{DialogText.Messages.BuildSettingsImportedSuccessfully(fileName)}\n\n{IncompleteCliStartupDisableMessage}",
                                DialogText.Captions.ImportSettings,
                                cancellationToken: token);
                        }

                        return;
                    }
                    previewBaseline = changedBaseline;
                    settingsChanged = true;
                }
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested) { }
            catch (JsonException ex)
            {
                _logger.Error("AppViewModel", "import-settings-json-invalid", nameof(ImportSettingsAsync), ex);
                await ShowImportFailureAsync(ex, fileName);
            }
            catch (InvalidDataException ex)
            {
                _logger.Error("AppViewModel", "import-settings-archive-invalid", nameof(ImportSettingsAsync), ex);
                await ShowImportFailureAsync(ex, fileName);
            }
            catch (NotSupportedException ex)
            {
                _logger.Error("AppViewModel", "import-settings-format-unsupported", nameof(ImportSettingsAsync), ex);
                await ShowImportFailureAsync(ex, fileName);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "import-settings-failed", nameof(ImportSettingsAsync), ex);
                await ShowImportFailureAsync(ex, fileName);
            }
            finally
            {
                IsApplyingSettings = false;
                if (!_isCleaningUp && !IsRuntimeShutdownRequested && IsPersistedAutoSaveEnabled() && HasPendingLocalEditsForRefresh())
                {
                    QueueAutoSave(nameof(ImportSettingsAsync));
                }
            }
        }

        private Task<AppDialogResult> ShowImportFailureAsync(Exception error, string fileName) =>
            _isCleaningUp || IsRuntimeShutdownRequested
                ? Task.FromResult(AppDialogResult.Cancelled)
                : _dialogs.ShowAsync(
                    AppDialogRequest.Acknowledge(SettingsFailure.ForDialog(error,
                        error is JsonException ? new("Validation_ImportJsonInvalid") : new("Message_FailedToImportSettingsFrom0", fileName)),
                        DialogText.Captions.ImportSettings, AppDialogKind.Error), ShutdownToken);

        private (bool Accepted, string FileName) ShowExportSettingsDialog(string initialDirectory, string fileName)
        {
            var dialog = new SaveFileDialog
            {
                Title = Localization["Dialog_ExportSettings"],
                Filter = Localization["Dialog_SettingsTransferFilter"],
                FilterIndex = 1,
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
                CheckPathExists = true,
                InitialDirectory = initialDirectory,
                FileName = fileName,
            };

            var owner = Helpers.DialogWindowHelper.ResolveVisibleOwner();
            return ((owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true, dialog.FileName);
        }

        private (bool Accepted, string FileName) ShowImportSettingsDialog(string initialDirectory)
        {
            var dialog = new OpenFileDialog
            {
                Title = Localization["Dialog_ImportSettings"],
                Filter = Localization["Dialog_SettingsTransferFilter"],
                FilterIndex = 1,
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                InitialDirectory = initialDirectory,
            };

            var owner = Helpers.DialogWindowHelper.ResolveVisibleOwner();
            return ((owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true, dialog.FileName);
        }
    }
}

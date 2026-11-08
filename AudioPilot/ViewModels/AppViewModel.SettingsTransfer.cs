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
            string initialDirectory = AppSettingsTransferCoordinator.ResolveInitialDirectory(GetSettingsPath());
            string fileName = AppSettingsTransferCoordinator.BuildDefaultExportFileName(DateTime.Now);

            (bool Accepted, string FileName) = ExportSettingsDialogForTests != null
                ? ExportSettingsDialogForTests(new SettingsExportDialogOptions(initialDirectory, fileName))
                : ShowExportSettingsDialog(initialDirectory, fileName);

            if (!Accepted)
            {
                return;
            }

            try
            {
                await AppSettingsTransferCoordinator.ExportAsync(_settings, CurrentSettings, FileName);
                await _dialogs.ShowSuccessAsync(
                    DialogText.Messages.BuildSettingsExportedSuccessfully(FileName),
                    DialogText.Captions.ExportSettings);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "export-settings-failed", nameof(ExportSettingsAsync), ex);
                await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(
                    SettingsFailure.ForDialog(ex, new("Message_FailedToExportSettingsTo0", FileName), includeContext: true),
                    DialogText.Captions.ExportSettings, AppDialogKind.Error));
            }
        }

        private async Task ImportSettingsAsync()
        {
            if (IsResettingSettings || _isApplyingSettings || _isSaving || IsSavingRoutines || _isCleaningUp) return;

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
                Settings previewBaseline = (CurrentSettings ?? _settings.LoadSettingsReadOnly()).Clone();
                Settings prepared = await AppSettingsTransferCoordinator.PreviewImportAsync(_settings, previewBaseline, fileName, ShutdownToken);
                var describeImport = await Task.Run(() => AppSettingsTransferCoordinator.DescribeImport(previewBaseline, prepared), ShutdownToken);
                if (_isCleaningUp) return;

                bool hasUnsavedEdits = HasPendingLocalEditsForRefresh();
                var confirmation = new LocalizedText("Message_Importing0WillReplaceYourCurrentSaved", Path.GetFileName(fileName));
                string PresentImport(LocalizationService language) => language.Format(confirmation) + (hasUnsavedEdits
                    ? "\n\n" + language["Message_YouAlsoHaveUnsavedLocalEditsContinuing"] : "") + Environment.NewLine + Environment.NewLine + describeImport(language);
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
                AppDialogResult importResult = await _dialogs.ShowAsync(request);
                if (importResult != AppDialogResult.Confirmed) return;

                Settings imported = await AppSettingsTransferCoordinator.ImportAsync(_settings, CurrentSettings, _settingsWriteSemaphore, fileName,
                    settings => SaveSettingsWithStartupRegistration(settings), preparedSettings: prepared, cancellationToken: ShutdownToken);

                ApplyExternallyReloadedSettings(imported);
                UpdateLastSettingsWriteTime();
                await _dialogs.ShowSuccessAsync(
                    DialogText.Messages.BuildSettingsImportedSuccessfully(fileName),
                    DialogText.Captions.ImportSettings);
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
                if (IsPersistedAutoSaveEnabled() && HasPendingLocalEditsForRefresh())
                {
                    QueueAutoSave(nameof(ImportSettingsAsync));
                }
            }
        }

        private Task<AppDialogResult> ShowImportFailureAsync(Exception error, string fileName) => _dialogs.ShowAsync(
            AppDialogRequest.Acknowledge(SettingsFailure.ForDialog(error,
                error is JsonException ? new("Validation_ImportJsonInvalid") : new("Message_FailedToImportSettingsFrom0", fileName)),
                DialogText.Captions.ImportSettings, AppDialogKind.Error));

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

            return (dialog.ShowDialog() == true, dialog.FileName);
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

            return (dialog.ShowDialog() == true, dialog.FileName);
        }
    }
}

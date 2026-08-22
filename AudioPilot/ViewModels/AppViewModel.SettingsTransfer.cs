using System.IO;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private const string SettingsTransferFilter = "ZIP archives (*.zip)|*.zip|JSON files (*.json)|*.json";
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
                await _dialogs.ShowErrorAsync(
                    DialogText.Messages.BuildSettingsExportFailed(FileName),
                    DialogText.Captions.ExportSettings);
            }
        }

        private async Task ImportSettingsAsync()
        {
            string initialDirectory = AppSettingsTransferCoordinator.ResolveInitialDirectory(GetSettingsPath());
            (bool Accepted, string FileName) = ImportSettingsDialogForTests != null
                ? ImportSettingsDialogForTests(new SettingsImportDialogOptions(initialDirectory))
                : ShowImportSettingsDialog(initialDirectory);

            if (!Accepted)
            {
                return;
            }

            bool hasUnsavedEdits = HasPendingLocalEditsForRefresh();
            AppDialogResult importResult = await _dialogs.ShowAsync(AppDialogRequest.Confirm(
                DialogText.Messages.BuildImportSettingsReplaceConfirmation(FileName, hasUnsavedEdits),
                DialogText.Captions.ImportSettings,
                AppDialogKind.Warning,
                "_Import",
                "_Cancel",
                AppDialogActionStyle.Destructive));
            if (importResult != AppDialogResult.Confirmed)
            {
                return;
            }

            IsApplyingSettings = true;

            try
            {
                Settings imported = await AppSettingsTransferCoordinator.ImportAsync(_settings, CurrentSettings, _settingsWriteSemaphore, FileName);

                ApplyExternallyReloadedSettings(imported);
                UpdateLastSettingsWriteTime();
                await _dialogs.ShowSuccessAsync(
                    DialogText.Messages.BuildSettingsImportedSuccessfully(FileName),
                    DialogText.Captions.ImportSettings);
            }
            catch (JsonException ex)
            {
                _logger.Error("AppViewModel", "import-settings-json-invalid", nameof(ImportSettingsAsync), ex);
                await _dialogs.ShowErrorAsync(ex.Message, DialogText.Captions.ImportSettings);
            }
            catch (InvalidDataException ex)
            {
                _logger.Error("AppViewModel", "import-settings-archive-invalid", nameof(ImportSettingsAsync), ex);
                await _dialogs.ShowErrorAsync(ex.Message, DialogText.Captions.ImportSettings);
            }
            catch (NotSupportedException ex)
            {
                _logger.Error("AppViewModel", "import-settings-format-unsupported", nameof(ImportSettingsAsync), ex);
                await _dialogs.ShowErrorAsync(ex.Message, DialogText.Captions.ImportSettings);
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", "import-settings-failed", nameof(ImportSettingsAsync), ex);
                await _dialogs.ShowErrorAsync(
                    DialogText.Messages.BuildSettingsImportFailed(FileName),
                    DialogText.Captions.ImportSettings);
            }
            finally
            {
                IsApplyingSettings = false;
            }
        }

        private static (bool Accepted, string FileName) ShowExportSettingsDialog(string initialDirectory, string fileName)
        {
            var dialog = new SaveFileDialog
            {
                Title = DialogText.Captions.ExportSettings,
                Filter = SettingsTransferFilter,
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

        private static (bool Accepted, string FileName) ShowImportSettingsDialog(string initialDirectory)
        {
            var dialog = new OpenFileDialog
            {
                Title = DialogText.Captions.ImportSettings,
                Filter = SettingsTransferFilter,
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

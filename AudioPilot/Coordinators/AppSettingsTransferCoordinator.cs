using System.IO;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal static class AppSettingsTransferCoordinator
    {
        internal static async Task ExportAsync(SettingsService settingsService, Settings? currentSettings, string filePath)
        {
            ArgumentNullException.ThrowIfNull(settingsService);
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            Settings settings = currentSettings ?? settingsService.LoadSettings();
            await Task.Run(() => SettingsTransferService.ExportSettings(settings, filePath));
        }

        internal static async Task<Settings> ImportAsync(
            SettingsService settingsService,
            Settings? currentSettings,
            SemaphoreSlim settingsWriteSemaphore,
            string filePath)
        {
            ArgumentNullException.ThrowIfNull(settingsService);
            ArgumentNullException.ThrowIfNull(settingsWriteSemaphore);
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            await settingsWriteSemaphore.WaitAsync();
            try
            {
                Settings current = currentSettings ?? settingsService.LoadSettings();
                Settings imported = await Task.Run(() =>
                {
                    string importJson = SettingsTransferService.ReadImportText(filePath, settingsService.ReadTextFileWithSettingsLock);
                    return SettingsTransferService.ParseImportedSettings(importJson, current, replaceImport: true);
                });

                await Task.Run(() => settingsService.SaveSettings(imported));
                return imported;
            }
            finally
            {
                settingsWriteSemaphore.Release();
            }
        }

        internal static string BuildDefaultExportFileName(DateTime timestamp)
        {
            return $"AudioPilot-settings-{timestamp:yyyyMMdd-HHmmss}.zip";
        }

        internal static string ResolveInitialDirectory(string settingsPath)
        {
            string settingsDirectory = Path.GetDirectoryName(settingsPath) ?? string.Empty;
            if (Directory.Exists(settingsDirectory))
            {
                return settingsDirectory;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
}

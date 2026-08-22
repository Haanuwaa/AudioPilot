using System.IO;
using AudioPilot.Constants;
using Microsoft.Win32;

namespace AudioPilot.Platform
{
    public static class AppDataPaths
    {
        private const string InstallerRegistryKey = @"Software\Haanuwaa\AudioPilot";
        private const string InstallFolderRegistryValue = "InstallFolder";
        private const string DataFolderRegistryValue = "DataFolder";

        internal static Func<string>? UserDataRootProviderOverride { get; set; }
        internal static Func<string>? BaseDirectoryProviderOverride { get; set; }
        internal static Func<(string? InstallFolder, string? DataFolder)>? InstallerRegistrationProviderOverride { get; set; }

        public static string GetUserDataRoot()
        {
            string? overrideRoot = UserDataRootProviderOverride?.Invoke();
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return overrideRoot;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppConstants.Identity.AppName);
        }

        public static string GetPrimaryDataRoot()
        {
            if (TryGetInstalledDataRoot(out string? installedDataRoot))
            {
                return installedDataRoot;
            }

            return GetBaseDirectory();
        }

        public static string GetFallbackDataRoot()
        {
            return GetUserDataRoot();
        }

        public static string GetWritableDataRoot()
        {
            string primaryRoot = GetPrimaryDataRoot();
            if (CanWriteToDirectory(primaryRoot))
            {
                return primaryRoot;
            }

            return GetFallbackDataRoot();
        }

        private static bool TryGetInstalledDataRoot(out string dataRoot)
        {
            dataRoot = string.Empty;

            try
            {
                (string? installFolder, string? registeredDataRoot) = GetInstallerRegistration();
                if (string.IsNullOrWhiteSpace(installFolder))
                {
                    return false;
                }

                string currentBaseDirectory = NormalizeDirectoryPath(GetBaseDirectory());
                string registeredInstallFolder = NormalizeDirectoryPath(installFolder);
                if (!string.Equals(currentBaseDirectory, registeredInstallFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                dataRoot = string.IsNullOrWhiteSpace(registeredDataRoot)
                    ? GetUserDataRoot()
                    : registeredDataRoot;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string GetBaseDirectory()
        {
            return BaseDirectoryProviderOverride?.Invoke() ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        private static (string? InstallFolder, string? DataFolder) GetInstallerRegistration()
        {
            if (InstallerRegistrationProviderOverride != null)
            {
                return InstallerRegistrationProviderOverride();
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InstallerRegistryKey);
            return (
                key?.GetValue(InstallFolderRegistryValue) as string,
                key?.GetValue(DataFolderRegistryValue) as string);
        }

        private static bool CanWriteToDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probePath = Path.Combine(directory, $".audiopilot-write-test-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probePath, string.Empty);
                File.Delete(probePath);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

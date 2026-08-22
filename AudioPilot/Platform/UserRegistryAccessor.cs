using Microsoft.Win32;

namespace AudioPilot.Platform
{
    internal interface IUserRegistryAccessor
    {
        object? GetValue(string subKeyPath, string valueName);

        void SetValue(string subKeyPath, string valueName, object value);

        void DeleteValue(string subKeyPath, string valueName);

        bool HasValuesOrSubKeys(string subKeyPath);

        void DeleteSubKeyTree(string subKeyPath);
    }

    internal sealed class CurrentUserRegistryAccessor : IUserRegistryAccessor
    {
        public static CurrentUserRegistryAccessor Instance { get; } = new();

        private CurrentUserRegistryAccessor()
        {
        }

        public object? GetValue(string subKeyPath, string valueName)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(valueName);
        }

        public void SetValue(string subKeyPath, string valueName, object value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true)
                ?? throw new InvalidOperationException($"Unable to open or create HKCU\\{subKeyPath}.");
            key.SetValue(valueName, value);
        }

        public void DeleteValue(string subKeyPath, string valueName)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }

        public bool HasValuesOrSubKeys(string subKeyPath)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            return key is { ValueCount: > 0 } || key is { SubKeyCount: > 0 };
        }

        public void DeleteSubKeyTree(string subKeyPath)
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
        }
    }
}

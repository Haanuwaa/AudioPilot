using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AudioPilot.Platform
{
    internal static class WindowsTimeFormatPreference
    {
        private const uint LocaleShortTimePattern = 0x00000079;
        private const uint LocaleLongTimePattern = 0x00001003;
        private const int MaximumPatternLength = 80;
        private const string ExplorerAdvancedRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string ShowSecondsRegistryValueName = "ShowSecondsInSystemClock";

        internal static string GetCurrentTaskbarTimePattern(
            string fallbackShortTimePattern,
            string fallbackLongTimePattern)
        {
            bool showSeconds = TryGetTaskbarShowsSeconds() == true;
            uint localePattern = showSeconds ? LocaleLongTimePattern : LocaleShortTimePattern;
            string fallbackPattern = SelectEffectiveTimePattern(
                fallbackShortTimePattern,
                fallbackLongTimePattern,
                showSeconds);

            return GetRegionalTimePattern(localePattern, fallbackPattern);
        }

        internal static string SelectEffectiveTimePattern(
            string shortTimePattern,
            string longTimePattern,
            bool showSeconds)
        {
            return showSeconds ? longTimePattern : shortTimePattern;
        }

        private static bool? TryGetTaskbarShowsSeconds()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ExplorerAdvancedRegistryPath);
                object? value = key?.GetValue(ShowSecondsRegistryValueName);
                return value switch
                {
                    int intValue => intValue != 0,
                    long longValue => longValue != 0,
                    _ => null,
                };
            }
            catch (System.IO.IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
        }

        private static string GetRegionalTimePattern(uint localePattern, string fallbackPattern)
        {
            try
            {
                var buffer = new StringBuilder(MaximumPatternLength);
                int length = GetLocaleInfoEx(
                    localeName: null,
                    localeType: localePattern,
                    localeData: buffer,
                    localeDataLength: buffer.Capacity);

                return length > 1 ? buffer.ToString() : fallbackPattern;
            }
            catch (DllNotFoundException)
            {
                return fallbackPattern;
            }
            catch (EntryPointNotFoundException)
            {
                return fallbackPattern;
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "GetLocaleInfoEx", CharSet = CharSet.Unicode)]
        private static extern int GetLocaleInfoEx(
            string? localeName,
            uint localeType,
            StringBuilder localeData,
            int localeDataLength);
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AudioPilot.Platform
{
    internal static partial class WindowsTimeFormatPreference
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
            catch (IOException)
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

        private static unsafe string GetRegionalTimePattern(uint localePattern, string fallbackPattern)
        {
            try
            {
                Span<char> buffer = stackalloc char[MaximumPatternLength];
                int length;
                fixed (char* pointer = buffer)
                {
                    length = GetLocaleInfoEx(
                        localeName: null,
                        localeType: localePattern,
                        localeData: pointer,
                        localeDataLength: buffer.Length);
                }

                return length > 1 ? new string(buffer[..(length - 1)]) : fallbackPattern;
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

        [LibraryImport("kernel32.dll", EntryPoint = "GetLocaleInfoEx", StringMarshalling = StringMarshalling.Utf16)]
        private static unsafe partial int GetLocaleInfoEx(
            string? localeName,
            uint localeType,
            char* localeData,
            int localeDataLength);
    }
}

using System.ComponentModel;
using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI;

internal enum WindowsAudioSettingsPage { Sound, VolumeMixer, MicrophonePrivacy, Bluetooth }

/// <summary>Opens only known Windows settings pages, with an escaped endpoint selector and a general fallback.</summary>
internal static class WindowsAudioSettings
{
    internal static string GetUri(WindowsAudioSettingsPage page) => page switch
    {
        WindowsAudioSettingsPage.Sound => "ms-settings:sound",
        WindowsAudioSettingsPage.VolumeMixer => "ms-settings:apps-volume",
        WindowsAudioSettingsPage.MicrophonePrivacy => "ms-settings:privacy-microphone",
        WindowsAudioSettingsPage.Bluetooth => "ms-settings:bluetooth",
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    internal static string GetDeviceUri(string endpointId, bool supportsDevicePage) =>
        supportsDevicePage && !string.IsNullOrWhiteSpace(endpointId) && endpointId.Length <= 4096
            ? "ms-settings:sound-properties?endpointId=" + Uri.EscapeDataString(endpointId)
            : GetUri(WindowsAudioSettingsPage.Sound);

    internal static bool Open(WindowsAudioSettingsPage page) => OpenWithFallback(GetUri(page),
        fallbackUri: page == WindowsAudioSettingsPage.Bluetooth ? "ms-settings:devices" : null);

    internal static bool OpenDevice(string endpointId) =>
        OpenWithFallback(GetDeviceUri(endpointId, OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)));

    internal static bool OpenWithFallback(string uri, Func<string, bool>? launch = null, string? fallbackUri = null, ILogger? logger = null)
    {
        logger ??= Logger.Instance;
        launch ??= static target =>
        {
            using Process? process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        };
        Exception? lastFailure = null;
        foreach (string target in new[] { uri, fallbackUri ?? GetUri(WindowsAudioSettingsPage.Sound) }.Distinct(StringComparer.Ordinal))
        {
            try { if (launch(target)) return true; }
            catch (Exception ex)
            {
                lastFailure = ex;
                logger.Debug("WindowsAudioSettings", () => $"windows-audio-settings-open-attempt-failed | error={ex.GetType().Name} hresult=0x{ex.HResult:X8}");
            }
        }
        string page = uri.Split('?', 2)[0] switch
        {
            "ms-settings:sound" => "Sound",
            "ms-settings:apps-volume" => "VolumeMixer",
            "ms-settings:privacy-microphone" => "MicrophonePrivacy",
            "ms-settings:bluetooth" => "Bluetooth",
            "ms-settings:sound-properties" => "DeviceProperties",
            _ => "Unknown",
        };
        logger.Warning("WindowsAudioSettings", () => $"windows-audio-settings-open-failed | page={page} error={lastFailure?.GetType().Name ?? "launch-rejected"} hresult={(lastFailure == null ? "none" : $"0x{lastFailure.HResult:X8}")} nativeError={(lastFailure is Win32Exception native ? native.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}");
        return false;
    }
}

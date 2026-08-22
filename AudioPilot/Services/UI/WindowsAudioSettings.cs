using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI;

internal enum WindowsAudioSettingsPage { Sound, VolumeMixer, MicrophonePrivacy }

/// <summary>Opens only known Windows settings pages, with an escaped endpoint selector and a general fallback.</summary>
internal static class WindowsAudioSettings
{
    internal static string GetUri(WindowsAudioSettingsPage page) => page switch
    {
        WindowsAudioSettingsPage.Sound => "ms-settings:sound",
        WindowsAudioSettingsPage.VolumeMixer => "ms-settings:apps-volume",
        WindowsAudioSettingsPage.MicrophonePrivacy => "ms-settings:privacy-microphone",
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    internal static string GetDeviceUri(string endpointId, bool supportsDevicePage) =>
        supportsDevicePage && !string.IsNullOrWhiteSpace(endpointId) && endpointId.Length <= 4096
            ? "ms-settings:sound-properties?endpointId=" + Uri.EscapeDataString(endpointId)
            : GetUri(WindowsAudioSettingsPage.Sound);

    internal static bool Open(WindowsAudioSettingsPage page) => OpenWithFallback(GetUri(page));

    internal static bool OpenDevice(string endpointId) =>
        OpenWithFallback(GetDeviceUri(endpointId, OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)));

    internal static bool OpenWithFallback(string uri, Func<string, bool>? launch = null)
    {
        launch ??= static target =>
        {
            using Process? process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        };
        foreach (string target in new[] { uri, GetUri(WindowsAudioSettingsPage.Sound) }.Distinct(StringComparer.Ordinal))
        {
            try { if (launch(target)) return true; }
            catch (Exception ex)
            {
                Logger.Instance.Debug("WindowsAudioSettings", () => $"windows-audio-settings-open-failed | error={ex.GetType().Name}");
            }
        }
        return false;
    }
}

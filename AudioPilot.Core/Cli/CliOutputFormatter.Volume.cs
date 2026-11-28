using System.Globalization;
using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
        internal static string FormatVolumeAdjustment(string kind, float previousPercent, float percent, bool muted, bool jsonOutput, string? deviceId, bool redactOutput)
        {
            return jsonOutput
                ? SerializeCliJson(new
                {
                    Success = true,
                    Kind = kind,
                    DeviceId = FormatOptionalDeviceId(deviceId, redactOutput),
                    PreviousPercent = Math.Round(previousPercent, 3),
                    Percent = Math.Round(percent, 3),
                    Muted = muted,
                    DiagCode = "volume-adjust-success",
                })
                : FormatVolumeResult(kind, percent, muted, false, "volume-adjust-success", deviceId, redactOutput);
        }

        public static string FormatMuteStatus(string target, bool enabled, bool jsonOutput)
        {
            string diagCode = GetMuteStatusDiagCode(target);

            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Success = true,
                    Target = target,
                    Enabled = enabled,
                    DiagCode = diagCode,
                });
            }

            return $"[diag-code:{diagCode}] {GetMuteStatusLabel(target)} {(enabled ? "enabled" : "disabled")}.";
        }

        public static string FormatVolumeResult(string kind, float percent, bool muted, bool jsonOutput, string diagCode, string? deviceId = null, bool redactOutput = false)
        {
            deviceId = FormatOptionalDeviceId(deviceId, redactOutput);
            int displayedPercent = (int)Math.Round(Math.Clamp(percent, 0f, 100f), MidpointRounding.AwayFromZero);

            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Success = true,
                    Kind = kind,
                    DeviceId = deviceId,
                    Percent = displayedPercent,
                    Muted = muted,
                    DiagCode = diagCode,
                });
            }

            string textPercent = VolumePercent.Format(percent, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(deviceId)
                ? $"[diag-code:{diagCode}] {GetVolumeLabel(kind)} {textPercent}% ({(muted ? "muted" : "unmuted")})."
                : $"[diag-code:{diagCode}] {GetVolumeLabel(kind)} {textPercent}% ({(muted ? "muted" : "unmuted")}) for device '{deviceId}'.";
        }

        public static string FormatVolumeError(string kind, string diagCode, string message, bool jsonOutput, string? deviceId = null, bool redactOutput = false)
        {
            deviceId = FormatOptionalDeviceId(deviceId, redactOutput);
            if (redactOutput)
            {
                message = RedactWarnings([message], redactOutput)[0];
            }

            return FormatCommandError(new { Kind = kind, DeviceId = deviceId }, 3, diagCode, message, jsonOutput);
        }
    }
}

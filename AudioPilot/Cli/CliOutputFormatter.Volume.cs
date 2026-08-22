namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
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

        public static string FormatVolumeResult(string kind, float percent, bool muted, bool jsonOutput, string diagCode, string? deviceId = null)
        {
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

            return string.IsNullOrWhiteSpace(deviceId)
                ? $"[diag-code:{diagCode}] {GetVolumeLabel(kind)} {displayedPercent}% ({(muted ? "muted" : "unmuted")})."
                : $"[diag-code:{diagCode}] {GetVolumeLabel(kind)} {displayedPercent}% ({(muted ? "muted" : "unmuted")}) for device '{deviceId}'.";
        }

        public static string FormatVolumeError(string kind, string diagCode, string message, bool jsonOutput, string? deviceId = null)
        {
            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Success = false,
                    Kind = kind,
                    DeviceId = deviceId,
                    DiagCode = diagCode,
                    Error = message,
                });
            }

            return $"[diag-code:{diagCode}] {message}";
        }
    }
}

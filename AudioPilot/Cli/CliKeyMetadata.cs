namespace AudioPilot.Cli
{
    public sealed record CliKeyMetadata(
        string Key,
        string ValueType,
        string? Range,
        string Scope,
        string Description,
        string? DefaultValue = null,
        string? CurrentValue = null);

    internal static class CliKeyMetadataFactory
    {
        public static CliKeyMetadata Create(
            string key,
            string valueType,
            string? range,
            string scope,
            string? defaultValue = null,
            string? currentValue = null)
        {
            return new CliKeyMetadata(
                key,
                valueType,
                range,
                scope,
                Describe(key),
                defaultValue,
                currentValue);
        }

        private static string Describe(string key)
        {
            return key switch
            {
                "steam-big-picture-monitor-debounce-ms" => "Coalesces bursts of Steam window events before verifying Big Picture state.",
                "steam-big-picture-confirmation-delay-ms" => "Waits before the confirmation pass when Steam Big Picture may be closing.",
                "bluetooth-reconnect-cached-endpoint-probe-attempts" => "Controls how many visibility probes run for a remembered Bluetooth endpoint.",
                "bluetooth-reconnect-cached-endpoint-probe-delay-ms" => "Controls the delay between remembered Bluetooth endpoint visibility probes.",
                _ => $"Controls {key.Replace('-', ' ')}.",
            };
        }
    }
}

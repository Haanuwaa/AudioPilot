using System.Text;

namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool? currentInputListenEnabled,
            string? listenMonitorTargetOutputDeviceName,
            IReadOnlyList<string>? warnings,
            bool jsonOutput,
            bool redactOutput = false)
        {
            return FormatStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                currentInputListenEnabled,
                listenMonitorTargetOutputDeviceName,
                warnings,
                jsonOutput,
                bluetoothReconnectEnabled: false,
                bluetoothReconnectMaxAttempts: 0,
                bluetoothReconnectAttemptTimeoutMs: 0,
                bluetoothReconnectCooldownMs: 0,
                bluetoothReconnectOnlyLikely: false,
                outputSwitchDebounceMs: 0,
                inputSwitchDebounceMs: 0,
                switchRetryDelayMs: 0,
                switchRetryMaxDelayMs: 0,
                switchMaxRetries: 0,
                hotplugRefreshDebounceMs: 0,
                hotplugConnectedOverlaySuppressAfterSwitchMs: 0,
                mixerSessionRefreshDebounceMs: 0,
                mixerSnapshotCacheInteractiveMs: 0,
                mixerSnapshotCacheBackgroundMs: 0,
                mixerDiagnosticsSummaryWindowSeconds: 0,
                mixerCacheWindowDiagnosticsLogEveryNRefreshes: 0,
                resumeHotkeyRetryDelayMs: 0,
                bluetoothReconnectSuccessObservedRecheckIntervalMs: 0,
                bluetoothReconnectTimeoutCircuitThreshold: 0,
                bluetoothReconnectTimeoutCircuitOpenMs: 0,
                includeRuntimeTuning: false,
                includeBluetoothReconnect: false,
                redactOutput: redactOutput);
        }

        public static string FormatStatusSnapshot(
            bool startupEnabled,
            int availableOutputDevices,
            int availableInputDevices,
            int configuredOutputCycleDevices,
            int configuredInputCycleDevices,
            bool? currentInputListenEnabled,
            string? listenMonitorTargetOutputDeviceName,
            IReadOnlyList<string>? warnings,
            bool jsonOutput,
            bool bluetoothReconnectEnabled,
            int bluetoothReconnectMaxAttempts,
            int bluetoothReconnectAttemptTimeoutMs,
            int bluetoothReconnectCooldownMs,
            bool bluetoothReconnectOnlyLikely,
            int outputSwitchDebounceMs,
            int inputSwitchDebounceMs,
            int switchRetryDelayMs,
            int switchRetryMaxDelayMs,
            int switchMaxRetries,
            int hotplugRefreshDebounceMs,
            int hotplugConnectedOverlaySuppressAfterSwitchMs,
            int mixerSessionRefreshDebounceMs,
            int mixerSnapshotCacheInteractiveMs,
            int mixerSnapshotCacheBackgroundMs,
            int resumeHotkeyRetryDelayMs,
            int mixerDiagnosticsSummaryWindowSeconds,
            int mixerCacheWindowDiagnosticsLogEveryNRefreshes,
            int bluetoothReconnectSuccessObservedRecheckIntervalMs,
            int bluetoothReconnectTimeoutCircuitThreshold,
            int bluetoothReconnectTimeoutCircuitOpenMs,
            bool includeRuntimeTuning = true,
            bool includeBluetoothReconnect = true,
            bool redactOutput = false)
        {
            CliBluetoothReconnectSnapshot? bluetoothReconnect = includeBluetoothReconnect
                ? new CliBluetoothReconnectSnapshot(
                    bluetoothReconnectEnabled,
                    bluetoothReconnectMaxAttempts,
                    bluetoothReconnectAttemptTimeoutMs,
                    bluetoothReconnectCooldownMs,
                    bluetoothReconnectOnlyLikely)
                : null;

            CliRuntimeTuningSnapshot? runtimeTuning = includeRuntimeTuning
                ? new CliRuntimeTuningSnapshot(
                    outputSwitchDebounceMs,
                    inputSwitchDebounceMs,
                    switchRetryDelayMs,
                    switchRetryMaxDelayMs,
                    switchMaxRetries,
                    hotplugRefreshDebounceMs,
                    hotplugConnectedOverlaySuppressAfterSwitchMs,
                    mixerSessionRefreshDebounceMs,
                    mixerSnapshotCacheInteractiveMs,
                    mixerSnapshotCacheBackgroundMs,
                    resumeHotkeyRetryDelayMs,
                    mixerDiagnosticsSummaryWindowSeconds,
                    mixerCacheWindowDiagnosticsLogEveryNRefreshes,
                    bluetoothReconnectSuccessObservedRecheckIntervalMs,
                    bluetoothReconnectTimeoutCircuitThreshold,
                    bluetoothReconnectTimeoutCircuitOpenMs)
                : null;

            var status = new CliStatusSnapshot(
                startupEnabled,
                availableOutputDevices,
                availableInputDevices,
                configuredOutputCycleDevices,
                configuredInputCycleDevices,
                currentInputListenEnabled,
                FormatOptionalDeviceName(listenMonitorTargetOutputDeviceName, redactOutput),
                RedactWarnings(warnings ?? [], redactOutput),
                bluetoothReconnect,
                runtimeTuning);

            if (jsonOutput)
            {
                return SerializeCliJson(status);
            }

            string baseOutput = $"startup: {(status.StartupEnabled ? "enabled" : "disabled")}{Environment.NewLine}" +
                       $"available output devices: {status.AvailableOutputDevices}{Environment.NewLine}" +
                       $"available input devices: {status.AvailableInputDevices}{Environment.NewLine}" +
                       $"configured output cycle devices: {status.ConfiguredOutputCycleDevices}{Environment.NewLine}" +
                       $"configured input cycle devices: {status.ConfiguredInputCycleDevices}{Environment.NewLine}" +
                       $"listen to input: {FormatListenState(status.CurrentInputListenEnabled)}{Environment.NewLine}" +
                       $"listen monitor target output: {FormatMonitorTarget(status.ListenMonitorTargetOutputDeviceName)}";

            if (status.BluetoothReconnect == null && status.RuntimeTuning == null)
            {
                return baseOutput + BuildWarningSection(status.Warnings);
            }

            if (status.BluetoothReconnect == null)
            {
                return baseOutput + Environment.NewLine +
                    BuildRuntimeText(status.RuntimeTuning) +
                    BuildWarningSection(status.Warnings);
            }

            string bluetoothText =
                $"bluetooth reconnect enabled: {status.BluetoothReconnect.Enabled}{Environment.NewLine}" +
                $"bluetooth reconnect max attempts: {status.BluetoothReconnect.MaxAttempts}{Environment.NewLine}" +
                $"bluetooth reconnect attempt timeout ms: {status.BluetoothReconnect.AttemptTimeoutMs}{Environment.NewLine}" +
                $"bluetooth reconnect cooldown ms: {status.BluetoothReconnect.CooldownMs}{Environment.NewLine}" +
                $"bluetooth reconnect only likely: {status.BluetoothReconnect.OnlyLikelyBluetoothEndpoints}";

            string runtimeText = BuildRuntimeText(status.RuntimeTuning);
            string composed = baseOutput + Environment.NewLine + bluetoothText;

            if (!string.IsNullOrWhiteSpace(runtimeText))
            {
                composed += Environment.NewLine + runtimeText;
            }

            return composed + BuildWarningSection(status.Warnings);
        }

        public static string FormatConfigValidation(IReadOnlyList<string> warnings, bool jsonOutput, bool redactOutput = false)
        {
            bool isValid = warnings.Count == 0;
            var snapshot = new CliConfigValidationSnapshot(isValid, RedactWarnings(warnings, redactOutput));

            if (jsonOutput)
            {
                return SerializeCliJson(snapshot);
            }

            if (snapshot.IsValid)
            {
                return "configuration is valid.";
            }

            var lines = new List<string> { "configuration has warnings:" };
            for (int index = 0; index < snapshot.Warnings.Count; index++)
            {
                lines.Add($"- {snapshot.Warnings[index]}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        public static string FormatSupportedKeyList(string kind, IReadOnlyList<string> keys, bool jsonOutput)
        {
            IReadOnlyList<CliKeyMetadata> metadata = ResolveKeyMetadata(kind, keys);

            if (jsonOutput)
            {
                return SerializeCliJson(new
                {
                    Kind = kind,
                    Keys = keys,
                    Details = metadata,
                });
            }

            if (keys.Count == 0)
            {
                return $"No supported {kind} keys.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Supported {kind} keys:");
            for (int index = 0; index < keys.Count; index++)
            {
                CliKeyMetadata detail = metadata[index];
                builder.Append(index + 1);
                builder.Append(". ");
                builder.AppendLine(detail.Key);
                builder.Append("   ");
                builder.Append(detail.ValueType);
                if (!string.IsNullOrWhiteSpace(detail.Range))
                {
                    builder.Append(" | range ");
                    builder.Append(detail.Range);
                }

                builder.Append(" | ");
                builder.Append(detail.Scope);
                if (!string.IsNullOrWhiteSpace(detail.DefaultValue))
                {
                    builder.Append(" | default ");
                    builder.Append(detail.DefaultValue);
                }

                if (!string.IsNullOrWhiteSpace(detail.CurrentValue))
                {
                    builder.Append(" | current ");
                    builder.Append(detail.CurrentValue);
                }

                builder.AppendLine();
                builder.Append("   ");
                builder.AppendLine(detail.Description);
            }

            return builder.ToString().TrimEnd();
        }

        private static IReadOnlyList<CliKeyMetadata> ResolveKeyMetadata(string kind, IReadOnlyList<string> keys)
        {
            IReadOnlyList<CliKeyMetadata> knownMetadata = kind.ToLowerInvariant() switch
            {
                "config" => CliConfigManager.GetKeyMetadata(),
                "runtime" => CliRuntimeManager.GetKeyMetadata(),
                _ => [],
            };
            Dictionary<string, CliKeyMetadata> byKey = knownMetadata.ToDictionary(
                static detail => detail.Key,
                StringComparer.OrdinalIgnoreCase);

            return [.. keys.Select(key => byKey.TryGetValue(key, out CliKeyMetadata? detail)
                ? detail
                : CliKeyMetadataFactory.Create(key, "unknown", null, kind))];
        }
    }
}

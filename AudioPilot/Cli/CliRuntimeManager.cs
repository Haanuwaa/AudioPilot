using System.Globalization;
using AudioPilot.Constants;

namespace AudioPilot.Cli
{
    public static class CliRuntimeManager
    {
        private static readonly RuntimeEntry[] Entries =
        [
            CreateIntEntry(
                "process-monitor-fallback-interval-ms",
                AppConstants.Routines.ProcessMonitorFallbackIntervalMs,
                () => RuntimeTuningConfig.ProcessMonitorFallbackIntervalMs,
                parsed => RuntimeTuningConfig.ProcessMonitorFallbackIntervalMs = parsed,
                500,
                10000,
                "Invalid process monitor fallback interval. Use an integer between 500 and 10000."),
            CreateIntEntry(
                "auto-save-debounce-ms",
                AppConstants.Timing.AutoSaveDebounceMs,
                () => RuntimeTuningConfig.AutoSaveDebounceMs,
                parsed => RuntimeTuningConfig.AutoSaveDebounceMs = parsed,
                100,
                10000,
                "Invalid auto-save debounce range. Use an integer between 100 and 10000."),
            CreateIntEntry(
                "output-switch-debounce-ms",
                AppConstants.Timing.OutputSwitchDebounceMs,
                () => RuntimeTuningConfig.OutputSwitchDebounceMs,
                parsed => RuntimeTuningConfig.OutputSwitchDebounceMs = parsed,
                25,
                2000,
                "Invalid output switch debounce range. Use an integer between 25 and 2000."),
            CreateIntEntry(
                "input-switch-debounce-ms",
                AppConstants.Timing.InputSwitchDebounceMs,
                () => RuntimeTuningConfig.InputSwitchDebounceMs,
                parsed => RuntimeTuningConfig.InputSwitchDebounceMs = parsed,
                25,
                2000,
                "Invalid input switch debounce range. Use an integer between 25 and 2000."),
            CreateIntEntry(
                "switch-retry-delay-ms",
                AppConstants.Timing.SwitchRetryDelayMs,
                () => RuntimeTuningConfig.SwitchRetryDelayMs,
                parsed => RuntimeTuningConfig.SwitchRetryDelayMs = parsed,
                10,
                1000,
                "Invalid switch retry delay range. Use an integer between 10 and 1000."),
            CreateIntEntry(
                "switch-retry-max-delay-ms",
                AppConstants.Timing.SwitchRetryMaxDelayMs,
                () => RuntimeTuningConfig.SwitchRetryMaxDelayMs,
                parsed => RuntimeTuningConfig.SwitchRetryMaxDelayMs = parsed,
                10,
                2000,
                "Invalid switch retry max delay range. Use an integer between 10 and 2000."),
            CreateIntEntry(
                "switch-max-retries",
                AppConstants.Timing.SwitchMaxRetries,
                () => RuntimeTuningConfig.SwitchMaxRetries,
                parsed => RuntimeTuningConfig.SwitchMaxRetries = parsed,
                1,
                10,
                "Invalid switch max retries range. Use an integer between 1 and 10."),
            CreateIntEntry(
                "bluetooth-reconnect-max-attempts",
                AppConstants.Bluetooth.ReconnectMaxAttemptsDefault,
                () => BluetoothReconnectRuntimeConfig.MaxAttempts,
                parsed => BluetoothReconnectRuntimeConfig.MaxAttempts = parsed,
                AppConstants.Limits.BluetoothReconnectMinAttempts,
                AppConstants.Limits.BluetoothReconnectMaxAttempts,
                "Invalid reconnect max attempts range. Use an integer between 1 and 3."),
            CreateIntEntry(
                "bluetooth-reconnect-attempt-timeout-ms",
                AppConstants.Timing.BluetoothReconnectAttemptTimeoutMs,
                () => BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
                parsed => BluetoothReconnectRuntimeConfig.AttemptTimeoutMs = parsed,
                AppConstants.Limits.BluetoothReconnectMinAttemptTimeoutMs,
                AppConstants.Limits.BluetoothReconnectMaxAttemptTimeoutMs,
                "Invalid reconnect attempt timeout range. Use an integer between 250 and 10000."),
            CreateIntEntry(
                "bluetooth-reconnect-cooldown-ms",
                AppConstants.Timing.BluetoothReconnectCooldownMs,
                () => BluetoothReconnectRuntimeConfig.CooldownMs,
                parsed => BluetoothReconnectRuntimeConfig.CooldownMs = parsed,
                AppConstants.Limits.BluetoothReconnectMinCooldownMs,
                AppConstants.Limits.BluetoothReconnectMaxCooldownMs,
                "Invalid reconnect cooldown range. Use an integer between 500 and 30000."),
            CreateBoolEntry(
                "bluetooth-reconnect-only-likely",
                AppConstants.Bluetooth.OnlyLikelyBluetoothEndpointsDefault,
                () => BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints,
                parsed => BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints = parsed),
            CreateIntEntry(
                "hotplug-refresh-debounce-ms",
                AppConstants.Timing.HotplugRefreshDebounceMs,
                () => RuntimeTuningConfig.HotplugRefreshDebounceMs,
                parsed => RuntimeTuningConfig.HotplugRefreshDebounceMs = parsed,
                50,
                2000,
                "Invalid hotplug refresh debounce range. Use an integer between 50 and 2000."),
            CreateIntEntry(
                "hotplug-refresh-fast-path-debounce-ms",
                AppConstants.Timing.HotplugRefreshFastPathDebounceMs,
                () => RuntimeTuningConfig.HotplugRefreshFastPathDebounceMs,
                parsed => RuntimeTuningConfig.HotplugRefreshFastPathDebounceMs = parsed,
                25,
                1000,
                "Invalid hotplug refresh fast-path debounce range. Use an integer between 25 and 1000."),
            CreateIntEntry(
                "hotplug-connected-overlay-suppress-ms",
                AppConstants.Timing.HotplugConnectedOverlaySuppressAfterSwitchMs,
                () => RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
                parsed => RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs = parsed,
                0,
                15000,
                "Invalid hotplug connected overlay suppress range. Use an integer between 0 and 15000."),
            CreateIntEntry(
                "mixer-session-refresh-debounce-ms",
                AppConstants.Timing.MixerSessionRefreshDebounceMs,
                () => RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
                parsed => RuntimeTuningConfig.MixerSessionRefreshDebounceMs = parsed,
                50,
                2000,
                "Invalid mixer session refresh debounce range. Use an integer between 50 and 2000."),
            CreateIntEntry(
                "show-window-mixer-refresh-debounce-ms",
                AppConstants.Timing.ShowWindowMixerRefreshDebounceMs,
                () => RuntimeTuningConfig.ShowWindowMixerRefreshDebounceMs,
                parsed => RuntimeTuningConfig.ShowWindowMixerRefreshDebounceMs = parsed,
                25,
                2000,
                "Invalid show-window mixer refresh debounce range. Use an integer between 25 and 2000."),
            CreateIntEntry(
                "visible-mixer-activation-refresh-debounce-ms",
                AppConstants.Timing.VisibleMixerActivationRefreshDebounceMs,
                () => RuntimeTuningConfig.VisibleMixerActivationRefreshDebounceMs,
                parsed => RuntimeTuningConfig.VisibleMixerActivationRefreshDebounceMs = parsed,
                25,
                2000,
                "Invalid visible mixer activation refresh debounce range. Use an integer between 25 and 2000."),
            CreateIntEntry(
                "mixer-snapshot-cache-interactive-ms",
                AppConstants.Timing.SessionSnapshotFastPathCacheInteractiveMs,
                () => RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
                parsed => RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs = parsed,
                0,
                2000,
                "Invalid interactive mixer snapshot cache range. Use an integer between 0 and 2000."),
            CreateIntEntry(
                "mixer-snapshot-cache-background-ms",
                AppConstants.Timing.SessionSnapshotFastPathCacheBackgroundMs,
                () => RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
                parsed => RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs = parsed,
                0,
                4000,
                "Invalid background mixer snapshot cache range. Use an integer between 0 and 4000."),
            CreateIntEntry(
                "mixer-diagnostics-summary-window-seconds",
                AppConstants.Timing.MixerDiagnosticsSummaryWindowSeconds,
                () => RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds,
                parsed => RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds = parsed,
                5,
                300,
                "Invalid mixer diagnostics summary window range. Use an integer between 5 and 300."),
            CreateIntEntry(
                "mixer-cache-window-diagnostics-log-every-n-refreshes",
                AppConstants.Timing.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
                () => RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
                parsed => RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes = parsed,
                1,
                1000,
                "Invalid mixer diagnostics log cadence range. Use an integer between 1 and 1000."),
            CreateIntEntry(
                "resume-hotkey-retry-delay-ms",
                AppConstants.Timing.ResumeHotkeyRetryDelayMs,
                () => RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
                parsed => RuntimeTuningConfig.ResumeHotkeyRetryDelayMs = parsed,
                50,
                5000,
                "Invalid resume hotkey retry delay range. Use an integer between 50 and 5000."),
            CreateIntEntry(
                "bluetooth-reconnect-post-attempt-recheck-delay-ms",
                AppConstants.Timing.BluetoothReconnectPostAttemptRecheckDelayMs,
                () => RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs = parsed,
                100,
                10000,
                "Invalid reconnect post-attempt recheck delay range. Use an integer between 100 and 10000."),
            CreateIntEntry(
                "bluetooth-reconnect-post-attempt-quick-recheck-delay-ms",
                AppConstants.Timing.BluetoothReconnectPostAttemptQuickRecheckDelayMs,
                () => RuntimeTuningConfig.BluetoothReconnectPostAttemptQuickRecheckDelayMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectPostAttemptQuickRecheckDelayMs = parsed,
                50,
                5000,
                "Invalid reconnect quick recheck delay range. Use an integer between 50 and 5000."),
            CreateIntEntry(
                "bluetooth-reconnect-success-stabilize-window-ms",
                AppConstants.Timing.BluetoothReconnectSuccessStabilizeWindowMs,
                () => RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs = parsed,
                1000,
                120000,
                "Invalid reconnect success stabilize window range. Use an integer between 1000 and 120000."),
            CreateIntEntry(
                "bluetooth-reconnect-success-recheck-interval-ms",
                AppConstants.Timing.BluetoothReconnectSuccessRecheckIntervalMs,
                () => RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs = parsed,
                100,
                5000,
                "Invalid reconnect success recheck interval range. Use an integer between 100 and 5000."),
            CreateIntEntry(
                "bluetooth-reconnect-success-recheck-initial-interval-ms",
                AppConstants.Timing.BluetoothReconnectSuccessRecheckInitialIntervalMs,
                () => RuntimeTuningConfig.BluetoothReconnectSuccessRecheckInitialIntervalMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectSuccessRecheckInitialIntervalMs = parsed,
                50,
                5000,
                "Invalid reconnect success initial recheck interval range. Use an integer between 50 and 5000."),
            CreateIntEntry(
                "bluetooth-reconnect-success-recheck-mid-interval-ms",
                AppConstants.Timing.BluetoothReconnectSuccessRecheckMidIntervalMs,
                () => RuntimeTuningConfig.BluetoothReconnectSuccessRecheckMidIntervalMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectSuccessRecheckMidIntervalMs = parsed,
                50,
                5000,
                "Invalid reconnect success mid recheck interval range. Use an integer between 50 and 5000."),
            CreateIntEntry(
                "bluetooth-reconnect-success-observed-recheck-interval-ms",
                AppConstants.Timing.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                () => RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs = parsed,
                50,
                5000,
                "Invalid reconnect success observed recheck interval range. Use an integer between 50 and 5000."),
            CreateIntEntry(
                "bluetooth-reconnect-success-active-stable-ms",
                AppConstants.Timing.BluetoothReconnectSuccessActiveStableMs,
                () => RuntimeTuningConfig.BluetoothReconnectSuccessActiveStableMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectSuccessActiveStableMs = parsed,
                100,
                20000,
                "Invalid reconnect success active stable range. Use an integer between 100 and 20000."),
            CreateIntEntry(
                "bluetooth-reconnect-success-timeout-grace-ms",
                AppConstants.Timing.BluetoothReconnectSuccessTimeoutGraceMs,
                () => RuntimeTuningConfig.BluetoothReconnectSuccessTimeoutGraceMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectSuccessTimeoutGraceMs = parsed,
                0,
                10000,
                "Invalid reconnect success timeout grace range. Use an integer between 0 and 10000."),
            CreateIntEntry(
                "bluetooth-reconnect-deferred-auto-switch-window-ms",
                AppConstants.Timing.BluetoothReconnectDeferredAutoSwitchWindowMs,
                () => RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs = parsed,
                1000,
                300000,
                "Invalid deferred auto-switch window range. Use an integer between 1000 and 300000."),
            CreateIntEntry(
                "bluetooth-reconnect-timeout-circuit-threshold",
                AppConstants.Timing.BluetoothReconnectTimeoutCircuitThreshold,
                () => RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold,
                parsed => RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold = parsed,
                1,
                10,
                "Invalid reconnect timeout circuit threshold range. Use an integer between 1 and 10."),
            CreateIntEntry(
                "bluetooth-reconnect-timeout-circuit-open-ms",
                AppConstants.Timing.BluetoothReconnectTimeoutCircuitOpenMs,
                () => RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs = parsed,
                1000,
                900000,
                "Invalid reconnect timeout circuit open duration range. Use an integer between 1000 and 900000."),
            CreateIntEntry(
                "bluetooth-reconnect-cached-endpoint-probe-attempts",
                AppConstants.Bluetooth.CachedEndpointVisibilityProbeAttempts,
                () => RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts,
                parsed => RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts = parsed,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMinAttempts,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMaxAttempts,
                "Invalid reconnect cached endpoint probe attempts range. Use an integer between 1 and 10."),
            CreateIntEntry(
                "bluetooth-reconnect-cached-endpoint-probe-delay-ms",
                AppConstants.Bluetooth.CachedEndpointVisibilityProbeDelayMs,
                () => RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs,
                parsed => RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs = parsed,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMinDelayMs,
                AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMaxDelayMs,
                "Invalid reconnect cached endpoint probe delay range. Use an integer between 25 and 1000."),
            CreateIntEntry(
                "steam-big-picture-monitor-debounce-ms",
                AppConstants.Routines.SteamBigPictureMonitorDebounceMs,
                () => RuntimeTuningConfig.SteamBigPictureMonitorDebounceMs,
                parsed => RuntimeTuningConfig.SteamBigPictureMonitorDebounceMs = parsed,
                AppConstants.Limits.SteamBigPictureMonitorDebounceMinMs,
                AppConstants.Limits.SteamBigPictureMonitorDebounceMaxMs,
                "Invalid Steam Big Picture monitor debounce range. Use an integer between 25 and 2000."),
            CreateIntEntry(
                "steam-big-picture-confirmation-delay-ms",
                AppConstants.Routines.SteamBigPictureConfirmationDelayMs,
                () => RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs,
                parsed => RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs = parsed,
                AppConstants.Limits.SteamBigPictureConfirmationDelayMinMs,
                AppConstants.Limits.SteamBigPictureConfirmationDelayMaxMs,
                "Invalid Steam Big Picture confirmation delay range. Use an integer between 50 and 5000."),
            CreateIntEntry(
                "media-overlay-browser-same-source-playing-near-start-window-seconds",
                AppConstants.MediaOverlay.BrowserSameSourcePlayingNearStartWindowSeconds,
                () => RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds,
                parsed => RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds = parsed,
                0,
                60,
                "Invalid media overlay browser same-source playing near-start window range. Use an integer between 0 and 60."),
            CreateIntEntry(
                "media-overlay-same-source-paused-candidate-near-start-window-seconds",
                AppConstants.MediaOverlay.SameSourcePausedCandidateNearStartWindowSeconds,
                () => RuntimeTuningConfig.MediaOverlaySameSourcePausedCandidateNearStartWindowSeconds,
                parsed => RuntimeTuningConfig.MediaOverlaySameSourcePausedCandidateNearStartWindowSeconds = parsed,
                0,
                60,
                "Invalid media overlay same-source paused candidate near-start window range. Use an integer between 0 and 60."),
            CreateIntEntry(
                "media-overlay-ambiguous-same-source-near-start-window-seconds",
                AppConstants.MediaOverlay.AmbiguousSameSourceNearStartWindowSeconds,
                () => RuntimeTuningConfig.MediaOverlayAmbiguousSameSourceNearStartWindowSeconds,
                parsed => RuntimeTuningConfig.MediaOverlayAmbiguousSameSourceNearStartWindowSeconds = parsed,
                0,
                10,
                "Invalid media overlay ambiguous same-source near-start window range. Use an integer between 0 and 10."),
            CreateIntEntry(
                "media-overlay-browser-pending-convergence-position-bucket-seconds",
                AppConstants.MediaOverlay.BrowserPendingConvergencePositionBucketSeconds,
                () => RuntimeTuningConfig.MediaOverlayBrowserPendingConvergencePositionBucketSeconds,
                parsed => RuntimeTuningConfig.MediaOverlayBrowserPendingConvergencePositionBucketSeconds = parsed,
                1,
                30,
                "Invalid media overlay browser pending convergence position bucket range. Use an integer between 1 and 30."),
            CreateIntEntry(
                "media-overlay-same-source-metadata-fallback-max-position-delta-seconds",
                AppConstants.MediaOverlay.SameSourceMetadataFallbackMaxPositionDeltaSeconds,
                () => RuntimeTuningConfig.MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds,
                parsed => RuntimeTuningConfig.MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds = parsed,
                0,
                120,
                "Invalid media overlay same-source metadata fallback max position delta range. Use an integer between 0 and 120."),
            CreateIntEntry(
                "media-overlay-preferred-source-single-candidate-trace-throttle-ms",
                AppConstants.MediaOverlay.PreferredSourceSingleCandidateTraceThrottleMs,
                () => RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs,
                parsed => RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs = parsed,
                0,
                10000,
                "Invalid media overlay preferred-source single candidate trace throttle range. Use an integer between 0 and 10000."),
            CreateIntEntry(
                "media-overlay-telemetry-flush-every-events",
                AppConstants.MediaOverlay.TelemetryFlushEveryEvents,
                () => RuntimeTuningConfig.MediaOverlayTelemetryFlushEveryEvents,
                parsed => RuntimeTuningConfig.MediaOverlayTelemetryFlushEveryEvents = parsed,
                1,
                1000,
                "Invalid media overlay telemetry flush event cadence range. Use an integer between 1 and 1000."),
            CreateIntEntry(
                "media-overlay-telemetry-flush-interval-seconds",
                AppConstants.MediaOverlay.TelemetryFlushIntervalSeconds,
                () => RuntimeTuningConfig.MediaOverlayTelemetryFlushIntervalSeconds,
                parsed => RuntimeTuningConfig.MediaOverlayTelemetryFlushIntervalSeconds = parsed,
                1,
                600,
                "Invalid media overlay telemetry flush interval range. Use an integer between 1 and 600."),
            CreateIntEntry(
                "media-overlay-state-trim-command-cadence",
                AppConstants.MediaOverlay.StateTrimCommandCadence,
                () => RuntimeTuningConfig.MediaOverlayStateTrimCommandCadence,
                parsed => RuntimeTuningConfig.MediaOverlayStateTrimCommandCadence = parsed,
                1,
                1000,
                "Invalid media overlay state trim command cadence range. Use an integer between 1 and 1000."),
            CreateIntEntry(
                "media-overlay-state-trim-interval-seconds",
                AppConstants.MediaOverlay.StateTrimIntervalSeconds,
                () => RuntimeTuningConfig.MediaOverlayStateTrimIntervalSeconds,
                parsed => RuntimeTuningConfig.MediaOverlayStateTrimIntervalSeconds = parsed,
                1,
                600,
                "Invalid media overlay state trim interval range. Use an integer between 1 and 600."),
        ];
        private static readonly string[] KnownKeys = [.. Entries.Select(static entry => entry.Key)];
        private static readonly Dictionary<string, RuntimeEntry> EntriesByKey = Entries.ToDictionary(
            static entry => entry.Key,
            StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<string> GetKnownKeys()
        {
            return KnownKeys;
        }

        public static IReadOnlyList<CliKeyMetadata> GetKeyMetadata()
        {
            var persistedKeys = CliConfigManager.GetKnownKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);

            return [.. Entries.Select(entry =>
            {
                bool canPersist = persistedKeys.Contains(entry.Key);
                return entry.Metadata with
                {
                    Scope = canPersist ? "runtime override; persisted via config" : "runtime-only",
                    CurrentValue = entry.GetValue(),
                };
            })];
        }

        public static bool TryGet(string key, out string value, out string? error)
        {
            value = string.Empty;
            error = null;

            if (!TryGetEntry(key, out RuntimeEntry? entry) || entry is null)
            {
                error = CliSuggestionHelper.BuildUnknownKeyError("runtime", key, KnownKeys);
                return false;
            }

            RuntimeEntry resolvedEntry = entry;
            value = resolvedEntry.GetValue();
            return true;
        }

        public static bool TrySet(string key, string value, out string? error)
        {
            if (!TryGetEntry(key, out RuntimeEntry? entry) || entry is null)
            {
                error = CliSuggestionHelper.BuildUnknownKeyError("runtime", key, KnownKeys);
                return false;
            }

            RuntimeEntry resolvedEntry = entry;
            return resolvedEntry.TrySetValue(value, out error);
        }

        private static bool TryGetEntry(string key, out RuntimeEntry? entry)
        {
            return EntriesByKey.TryGetValue(NormalizeKey(key), out entry);
        }

        private static RuntimeEntry CreateIntEntry(
            string key,
            int defaultValue,
            Func<int> getter,
            Action<int> setter,
            int minValue,
            int maxValue,
            string rangeError)
        {
            return CreateCustomEntry(
                key,
                () => getter().ToString(CultureInfo.InvariantCulture),
                (rawValue, out error) =>
                {
                    if (!TryParseIntInRange(rawValue, minValue, maxValue, out int parsed))
                    {
                        error = rangeError;
                        return false;
                    }

                    setter(parsed);
                    error = null;
                    return true;
                },
                CliKeyMetadataFactory.Create(key, "integer", $"{minValue}..{maxValue}", "runtime-only", defaultValue.ToString(CultureInfo.InvariantCulture)));
        }

        private static RuntimeEntry CreateBoolEntry(string key, bool defaultValue, Func<bool> getter, Action<bool> setter)
        {
            return CreateCustomEntry(
                key,
                () => getter() ? "true" : "false",
                (rawValue, out error) =>
                {
                    if (!TryParseBool(rawValue, out bool parsed))
                    {
                        error = "Invalid boolean value. Use true/false.";
                        return false;
                    }

                    setter(parsed);
                    error = null;
                    return true;
                },
                CliKeyMetadataFactory.Create(key, "boolean", null, "runtime-only", defaultValue ? "true" : "false"));
        }

        private static RuntimeEntry CreateCustomEntry(
            string key,
            Func<string> getter,
            TrySetRuntimeValue setter,
            CliKeyMetadata? metadata = null)
        {
            return new RuntimeEntry(
                key,
                getter,
                setter,
                metadata ?? CliKeyMetadataFactory.Create(key, "string", null, "runtime-only"));
        }

        private static bool TryParseIntInRange(string value, int minValue, int maxValue, out int parsed)
        {
            if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }

            return parsed >= minValue && parsed <= maxValue;
        }

        private static bool TryParseBool(string value, out bool parsed)
        {
            string normalized = value.Trim();
            if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase))
            {
                parsed = true;
                return true;
            }

            if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase))
            {
                parsed = false;
                return true;
            }

            return bool.TryParse(normalized, out parsed);
        }

        private static string NormalizeKey(string key)
        {
            return key.Trim().ToLowerInvariant();
        }

        private delegate bool TrySetRuntimeValue(string value, out string? error);

        private sealed record RuntimeEntry(
            string Key,
            Func<string> GetValue,
            TrySetRuntimeValue TrySetValue,
            CliKeyMetadata Metadata);
    }
}

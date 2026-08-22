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
                "process-monitor-fallback-interval-ms" => "Process polling interval when event monitoring is unavailable. Updates active polling without resetting its baseline; has no effect in event-driven mode. Short-lived processes can still be missed. Resets on restart.",
                "auto-save-debounce-ms" => "Waits after the last edit before auto-saving. Larger values reduce writes but delay persistence.",
                "output-switch-debounce-ms" => "Coalesces repeated output-switch requests. Smaller values respond sooner but allow more switching.",
                "input-switch-debounce-ms" => "Coalesces repeated microphone-switch requests. Smaller values respond sooner but allow more switching.",
                "switch-retry-delay-ms" => "Delay before retrying an unverified device-role switch. Larger values give Windows more time to settle.",
                "switch-retry-max-delay-ms" => "Delay after a COM failure during device-role switching; this is not a total switch timeout.",
                "switch-max-retries" => "Maximum device-role switch attempts, including the first. More attempts can increase failure latency.",
                "bluetooth-reconnect-max-attempts" => "Maximum reconnect attempts per eligible Bluetooth endpoint. More attempts may help intermittent devices but take longer.",
                "bluetooth-reconnect-attempt-timeout-ms" => "Time budget for each Bluetooth reconnect attempt. This is separate from waiting for the audio endpoint afterward.",
                "bluetooth-reconnect-cooldown-ms" => "Minimum spacing between reconnect attempts for the same endpoint across requests.",
                "bluetooth-reconnect-only-likely" => "Restricts reconnect attempts to endpoints identified as likely Bluetooth. Disable only to diagnose a misidentified endpoint.",
                "bluetooth-reconnect-success-stabilize-window-ms" => "Maximum foreground wait for an audio endpoint after Bluetooth reconnects. Longer windows accommodate slow drivers but delay failure feedback.",
                "bluetooth-reconnect-post-attempt-recheck-delay-ms" => "Fallback delay before checking devices after a reconnect attempt.",
                "bluetooth-reconnect-post-attempt-quick-recheck-delay-ms" => "Short delay used for the quick device check after a reconnect attempt.",
                "bluetooth-reconnect-success-recheck-initial-interval-ms" => "Audio-endpoint check interval early in reconnect recovery. Smaller values increase checking work.",
                "bluetooth-reconnect-success-recheck-mid-interval-ms" => "Audio-endpoint check interval during the middle of reconnect recovery.",
                "bluetooth-reconnect-success-recheck-interval-ms" => "Audio-endpoint check interval later in reconnect recovery and during deferred recovery.",
                "bluetooth-reconnect-success-observed-recheck-interval-ms" => "Audio-endpoint check interval after a Bluetooth connection has been observed.",
                "bluetooth-reconnect-success-active-stable-ms" => "How long an observed active endpoint must remain stable in recovery paths that require stability.",
                "bluetooth-reconnect-success-timeout-grace-ms" => "Extra endpoint-observation grace period used when reconnect recovery reaches its timeout.",
                "bluetooth-reconnect-deferred-auto-switch-window-ms" => "How long deferred recovery watches for a late audio endpoint after foreground recovery finishes.",
                "bluetooth-reconnect-timeout-circuit-threshold" => "Repeated reconnect timeouts needed to temporarily stop retrying an endpoint.",
                "bluetooth-reconnect-timeout-circuit-open-ms" => "How long retries pause after an endpoint repeatedly times out.",
                "hotplug-refresh-debounce-ms" => "Coalesces device-change notifications before refreshing. Larger values reduce refresh work but delay updates.",
                "hotplug-refresh-fast-path-debounce-ms" => "Short refresh delay while the app is hidden or for the first pending device-change signal.",
                "hotplug-connected-overlay-suppress-ms" => "Suppresses redundant connected-device overlays shortly after switching. Zero removes this suppression.",
                "mixer-session-refresh-debounce-ms" => "Coalesces audio-session refresh work. Smaller values update sooner but can increase CPU use during session churn.",
                "show-window-mixer-refresh-debounce-ms" => "Delay before refreshing mixer sessions when showing the app.",
                "visible-mixer-activation-refresh-debounce-ms" => "Delay before refreshing an already-visible mixer when the app is activated.",
                "mixer-snapshot-cache-interactive-ms" => "How long an interactive mixer snapshot may be reused. Zero disables reuse; larger values can show older data.",
                "mixer-snapshot-cache-background-ms" => "How long a background mixer snapshot may be reused. Larger values reduce enumeration work but allow older data.",
                "mixer-diagnostics-summary-window-seconds" => "Time window for aggregated mixer diagnostic summaries. Smaller values produce more frequent logs.",
                "mixer-cache-window-diagnostics-log-every-n-refreshes" => "Refreshes between mixer cache diagnostic entries. Smaller values produce more logging.",
                "resume-hotkey-retry-delay-ms" => "Delay before retrying hotkey registration after resume.",
                "media-overlay-browser-same-source-playing-near-start-window-seconds" => "Treats playing browser candidates within this many seconds of the start as newly started during media-session selection.",
                "media-overlay-same-source-paused-candidate-near-start-window-seconds" => "Near-start threshold for paused media candidates from the same app; affects ambiguous-session selection.",
                "media-overlay-ambiguous-same-source-near-start-window-seconds" => "Near-start threshold when resolving ambiguous media candidates from the same app.",
                "media-overlay-browser-pending-convergence-position-bucket-seconds" => "Groups browser playback positions into buckets while reconciling conflicting session observations.",
                "media-overlay-same-source-metadata-fallback-max-position-delta-seconds" => "Largest playback-position difference allowed for same-app metadata fallback. Larger values permit looser matches.",
                "media-overlay-preferred-source-single-candidate-trace-throttle-ms" => "Minimum spacing between repeated preferred-media-source trace messages. Zero disables throttling.",
                "media-overlay-telemetry-flush-every-events" => "Media events between diagnostic summary flushes. Smaller values increase logging.",
                "media-overlay-telemetry-flush-interval-seconds" => "Elapsed-time threshold checked during media activity for flushing diagnostic summaries; does not add an idle timer.",
                "media-overlay-state-trim-command-cadence" => "Media commands between stale-state cleanup checks.",
                "media-overlay-state-trim-interval-seconds" => "Elapsed-time threshold checked during media activity for stale-state cleanup; does not add an idle timer.",
                "steam-big-picture-monitor-debounce-ms" => "Coalesces bursts of Steam window events before verifying Big Picture state.",
                "steam-big-picture-confirmation-delay-ms" => "Waits before the confirmation pass when Steam Big Picture may be closing.",
                "bluetooth-reconnect-cached-endpoint-probe-attempts" => "Connection checks after Windows reports a Bluetooth endpoint is already paired. More checks can extend the wait.",
                "bluetooth-reconnect-cached-endpoint-probe-delay-ms" => "Delay between post-pair Bluetooth connection checks. Smaller values check more often.",
                _ => $"Controls {key.Replace('-', ' ')}.",
            };
        }
    }
}

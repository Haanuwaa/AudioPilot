using AudioPilot.Cli;
using AudioPilot.Constants;

namespace AudioPilot.Tests.Cli;

[Collection("RuntimeTuningConfigIsolation")]
public sealed class CliRuntimeManagerTests
{
    public CliRuntimeManagerTests()
    {
        BluetoothReconnectRuntimeConfig.MaxAttempts = AppConstants.Limits.BluetoothReconnectMaxAttempts;
        BluetoothReconnectRuntimeConfig.AttemptTimeoutMs = AppConstants.Timing.BluetoothReconnectAttemptTimeoutMs;
        BluetoothReconnectRuntimeConfig.CooldownMs = AppConstants.Timing.BluetoothReconnectCooldownMs;
        BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints = true;

        RuntimeTuningConfig.HotplugRefreshDebounceMs = AppConstants.Timing.HotplugRefreshDebounceMs;
        RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs = AppConstants.Timing.HotplugConnectedOverlaySuppressAfterSwitchMs;
        RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs = AppConstants.Timing.SessionSnapshotFastPathCacheInteractiveMs;
        RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs = AppConstants.Timing.SessionSnapshotFastPathCacheBackgroundMs;
        RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds = AppConstants.Timing.MixerDiagnosticsSummaryWindowSeconds;
        RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes = AppConstants.Timing.MixerCacheWindowDiagnosticsLogEveryNRefreshes;
        RuntimeTuningConfig.ResumeHotkeyRetryDelayMs = AppConstants.Timing.ResumeHotkeyRetryDelayMs;
        RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs = AppConstants.Timing.BluetoothReconnectPostAttemptRecheckDelayMs;
        RuntimeTuningConfig.BluetoothReconnectPostAttemptQuickRecheckDelayMs = AppConstants.Timing.BluetoothReconnectPostAttemptQuickRecheckDelayMs;
        RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs = AppConstants.Timing.BluetoothReconnectSuccessStabilizeWindowMs;
        RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs = AppConstants.Timing.BluetoothReconnectSuccessRecheckIntervalMs;
        RuntimeTuningConfig.BluetoothReconnectSuccessRecheckInitialIntervalMs = AppConstants.Timing.BluetoothReconnectSuccessRecheckInitialIntervalMs;
        RuntimeTuningConfig.BluetoothReconnectSuccessRecheckMidIntervalMs = AppConstants.Timing.BluetoothReconnectSuccessRecheckMidIntervalMs;
        RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs = AppConstants.Timing.BluetoothReconnectSuccessObservedRecheckIntervalMs;
        RuntimeTuningConfig.BluetoothReconnectSuccessActiveStableMs = AppConstants.Timing.BluetoothReconnectSuccessActiveStableMs;
        RuntimeTuningConfig.BluetoothReconnectSuccessTimeoutGraceMs = AppConstants.Timing.BluetoothReconnectSuccessTimeoutGraceMs;
        RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs = AppConstants.Timing.BluetoothReconnectDeferredAutoSwitchWindowMs;
        RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold = AppConstants.Timing.BluetoothReconnectTimeoutCircuitThreshold;
        RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs = AppConstants.Timing.BluetoothReconnectTimeoutCircuitOpenMs;
        RuntimeTuningConfig.AutoSaveDebounceMs = AppConstants.Timing.AutoSaveDebounceMs;
        RuntimeTuningConfig.OutputSwitchDebounceMs = AppConstants.Timing.OutputSwitchDebounceMs;
        RuntimeTuningConfig.InputSwitchDebounceMs = AppConstants.Timing.InputSwitchDebounceMs;
        RuntimeTuningConfig.SwitchRetryDelayMs = AppConstants.Timing.SwitchRetryDelayMs;
        RuntimeTuningConfig.SwitchRetryMaxDelayMs = AppConstants.Timing.SwitchRetryMaxDelayMs;
        RuntimeTuningConfig.SwitchMaxRetries = AppConstants.Timing.SwitchMaxRetries;
        RuntimeTuningConfig.MixerSessionRefreshDebounceMs = AppConstants.Timing.MixerSessionRefreshDebounceMs;
        RuntimeTuningConfig.MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds = AppConstants.MediaOverlay.BrowserSameSourcePlayingNearStartWindowSeconds;
        RuntimeTuningConfig.MediaOverlaySameSourcePausedCandidateNearStartWindowSeconds = AppConstants.MediaOverlay.SameSourcePausedCandidateNearStartWindowSeconds;
        RuntimeTuningConfig.MediaOverlayAmbiguousSameSourceNearStartWindowSeconds = AppConstants.MediaOverlay.AmbiguousSameSourceNearStartWindowSeconds;
        RuntimeTuningConfig.MediaOverlayBrowserPendingConvergencePositionBucketSeconds = AppConstants.MediaOverlay.BrowserPendingConvergencePositionBucketSeconds;
        RuntimeTuningConfig.MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds = AppConstants.MediaOverlay.SameSourceMetadataFallbackMaxPositionDeltaSeconds;
        RuntimeTuningConfig.MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs = AppConstants.MediaOverlay.PreferredSourceSingleCandidateTraceThrottleMs;
    }

    [Fact]
    public void TrySetThenGet_BluetoothReconnectMaxAttempts_RoundTrips()
    {
        bool updated = CliRuntimeManager.TrySet("bluetooth-reconnect-max-attempts", "2", out string? setError);
        bool found = CliRuntimeManager.TryGet("bluetooth-reconnect-max-attempts", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("2", value);
    }

    [Fact]
    public void TrySetThenGet_HotplugRefreshDebounce_RoundTrips()
    {
        bool updated = CliRuntimeManager.TrySet("hotplug-refresh-debounce-ms", "420", out string? setError);
        bool found = CliRuntimeManager.TryGet("hotplug-refresh-debounce-ms", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("420", value);
    }

    [Theory]
    [InlineData("auto-save-debounce-ms", "900")]
    [InlineData("output-switch-debounce-ms", "125")]
    [InlineData("input-switch-debounce-ms", "175")]
    [InlineData("switch-retry-delay-ms", "40")]
    [InlineData("switch-retry-max-delay-ms", "140")]
    [InlineData("switch-max-retries", "4")]
    [InlineData("mixer-session-refresh-debounce-ms", "450")]
    [InlineData("bluetooth-reconnect-success-observed-recheck-interval-ms", "220")]
    [InlineData("media-overlay-browser-same-source-playing-near-start-window-seconds", "15")]
    [InlineData("media-overlay-same-source-paused-candidate-near-start-window-seconds", "10")]
    [InlineData("media-overlay-ambiguous-same-source-near-start-window-seconds", "2")]
    [InlineData("media-overlay-browser-pending-convergence-position-bucket-seconds", "6")]
    [InlineData("media-overlay-same-source-metadata-fallback-max-position-delta-seconds", "20")]
    [InlineData("media-overlay-preferred-source-single-candidate-trace-throttle-ms", "1500")]
    public void TrySetThenGet_AdditionalRuntimeKeys_RoundTrips(string key, string expectedValue)
    {
        bool updated = CliRuntimeManager.TrySet(key, expectedValue, out string? setError);
        bool found = CliRuntimeManager.TryGet(key, out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void TrySetThenGet_TimeoutCircuitThreshold_RoundTrips()
    {
        bool updated = CliRuntimeManager.TrySet("bluetooth-reconnect-timeout-circuit-threshold", "3", out string? setError);
        bool found = CliRuntimeManager.TryGet("bluetooth-reconnect-timeout-circuit-threshold", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("3", value);
    }

    [Fact]
    public void TrySetThenGet_BluetoothReconnectSuccessTimeoutGrace_RoundTrips()
    {
        bool updated = CliRuntimeManager.TrySet("bluetooth-reconnect-success-timeout-grace-ms", "750", out string? setError);
        bool found = CliRuntimeManager.TryGet("bluetooth-reconnect-success-timeout-grace-ms", out string value, out string? getError);

        Assert.True(updated);
        Assert.Null(setError);
        Assert.True(found);
        Assert.Null(getError);
        Assert.Equal("750", value);
    }

    [Theory]
    [InlineData("auto-save-debounce-ms", "99")]
    [InlineData("output-switch-debounce-ms", "24")]
    [InlineData("input-switch-debounce-ms", "2001")]
    [InlineData("switch-retry-delay-ms", "9")]
    [InlineData("switch-retry-max-delay-ms", "2001")]
    [InlineData("switch-max-retries", "11")]
    [InlineData("mixer-session-refresh-debounce-ms", "49")]
    [InlineData("bluetooth-reconnect-success-observed-recheck-interval-ms", "49")]
    [InlineData("hotplug-refresh-debounce-ms", "10")]
    [InlineData("mixer-snapshot-cache-interactive-ms", "5000")]
    [InlineData("bluetooth-reconnect-only-likely", "maybe")]
    [InlineData("mixer-diagnostics-summary-window-seconds", "4")]
    [InlineData("bluetooth-reconnect-timeout-circuit-open-ms", "999")]
    [InlineData("bluetooth-reconnect-success-timeout-grace-ms", "10001")]
    [InlineData("media-overlay-browser-same-source-playing-near-start-window-seconds", "61")]
    [InlineData("media-overlay-same-source-paused-candidate-near-start-window-seconds", "61")]
    [InlineData("media-overlay-ambiguous-same-source-near-start-window-seconds", "11")]
    [InlineData("media-overlay-browser-pending-convergence-position-bucket-seconds", "0")]
    [InlineData("media-overlay-same-source-metadata-fallback-max-position-delta-seconds", "121")]
    [InlineData("media-overlay-preferred-source-single-candidate-trace-throttle-ms", "10001")]
    public void TrySet_InvalidValue_ReturnsError(string key, string value)
    {
        bool updated = CliRuntimeManager.TrySet(key, value, out string? error);

        Assert.False(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryGet_RemovedVisibleMixerPollKey_ReturnsUnknownKey()
    {
        bool found = CliRuntimeManager.TryGet("mixer-visible-refresh-poll-interval-ms", out string value, out string? error);

        Assert.False(found);
        Assert.Equal(string.Empty, value);
        Assert.Equal("Unknown runtime key 'mixer-visible-refresh-poll-interval-ms'.", error);
    }
}

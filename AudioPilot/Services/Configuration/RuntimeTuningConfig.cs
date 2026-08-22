using AudioPilot.Constants;

namespace AudioPilot.Services.Configuration
{
    public static class RuntimeTuningConfig
    {
        private static int _autoSaveDebounceMs = AppConstants.Timing.AutoSaveDebounceMs;
        private static int _outputSwitchDebounceMs = AppConstants.Timing.OutputSwitchDebounceMs;
        private static int _inputSwitchDebounceMs = AppConstants.Timing.InputSwitchDebounceMs;
        private static int _switchRetryDelayMs = AppConstants.Timing.SwitchRetryDelayMs;
        private static int _switchRetryMaxDelayMs = AppConstants.Timing.SwitchRetryMaxDelayMs;
        private static int _switchMaxRetries = AppConstants.Timing.SwitchMaxRetries;
        private static int _hotplugRefreshDebounceMs = AppConstants.Timing.HotplugRefreshDebounceMs;
        private static int _hotplugRefreshFastPathDebounceMs = AppConstants.Timing.HotplugRefreshFastPathDebounceMs;
        private static int _hotplugConnectedOverlaySuppressMs = AppConstants.Timing.HotplugConnectedOverlaySuppressAfterSwitchMs;
        private static int _mixerSessionRefreshDebounceMs = AppConstants.Timing.MixerSessionRefreshDebounceMs;
        private static int _showWindowMixerRefreshDebounceMs = AppConstants.Timing.ShowWindowMixerRefreshDebounceMs;
        private static int _visibleMixerActivationRefreshDebounceMs = AppConstants.Timing.VisibleMixerActivationRefreshDebounceMs;
        private static int _mixerSnapshotCacheInteractiveMs = AppConstants.Timing.SessionSnapshotFastPathCacheInteractiveMs;
        private static int _mixerSnapshotCacheBackgroundMs = AppConstants.Timing.SessionSnapshotFastPathCacheBackgroundMs;
        private static int _mixerDiagnosticsSummaryWindowSeconds = AppConstants.Timing.MixerDiagnosticsSummaryWindowSeconds;
        private static int _mixerCacheWindowDiagnosticsLogEveryNRefreshes = AppConstants.Timing.MixerCacheWindowDiagnosticsLogEveryNRefreshes;
        private static int _resumeHotkeyRetryDelayMs = AppConstants.Timing.ResumeHotkeyRetryDelayMs;
        private static int _bluetoothReconnectPostAttemptRecheckDelayMs = AppConstants.Timing.BluetoothReconnectPostAttemptRecheckDelayMs;
        private static int _bluetoothReconnectPostAttemptQuickRecheckDelayMs = AppConstants.Timing.BluetoothReconnectPostAttemptQuickRecheckDelayMs;
        private static int _bluetoothReconnectSuccessStabilizeWindowMs = AppConstants.Timing.BluetoothReconnectSuccessStabilizeWindowMs;
        private static int _bluetoothReconnectSuccessRecheckIntervalMs = AppConstants.Timing.BluetoothReconnectSuccessRecheckIntervalMs;
        private static int _bluetoothReconnectSuccessRecheckInitialIntervalMs = AppConstants.Timing.BluetoothReconnectSuccessRecheckInitialIntervalMs;
        private static int _bluetoothReconnectSuccessRecheckMidIntervalMs = AppConstants.Timing.BluetoothReconnectSuccessRecheckMidIntervalMs;
        private static int _bluetoothReconnectSuccessObservedRecheckIntervalMs = AppConstants.Timing.BluetoothReconnectSuccessObservedRecheckIntervalMs;
        private static int _bluetoothReconnectSuccessActiveStableMs = AppConstants.Timing.BluetoothReconnectSuccessActiveStableMs;
        private static int _bluetoothReconnectSuccessTimeoutGraceMs = AppConstants.Timing.BluetoothReconnectSuccessTimeoutGraceMs;
        private static int _bluetoothReconnectDeferredAutoSwitchWindowMs = AppConstants.Timing.BluetoothReconnectDeferredAutoSwitchWindowMs;
        private static int _bluetoothReconnectTimeoutCircuitThreshold = AppConstants.Timing.BluetoothReconnectTimeoutCircuitThreshold;
        private static int _bluetoothReconnectTimeoutCircuitOpenMs = AppConstants.Timing.BluetoothReconnectTimeoutCircuitOpenMs;
        private static int _bluetoothReconnectCachedEndpointVisibilityProbeAttempts = AppConstants.Bluetooth.CachedEndpointVisibilityProbeAttempts;
        private static int _bluetoothReconnectCachedEndpointVisibilityProbeDelayMs = AppConstants.Bluetooth.CachedEndpointVisibilityProbeDelayMs;
        private static int _steamBigPictureMonitorDebounceMs = AppConstants.Routines.SteamBigPictureMonitorDebounceMs;
        private static int _steamBigPictureConfirmationDelayMs = AppConstants.Routines.SteamBigPictureConfirmationDelayMs;
        private static int _mediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds = AppConstants.MediaOverlay.BrowserSameSourcePlayingNearStartWindowSeconds;
        private static int _mediaOverlaySameSourcePausedCandidateNearStartWindowSeconds = AppConstants.MediaOverlay.SameSourcePausedCandidateNearStartWindowSeconds;
        private static int _mediaOverlayAmbiguousSameSourceNearStartWindowSeconds = AppConstants.MediaOverlay.AmbiguousSameSourceNearStartWindowSeconds;
        private static int _mediaOverlayBrowserPendingConvergencePositionBucketSeconds = AppConstants.MediaOverlay.BrowserPendingConvergencePositionBucketSeconds;
        private static int _mediaOverlayPreferredSourceSingleCandidateTraceThrottleMs = AppConstants.MediaOverlay.PreferredSourceSingleCandidateTraceThrottleMs;
        private static int _mediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds = AppConstants.MediaOverlay.SameSourceMetadataFallbackMaxPositionDeltaSeconds;
        private static int _mediaOverlayTelemetryFlushEveryEvents = AppConstants.MediaOverlay.TelemetryFlushEveryEvents;
        private static int _mediaOverlayTelemetryFlushIntervalSeconds = AppConstants.MediaOverlay.TelemetryFlushIntervalSeconds;
        private static int _mediaOverlayStateTrimCommandCadence = AppConstants.MediaOverlay.StateTrimCommandCadence;
        private static int _mediaOverlayStateTrimIntervalSeconds = AppConstants.MediaOverlay.StateTrimIntervalSeconds;

        public static int AutoSaveDebounceMs
        {
            get => Volatile.Read(ref _autoSaveDebounceMs);
            set => Volatile.Write(ref _autoSaveDebounceMs, Math.Clamp(value, 100, 10000));
        }

        public static int OutputSwitchDebounceMs
        {
            get => Volatile.Read(ref _outputSwitchDebounceMs);
            set => Volatile.Write(ref _outputSwitchDebounceMs, Math.Clamp(value, 25, 2000));
        }

        public static int InputSwitchDebounceMs
        {
            get => Volatile.Read(ref _inputSwitchDebounceMs);
            set => Volatile.Write(ref _inputSwitchDebounceMs, Math.Clamp(value, 25, 2000));
        }

        public static int SwitchRetryDelayMs
        {
            get => Volatile.Read(ref _switchRetryDelayMs);
            set => Volatile.Write(ref _switchRetryDelayMs, Math.Clamp(value, 10, 1000));
        }

        public static int SwitchRetryMaxDelayMs
        {
            get => Volatile.Read(ref _switchRetryMaxDelayMs);
            set => Volatile.Write(ref _switchRetryMaxDelayMs, Math.Clamp(value, 10, 2000));
        }

        public static int SwitchMaxRetries
        {
            get => Volatile.Read(ref _switchMaxRetries);
            set => Volatile.Write(ref _switchMaxRetries, Math.Clamp(value, 1, 10));
        }

        public static int HotplugRefreshDebounceMs
        {
            get => Volatile.Read(ref _hotplugRefreshDebounceMs);
            set => Volatile.Write(ref _hotplugRefreshDebounceMs, Math.Clamp(value, 50, 2000));
        }

        public static int HotplugRefreshFastPathDebounceMs
        {
            get => Volatile.Read(ref _hotplugRefreshFastPathDebounceMs);
            set => Volatile.Write(ref _hotplugRefreshFastPathDebounceMs, Math.Clamp(value, 25, 1000));
        }

        public static int HotplugConnectedOverlaySuppressAfterSwitchMs
        {
            get => Volatile.Read(ref _hotplugConnectedOverlaySuppressMs);
            set => Volatile.Write(ref _hotplugConnectedOverlaySuppressMs, Math.Clamp(value, 0, 15000));
        }

        public static int MixerSessionRefreshDebounceMs
        {
            get => Volatile.Read(ref _mixerSessionRefreshDebounceMs);
            set => Volatile.Write(ref _mixerSessionRefreshDebounceMs, Math.Clamp(value, 50, 2000));
        }

        public static int ShowWindowMixerRefreshDebounceMs
        {
            get => Volatile.Read(ref _showWindowMixerRefreshDebounceMs);
            set => Volatile.Write(ref _showWindowMixerRefreshDebounceMs, Math.Clamp(value, 25, 2000));
        }

        public static int VisibleMixerActivationRefreshDebounceMs
        {
            get => Volatile.Read(ref _visibleMixerActivationRefreshDebounceMs);
            set => Volatile.Write(ref _visibleMixerActivationRefreshDebounceMs, Math.Clamp(value, 25, 2000));
        }

        public static int MixerSnapshotCacheInteractiveMs
        {
            get => Volatile.Read(ref _mixerSnapshotCacheInteractiveMs);
            set => Volatile.Write(ref _mixerSnapshotCacheInteractiveMs, Math.Clamp(value, 0, 2000));
        }

        public static int MixerSnapshotCacheBackgroundMs
        {
            get => Volatile.Read(ref _mixerSnapshotCacheBackgroundMs);
            set => Volatile.Write(ref _mixerSnapshotCacheBackgroundMs, Math.Clamp(value, 0, 4000));
        }

        public static int MixerDiagnosticsSummaryWindowSeconds
        {
            get => Volatile.Read(ref _mixerDiagnosticsSummaryWindowSeconds);
            set => Volatile.Write(ref _mixerDiagnosticsSummaryWindowSeconds, Math.Clamp(value, 5, 300));
        }

        public static int MixerCacheWindowDiagnosticsLogEveryNRefreshes
        {
            get => Volatile.Read(ref _mixerCacheWindowDiagnosticsLogEveryNRefreshes);
            set => Volatile.Write(ref _mixerCacheWindowDiagnosticsLogEveryNRefreshes, Math.Clamp(value, 1, 1000));
        }

        public static int ResumeHotkeyRetryDelayMs
        {
            get => Volatile.Read(ref _resumeHotkeyRetryDelayMs);
            set => Volatile.Write(ref _resumeHotkeyRetryDelayMs, Math.Clamp(value, 50, 5000));
        }

        public static int BluetoothReconnectPostAttemptRecheckDelayMs
        {
            get => Volatile.Read(ref _bluetoothReconnectPostAttemptRecheckDelayMs);
            set => Volatile.Write(ref _bluetoothReconnectPostAttemptRecheckDelayMs, Math.Clamp(value, 100, 10000));
        }

        public static int BluetoothReconnectPostAttemptQuickRecheckDelayMs
        {
            get => Volatile.Read(ref _bluetoothReconnectPostAttemptQuickRecheckDelayMs);
            set => Volatile.Write(ref _bluetoothReconnectPostAttemptQuickRecheckDelayMs, Math.Clamp(value, 50, 5000));
        }

        public static int BluetoothReconnectSuccessStabilizeWindowMs
        {
            get => Volatile.Read(ref _bluetoothReconnectSuccessStabilizeWindowMs);
            set => Volatile.Write(ref _bluetoothReconnectSuccessStabilizeWindowMs, Math.Clamp(value, 1000, 120000));
        }

        public static int BluetoothReconnectSuccessRecheckIntervalMs
        {
            get => Volatile.Read(ref _bluetoothReconnectSuccessRecheckIntervalMs);
            set => Volatile.Write(ref _bluetoothReconnectSuccessRecheckIntervalMs, Math.Clamp(value, 100, 5000));
        }

        public static int BluetoothReconnectSuccessRecheckInitialIntervalMs
        {
            get => Volatile.Read(ref _bluetoothReconnectSuccessRecheckInitialIntervalMs);
            set => Volatile.Write(ref _bluetoothReconnectSuccessRecheckInitialIntervalMs, Math.Clamp(value, 50, 5000));
        }

        public static int BluetoothReconnectSuccessRecheckMidIntervalMs
        {
            get => Volatile.Read(ref _bluetoothReconnectSuccessRecheckMidIntervalMs);
            set => Volatile.Write(ref _bluetoothReconnectSuccessRecheckMidIntervalMs, Math.Clamp(value, 50, 5000));
        }

        public static int BluetoothReconnectSuccessObservedRecheckIntervalMs
        {
            get => Volatile.Read(ref _bluetoothReconnectSuccessObservedRecheckIntervalMs);
            set => Volatile.Write(ref _bluetoothReconnectSuccessObservedRecheckIntervalMs, Math.Clamp(value, 50, 5000));
        }

        public static int BluetoothReconnectSuccessActiveStableMs
        {
            get => Volatile.Read(ref _bluetoothReconnectSuccessActiveStableMs);
            set => Volatile.Write(ref _bluetoothReconnectSuccessActiveStableMs, Math.Clamp(value, 100, 20000));
        }

        public static int BluetoothReconnectSuccessTimeoutGraceMs
        {
            get => Volatile.Read(ref _bluetoothReconnectSuccessTimeoutGraceMs);
            set => Volatile.Write(ref _bluetoothReconnectSuccessTimeoutGraceMs, Math.Clamp(value, 0, 10000));
        }

        public static int BluetoothReconnectDeferredAutoSwitchWindowMs
        {
            get => Volatile.Read(ref _bluetoothReconnectDeferredAutoSwitchWindowMs);
            set => Volatile.Write(ref _bluetoothReconnectDeferredAutoSwitchWindowMs, Math.Clamp(value, 1000, 300000));
        }

        public static int BluetoothReconnectTimeoutCircuitThreshold
        {
            get => Volatile.Read(ref _bluetoothReconnectTimeoutCircuitThreshold);
            set => Volatile.Write(ref _bluetoothReconnectTimeoutCircuitThreshold, Math.Clamp(value, 1, 10));
        }

        public static int BluetoothReconnectTimeoutCircuitOpenMs
        {
            get => Volatile.Read(ref _bluetoothReconnectTimeoutCircuitOpenMs);
            set => Volatile.Write(ref _bluetoothReconnectTimeoutCircuitOpenMs, Math.Clamp(value, 1000, 900000));
        }

        public static int BluetoothReconnectCachedEndpointVisibilityProbeAttempts
        {
            get => Volatile.Read(ref _bluetoothReconnectCachedEndpointVisibilityProbeAttempts);
            set => Volatile.Write(
                ref _bluetoothReconnectCachedEndpointVisibilityProbeAttempts,
                Math.Clamp(
                    value,
                    AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMinAttempts,
                    AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMaxAttempts));
        }

        public static int BluetoothReconnectCachedEndpointVisibilityProbeDelayMs
        {
            get => Volatile.Read(ref _bluetoothReconnectCachedEndpointVisibilityProbeDelayMs);
            set => Volatile.Write(
                ref _bluetoothReconnectCachedEndpointVisibilityProbeDelayMs,
                Math.Clamp(
                    value,
                    AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMinDelayMs,
                    AppConstants.Limits.BluetoothReconnectCachedEndpointProbeMaxDelayMs));
        }

        public static int SteamBigPictureMonitorDebounceMs
        {
            get => Volatile.Read(ref _steamBigPictureMonitorDebounceMs);
            set => Volatile.Write(
                ref _steamBigPictureMonitorDebounceMs,
                Math.Clamp(
                    value,
                    AppConstants.Limits.SteamBigPictureMonitorDebounceMinMs,
                    AppConstants.Limits.SteamBigPictureMonitorDebounceMaxMs));
        }

        public static int SteamBigPictureConfirmationDelayMs
        {
            get => Volatile.Read(ref _steamBigPictureConfirmationDelayMs);
            set => Volatile.Write(
                ref _steamBigPictureConfirmationDelayMs,
                Math.Clamp(
                    value,
                    AppConstants.Limits.SteamBigPictureConfirmationDelayMinMs,
                    AppConstants.Limits.SteamBigPictureConfirmationDelayMaxMs));
        }

        public static int MediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds
        {
            get => Volatile.Read(ref _mediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds);
            set => Volatile.Write(ref _mediaOverlayBrowserSameSourcePlayingNearStartWindowSeconds, Math.Clamp(value, 0, 60));
        }

        public static int MediaOverlaySameSourcePausedCandidateNearStartWindowSeconds
        {
            get => Volatile.Read(ref _mediaOverlaySameSourcePausedCandidateNearStartWindowSeconds);
            set => Volatile.Write(ref _mediaOverlaySameSourcePausedCandidateNearStartWindowSeconds, Math.Clamp(value, 0, 60));
        }

        public static int MediaOverlayAmbiguousSameSourceNearStartWindowSeconds
        {
            get => Volatile.Read(ref _mediaOverlayAmbiguousSameSourceNearStartWindowSeconds);
            set => Volatile.Write(ref _mediaOverlayAmbiguousSameSourceNearStartWindowSeconds, Math.Clamp(value, 0, 10));
        }

        public static int MediaOverlayBrowserPendingConvergencePositionBucketSeconds
        {
            get => Volatile.Read(ref _mediaOverlayBrowserPendingConvergencePositionBucketSeconds);
            set => Volatile.Write(ref _mediaOverlayBrowserPendingConvergencePositionBucketSeconds, Math.Clamp(value, 1, 30));
        }

        public static int MediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds
        {
            get => Volatile.Read(ref _mediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds);
            set => Volatile.Write(ref _mediaOverlaySameSourceMetadataFallbackMaxPositionDeltaSeconds, Math.Clamp(value, 0, 120));
        }

        public static int MediaOverlayPreferredSourceSingleCandidateTraceThrottleMs
        {
            get => Volatile.Read(ref _mediaOverlayPreferredSourceSingleCandidateTraceThrottleMs);
            set => Volatile.Write(ref _mediaOverlayPreferredSourceSingleCandidateTraceThrottleMs, Math.Clamp(value, 0, 10000));
        }

        public static int MediaOverlayTelemetryFlushEveryEvents
        {
            get => Volatile.Read(ref _mediaOverlayTelemetryFlushEveryEvents);
            set => Volatile.Write(ref _mediaOverlayTelemetryFlushEveryEvents, Math.Clamp(value, 1, 1000));
        }

        public static int MediaOverlayTelemetryFlushIntervalSeconds
        {
            get => Volatile.Read(ref _mediaOverlayTelemetryFlushIntervalSeconds);
            set => Volatile.Write(ref _mediaOverlayTelemetryFlushIntervalSeconds, Math.Clamp(value, 1, 600));
        }

        public static int MediaOverlayStateTrimCommandCadence
        {
            get => Volatile.Read(ref _mediaOverlayStateTrimCommandCadence);
            set => Volatile.Write(ref _mediaOverlayStateTrimCommandCadence, Math.Clamp(value, 1, 1000));
        }

        public static int MediaOverlayStateTrimIntervalSeconds
        {
            get => Volatile.Read(ref _mediaOverlayStateTrimIntervalSeconds);
            set => Volatile.Write(ref _mediaOverlayStateTrimIntervalSeconds, Math.Clamp(value, 1, 600));
        }
    }
}

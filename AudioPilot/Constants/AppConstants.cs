using System.Windows.Input;

namespace AudioPilot.Constants
{
    public static class AppConstants
    {
        public static class Identity
        {
            public const string AppName = "AudioPilot";
            public const string DisplayName = "AudioPilot";
        }

        public static class Links
        {
            public const string RepositoryUrl = "https://github.com/Haanuwaa/AudioPilot";
            public const string UserGuideUrl = RepositoryUrl + "/blob/main/docs/USER_GUIDE.md";
        }

        public static class Files
        {
            public const string SettingsFileName = "settings.json";
            public const string LogFileName = "AudioPilot.log";
            public const string AboutFileName = "ABOUT.txt";
            public const string IconPath = "pack://application:,,,/Images/sound.ico";
            public const string BackupFolderName = "backups";
            public const int SettingsBackupRetentionCount = 5;
            public const int LogBackupRetentionCount = 5;
        }

        public static class Timing
        {
            public const int ShowCooldownMs = 350;
            public const int FirstPresentationRenderTimeoutMs = 2000;
            public const int TooltipHoverDelayMs = 100;
            public const int StartupDebounceMs = 50;
            public const int AutoSaveDebounceMs = 750;
            public const int DeviceCacheDurationMs = 3000;
            public const int VolumeThrottleIntervalMs = 75;
            public const int ProcessCacheTtlSeconds = 15;
            public const int CacheEntryExpiryMinutes = 10;
            public const int CacheCleanupIntervalMs = 60000;
            public const int SessionMonitorDebounceMs = 150;
            public const int SessionInitDelayMs = 100;
            public const int VolumeCacheTtlMinutes = 15;
            public const int RetryStateTtlMinutes = 30;
            public const int CircuitBreakerCooldownSeconds = 30;
            public const int OutputSwitchDebounceMs = 100;
            public const int InputSwitchDebounceMs = 100;
            public const int SwitchRetryDelayMs = 50;
            public const int SwitchRetryMaxDelayMs = 100;
            public const int SwitchMaxRetries = 3;
            public const int AudioDeviceQueryLogCooldownMs = 100;
            public const int MixerDiagnosticsSummaryWindowSeconds = 30;
            public const int MixerSlowRefreshWarningMs = 900;
            public const int MixerSlowRefreshConsecutiveCount = 3;
            public const int SessionDiagnosticsSummaryWindowSeconds = 30;
            public const int SessionSlowSnapshotWarningMs = 900;
            public const int SessionSlowSnapshotConsecutiveCount = 3;
            public const int OurVolumeSetTtlSeconds = 2;
            public const double OverlayAutoHideSeconds = 2.5;
            public const int SessionCacheShortTtlSeconds = 5;
            public const int PackagedAppInventoryCacheMinutes = 5;
            public const int MixerPrimaryEndpointRefreshCadenceMs = 8000;
            public const int SessionSnapshotFastPathCacheMs = 200;
            public const int SessionSnapshotFastPathCacheInteractiveMs = 100;
            public const int SessionSnapshotFastPathCacheBackgroundMs = 300;
            public const int SessionSnapshotPrewarmReuseMs = 6000;
            public const int MediaOverlayPreferredSourceSingleCandidateTraceRetentionSeconds = 300;
            public const long HotkeyDebounceTicks = 50 * TimeSpan.TicksPerMillisecond;
            public const long HotkeyDebounceRetentionTicks = HotkeyDebounceTicks * 8;
            public const int PendingRestoreTtlSeconds = 25;
            public const int PidFallbackFreshnessSeconds = 5;
            public const int CleanupWaitMs = 750;
            public const int CleanupGraceExtensionMs = 750;
            public const int HotkeyDiagnosticsWindowSeconds = 30;
            public const int DeviceStateMetricsWindowSeconds = 5;
            public const int DeviceStateStormThreshold = 20;
            public const int DeviceStateSummaryWindowSeconds = 30;
            public const int SingleInstanceListenerStopTimeoutSeconds = 1;
            public const int SingleInstanceListenerRetryDelayMs = 100;
            public const int SingleInstanceConnectTimeoutMs = 300;
            public const int SingleInstanceRequestReadTimeoutMs = 3000;
            public const int SingleInstanceResponseWriteTimeoutMs = 3000;
            public const int SingleInstanceResponseTimeoutMs = 30000;
            public const int SingleInstanceRecoveryGracefulCloseTimeoutMs = 1500;
            public const int SingleInstanceRecoveryKillWaitTimeoutMs = 1500;
            public const int SettingsIoCrossProcessLockTimeoutMs = 5000;
            public const int ShutdownStepTimeoutMs = 5000;
            public const int MixerSessionRefreshDebounceMs = 300;
            public const int ShowWindowMixerRefreshDebounceMs = 100;
            public const int VisibleMixerActivationRefreshDebounceMs = 75;
            public const int SystemMediaCommandTimeoutMs = 350;
            public const int HotplugRefreshDebounceMs = 350;
            public const int HotplugRefreshFastPathDebounceMs = 120;
            public const int HotplugConnectedOverlaySuppressAfterSwitchMs = 2200;
            public const int HotplugDiagnosticsLogEveryNAppliedRefreshes = 10;
            public const int MixerCacheWindowDiagnosticsLogEveryNRefreshes = 20;
            public const int VolumeSessionTraceLogEveryN = 25;
            public const int HotkeyExecuteTraceLogEveryN = 25;
            public const int HotkeyDispatchLatencyTraceLogEveryN = 25;
            public const int SwitchSpamGuardDiagnosticsLogEveryN = 25;
            public const int ResumeHotkeyRetryDelayMs = 300;
            public const int BluetoothReconnectAttemptTimeoutMs = 1200;
            public const int BluetoothReconnectCooldownMs = 5000;
            public const int BluetoothReconnectFallbackReservedBudgetMs = 250;
            public const int BluetoothAssociationEndpointMinimalPropertiesRetryBudgetMs = 250;
            public const int BluetoothReconnectPostAttemptRecheckDelayMs = 400;
            public const int BluetoothReconnectPostAttemptQuickRecheckDelayMs = 150;
            public const int BluetoothReconnectSuccessStabilizeWindowMs = 12000;
            public const int BluetoothReconnectSuccessRecheckIntervalMs = 500;
            public const int BluetoothReconnectSuccessRecheckInitialIntervalMs = 220;
            public const int BluetoothReconnectSuccessRecheckMidIntervalMs = 350;
            public const int BluetoothReconnectSuccessObservedRecheckIntervalMs = 100;
            public const int BluetoothReconnectSuccessActiveStableMs = 1000;
            public const int BluetoothReconnectSuccessTimeoutGraceMs = 2000;
            public const int BluetoothReconnectDeferredAutoSwitchWindowMs = 30000;
            public const int BluetoothReconnectTimeoutCircuitThreshold = 2;
            public const int BluetoothReconnectTimeoutCircuitOpenMs = 180000;
            public const int RoutineLastRunRefreshIntervalSeconds = 30;
        }

        public static class Overlay
        {
            public const int TargetWidthPx = 360;
            public const int EmojiRenderCacheMaxEntries = 128;
            public const double MarginDip = 8;
            public const double StackGapDip = 4;
            public const int FadeInDurationMs = 140;
        }

        public static class Routines
        {
            public const int SteamBigPictureMonitorDebounceMs = 150;
            public const int SteamBigPictureConfirmationDelayMs = 650;
            public const int SteamBigPictureFallbackRevalidationIntervalMs = 3000;
            public const int SteamBigPictureHookRetryIntervalMs = 15000;
        }

        public static class Bluetooth
        {
            public const int ReconnectMaxAttemptsDefault = 2;
            public const bool OnlyLikelyBluetoothEndpointsDefault = true;
            public const int RememberedEndpointCacheTtlHours = 72;
            public const int RememberedEndpointCacheMaxEntries = 64;
            public const int CachedEndpointVisibilityProbeAttempts = 4;
            public const int CachedEndpointVisibilityProbeDelayMs = 120;
        }

        public static class Limits
        {
            public const int MaxProcessCacheEntries = 1024;
            public const int MaxMediaOverlayPreferredSourceSingleCandidateTraceEntries = 128;
            public const int MaxHotkeyDebounceEntries = 1024;
            public const int MaxVolumeCacheEntries = 2048;
            public const int MaxVolumeAliasEntries = 4096;
            public const int MaxConcurrentBackgroundTasks = 32;
            public const int MaxDeferredBackgroundOperations = 128;
            public const int MaxPidProcessMapEntries = 2048;
            public const int MaxPackagedAppInventoryEntries = 2048;
            public const int MaxFileDescriptionCacheEntries = 2048;
            public const int MaxAumidCacheEntries = 2048;
            public const int MaxSettingsImportFileBytes = 256 * 1024;
            public const int MaxSettingsImportArchiveEntryBytes = 256 * 1024;
            public const int BluetoothReconnectMinAttempts = 1;
            public const int BluetoothReconnectMaxAttempts = 3;
            public const int BluetoothReconnectMinAttemptTimeoutMs = 250;
            public const int BluetoothReconnectMaxAttemptTimeoutMs = 10000;
            public const int BluetoothReconnectMinCooldownMs = 500;
            public const int BluetoothReconnectMaxCooldownMs = 30000;
            public const int BluetoothReconnectCachedEndpointProbeMinAttempts = 1;
            public const int BluetoothReconnectCachedEndpointProbeMaxAttempts = 10;
            public const int BluetoothReconnectCachedEndpointProbeMinDelayMs = 25;
            public const int BluetoothReconnectCachedEndpointProbeMaxDelayMs = 1000;
            public const int SteamBigPictureMonitorDebounceMinMs = 25;
            public const int SteamBigPictureMonitorDebounceMaxMs = 2000;
            public const int SteamBigPictureConfirmationDelayMinMs = 50;
            public const int SteamBigPictureConfirmationDelayMaxMs = 5000;
        }

        public static class Logging
        {
            public const int MaxQueueSize = 8192;
            public const int MaxBatchSize = 100;
            public const long MaxLogFileBytes = 5 * 1024 * 1024;
            public const int LogResetIntervalDays = 3;
            public const int LogBackupMaxAgeDays = 21;
            public const int DisposeWaitMs = 5000;
            public const int MaxExceptionDetailsChars = 4096;
        }

        public static class Hotkeys
        {
            public const int HotkeyId = 9000;
            public const int ToggleAppVisibilityHotkeyId = 9001;
            public const int MediaPlayPauseId = 9002;
            public const int MediaNextTrackId = 9003;
            public const int MediaPrevTrackId = 9004;
            public const int MediaShowCurrentTrackId = 9005;
            public const int MuteMicId = 9006;
            public const int MuteSoundId = 9007;
            public const int DeafenId = 9008;
            public const int OutputSwitchHotkeyId = 9009;
            public const int InputSwitchHotkeyId = 9010;
            public const int OutputReverseSwitchHotkeyId = 9011;
            public const int InputReverseSwitchHotkeyId = 9012;
            public const int ListenToInputHotkeyId = 9013;
            public const int MasterVolumeUpHotkeyId = 9014;
            public const int MasterVolumeDownHotkeyId = 9015;
            public const int MicVolumeUpHotkeyId = 9016;
            public const int MicVolumeDownHotkeyId = 9017;
            public const int RoutineHotkeyIdBase = 10000;
            public const int RoutineHotkeyIdMaxCount = 512;
            public const uint SyntheticMediaInputMarker = 0x4155504D; // "AUPM"

            public static readonly HashSet<string> ModifierTokens = new(StringComparer.OrdinalIgnoreCase)
            {
                "ctrl", "control", "alt", "shift", "win", "windows"
            };

            public static readonly Dictionary<string, Key> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ctrl"] = Key.LeftCtrl,
                ["control"] = Key.LeftCtrl,
                ["alt"] = Key.LeftAlt,
                ["shift"] = Key.LeftShift,
                ["win"] = Key.LWin,
                ["windows"] = Key.LWin,
            };

            public static readonly Dictionary<string, Key> MainKeyAliases = new(StringComparer.OrdinalIgnoreCase)
            {
                ["."] = Key.OemPeriod,
                [","] = Key.OemComma,
                ["period"] = Key.OemPeriod,
                ["comma"] = Key.OemComma,
                ["+"] = Key.OemPlus,
                ["plus"] = Key.OemPlus,
                ["equals"] = Key.OemPlus,
                ["="] = Key.OemPlus,
                ["-"] = Key.OemMinus,
                ["minus"] = Key.OemMinus,
                ["dash"] = Key.OemMinus,
                ["_"] = Key.OemMinus,
                ["underscore"] = Key.OemMinus,
                ["/"] = Key.Oem2,
                ["slash"] = Key.Oem2,
                ["?"] = Key.Oem2,
                ["question"] = Key.Oem2,
                [";"] = Key.OemSemicolon,
                ["semicolon"] = Key.OemSemicolon,
                [":"] = Key.OemSemicolon,
                ["colon"] = Key.OemSemicolon,
                ["'"] = Key.OemQuotes,
                ["quote"] = Key.OemQuotes,
                ["\""] = Key.OemQuotes,
                ["dblquote"] = Key.OemQuotes,
                ["["] = Key.OemOpenBrackets,
                ["openbracket"] = Key.OemOpenBrackets,
                ["]"] = Key.OemCloseBrackets,
                ["closebracket"] = Key.OemCloseBrackets,
                ["\\"] = Key.OemBackslash,
                ["backslash"] = Key.OemBackslash,
                ["`"] = Key.OemTilde,
                ["tilde"] = Key.OemTilde,

                ["space"] = Key.Space,
                ["enter"] = Key.Enter,
                ["return"] = Key.Enter,
                ["esc"] = Key.Escape,
                ["escape"] = Key.Escape,
                ["back"] = Key.Back,
                ["backspace"] = Key.Back,
                ["tab"] = Key.Tab,
                ["del"] = Key.Delete,
                ["delete"] = Key.Delete,
                ["ins"] = Key.Insert,
                ["insert"] = Key.Insert,
            };

            public static readonly Dictionary<Key, string> MainKeyDisplayAliases = new()
            {
                [Key.OemPeriod] = ".",
                [Key.OemComma] = ",",
                [Key.OemPlus] = "+",
                [Key.OemMinus] = "-",
                [Key.Oem2] = "/",
                [Key.OemSemicolon] = ";",
                [Key.OemQuotes] = "'",
                [Key.OemOpenBrackets] = "[",
                [Key.OemCloseBrackets] = "]",
                [Key.OemBackslash] = "\\",
                [Key.OemTilde] = "`",
            };
        }

        public static class MediaOverlay
        {
            public const int InitialSettleDelayMs = 150;
            public const int SingleSourceInitialSettleDelayMs = 60;
            public const int BrowserSingleSourceInitialSettleDelayMs = 90;
            public const int RetryDelayMs = 100;
            public const int MaxAttempts = 5;
            public const int MaxCaptureDurationMs = 9000;
            public const int CaptureCancellationGraceMs = 250;
            public const int ShutdownDrainTimeoutMs = 2000;
            public const int ExtraAttemptsAfterSessionDrop = 4;
            public const int SessionDropRecoveryInitialDelayMs = 500;
            public const int SessionDropRecoveryRetryDelayMs = 500;
            public const int SessionDropRecoveryAttempts = 6;
            public const int SessionDropTrackLoadRecoveryInitialDelayMs = 1100;
            public const int SessionDropTrackLoadRecoveryRetryDelayMs = 450;
            public const int SessionDropTrackLoadRecoveryAttempts = 5;
            public const int UnchangedRecoveryInitialDelayMs = 500;
            public const int UnchangedRecoveryRetryDelayMs = 320;
            public const int BrowserSingleSourceUnchangedRecoveryInitialDelayMs = 220;
            public const int BrowserSingleSourceUnchangedRecoveryRetryDelayMs = 140;
            public const int BrowserSingleSourceLatePendingCorroborationRetryDelayMs = 60;
            public const int UnchangedRecoveryAttempts = 4;
            public const int FirstCommandGraceWindowMs = 900;
            public const int GraceRecoveryInitialDelayMs = 650;
            public const int GraceRecoveryRetryDelayMs = 380;
            public const int GraceRecoveryAttempts = 4;
            public const int PlayPauseSettleInitialDelayMs = 140;
            public const int PlayPauseSettleRetryDelayMs = 140;
            public const int PlayPauseSettleAttempts = 6;
            public const int TrackLoadRecoveryInitialDelayMs = 700;
            public const int TrackLoadRecoveryRetryDelayMs = 320;
            public const int TrackLoadRecoveryAttempts = 3;
            public const int StagnantTrackRecoveryInitialDelayMs = 1400;
            public const int StagnantTrackRecoveryRetryDelayMs = 450;
            public const int StagnantTrackRecoveryAttempts = 2;
            public const int TimelineJumpThresholdSeconds = 6;
            public const int TimelineResetFromSeconds = 4;
            public const int TimelineResetToSeconds = 1;
            public const int PostDropSameTrackNearStartWindowSeconds = 3;
            public const int BrowserSameSourcePlayingNearStartWindowSeconds = 12;
            public const int SameSourcePausedCandidateNearStartWindowSeconds = 12;
            public const int AmbiguousSameSourceNearStartWindowSeconds = 1;
            public const int BrowserPendingConvergencePositionBucketSeconds = 5;
            public const int SameSourceMetadataFallbackMaxPositionDeltaSeconds = 15;
            public const int PendingEventRecentSignalMaxPositionDeltaSeconds = 60;
            public const int PreferredSourceSingleCandidateTraceThrottleMs = 1000;
            public const int TelemetryFlushEveryEvents = 32;
            public const int TelemetryFlushIntervalSeconds = 60;
            public const int StateTrimCommandCadence = 32;
            public const int StateTrimIntervalSeconds = 30;
            public const int StickySourceTtlSeconds = 8;
            public const int RecentSignalTtlMs = 2500;
            public const int RecentTrustedSourceTtlMs = 2500;
            public const int ConfidentSourceInitialSettleDelayMs = 40;
            public const int StableRepeatedChangedCandidateNearStartWindowSeconds = 3;
            public const int StableRepeatedChangedCandidateMaxForwardAdvanceSeconds = 2;
        }

        public static class Registry
        {
            public const string StartupPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        }

        public static class Audio
        {
            public static class ErrorCodes
            {
                public static class CyclePreflight
                {
                    public const string NoDefaultInputDevice = "no-default-input-device";
                }

                public static class Listen
                {
                    public const string NoDefaultInputDevice = "no-default-input-device";
                    public const string NoDefaultOutputDevice = "no-default-output-device";
                    public const string StateReadFailed = "listen-state-read-failed";
                    public const string StateUnknownType = "listen-state-unknown-type";
                    public const string StateReadException = "listen-state-read-exception";
                    public const string StateSetFailed = "listen-state-set-failed";
                    public const string StateSetException = "listen-state-set-exception";
                    public const string StateVerifyUnknown = "listen-state-verify-unknown";
                    public const string StateVerifyMismatch = "listen-state-verify-mismatch";
                    public const string WriteMmDeviceInterfaceUnavailable = "listen-write-mmdevice-interface-unavailable";
                    public const string WriteRenderIdMissing = "listen-write-render-id-missing";
                    public const string WriteOpenStoreHrPrefix = "listen-write-open-store-hr-0x";
                    public const string WriteRenderSetHrPrefix = "listen-write-render-set-hr-0x";
                    public const string WriteEnabledSetHrPrefix = "listen-write-enabled-set-hr-0x";
                    public const string WriteCommitHrPrefix = "listen-write-commit-hr-0x";
                    public const string WriteFailedHrPrefix = "listen-write-failed-hr-0x";
                    public const string WriteFailedExceptionPrefix = "listen-write-failed:";
                }
            }

            public static class LogEvents
            {
                public static class DeviceNotifications
                {
                    public const string Register = "device-notifications-register";
                    public const string Unregister = "device-notifications-unregister";
                }

                public static class Listen
                {
                    public const string StateReadPropertyStoreException = "listen-state-read-property-store-exception";
                    public const string StateReadException = "listen-state-read-exception";
                    public const string StateSetException = "listen-state-set-exception";
                    public const string SetSuccess = "listen-set-success";
                    public const string SetVerifyUnknown = "listen-set-verify-unknown";
                    public const string SetVerifyMismatch = "listen-set-verify-mismatch";
                    public const string WritePropertyStoreException = "listen-write-property-store-exception";
                    public const string ToggleFailed = "listen-toggle-failed";
                    public const string SetFailed = "listen-set-failed";
                }

                public static class ResumeRecovery
                {
                    public const string Skip = "resume-recovery-skip";
                    public const string Start = "resume-recovery-start";
                    public const string Success = "resume-recovery-success";
                    public const string Failed = "resume-recovery-failed";
                    public const string Summary = "resume-recovery-summary";
                    public const string HotkeysRegister = "resume-hotkeys-register";
                    public const string BestEffortQueueSkip = "resume-recovery-best-effort-queue-skip";
                    public const string BestEffortRegisterFailed = "resume-recovery-best-effort-register-failed";
                    public const string BestEffortMonitorFailed = "resume-recovery-best-effort-monitor-failed";
                    public const string BestEffortFailed = "resume-recovery-best-effort-failed";
                }

                public static class OutputSwitch
                {
                    public const string Failed = "output-switch-failed";
                    public const string Skip = "output-switch-skip";
                    public const string SkipDisconnected = "output-switch-skip-disconnected";
                    public const string Start = "output-switch-start";
                    public const string Phases = "output-switch-phases";
                    public const string EnginePhases = "output-switch-engine-phases";
                    public const string SnapshotCaptured = "output-switch-snapshot-captured";
                    public const string Confirmed = "output-switch-confirmed";
                    public const string PostFailed = "output-switch-post-failed";
                    public const string Success = "output-switch-success";
                    public const string VerifyRetry = "output-switch-verify-retry";
                    public const string ComRetry = "output-switch-com-retry";
                    public const string VerifyFailed = "output-switch-verify-failed";
                    public const string PostSkipRecordingEndpoint = "output-switch-post-skip-recording-endpoint";
                }

                public static class InputSwitch
                {
                    public const string Failed = "input-switch-failed";
                    public const string Skip = "input-switch-skip";
                    public const string SkipDisconnected = "input-switch-skip-disconnected";
                    public const string Start = "input-switch-start";
                    public const string Phases = "input-switch-phases";
                    public const string EnginePhases = "input-switch-engine-phases";
                    public const string Success = "input-switch-success";
                    public const string Retry = "input-switch-retry";
                    public const string ComRetry = "input-switch-com-retry";
                    public const string VerifyFailed = "input-switch-verify-failed";
                    public const string PostFailed = "input-switch-post-failed";
                }

                public static class Lifecycle
                {
                    public const string DisposeTimeout = "dispose-timeout";
                    public const string DisposeCompleteAfterGrace = "dispose-complete-after-grace";
                    public const string DisposeForced = "dispose-forced";
                }

                public static class App
                {
                    public const string CliForwardParseFailed = "cli-forward-parse-failed";
                    public const string CliForwardStart = "cli-forward-start";
                    public const string CliForwardComplete = "cli-forward-complete";
                }

                public static class ViewModel
                {
                    public const string RefreshFailed = "refresh-failed";
                    public const string RefreshSettingsReload = "refresh-settings-reload";
                    public const string HotkeysRegisterSuccess = "hotkeys-register-success";
                    public const string HotkeysRegisterFailed = "hotkeys-register-failed";
                    public const string SwitchHotkeysRegisterFailed = "switch-hotkeys-register-failed";

                    public static class App
                    {
                        public const string StartupDebounceSkip = "startup-debounce-skip";
                        public const string StartupSyncWarning = "startup-sync-warning";
                        public const string StartupRegistrySync = "startup-registry-sync";
                        public const string StartupRegistryUpdate = "startup-registry-update";
                        public const string StartupJsonSyncSkip = "startup-json-sync-skip";
                        public const string StartupJsonSyncFailed = "startup-json-sync-failed";
                        public const string StartupJsonSyncSuccess = "startup-json-sync-success";
                        public const string AppInitFailed = "app-init-failed";
                        public const string AppInitFirstRun = "app-init-first-run";
                        public const string OutputSwitchPost = "output-switch-post";
                        public const string CleanupStart = "cleanup-started";
                        public const string CleanupComplete = "cleanup-completed";
                        public const string CleanupDisposalForced = "cleanup-disposal-forced";
                        public const string CleanupTimeout = "cleanup-timeout";
                        public const string CleanupCompleteAfterGrace = "cleanup-complete-after-grace";
                        public const string RefreshSkip = "refresh-skip";
                        public const string RefreshSettingsReloadSkip = "refresh-settings-reload-skip";
                        public const string RefreshState = "refresh-state";
                        public const string HotplugPostRefreshDeferred = "hotplug-post-refresh-deferred";
                        public const string HotplugMixerRefreshDeferred = "hotplug-mixer-refresh-deferred";
                        public const string DeviceReferenceSkip = "device-reference-skip";
                        public const string SettingsApply = "settings-apply";
                        public const string RoutineApplicationTriggerBatch = "routine-application-trigger-batch";
                        public const string RoutineApplicationLeaseRefresh = "routine-application-lease-refresh";
                        public const string SaveValidationFailed = "save-validation-failed";
                        public const string ResetSkip = "reset-skip";
                        public const string ResetStepComplete = "reset-step-complete";
                    }

                    public static class Mixer
                    {
                        public const string RefreshStart = "mixer-refresh-start";
                        public const string RefreshCancelled = "mixer-refresh-cancelled";
                        public const string RefreshSnapshot = "mixer-refresh-snapshot";
                        public const string RefreshComplete = "mixer-refresh-complete";
                        public const string RefreshSlow = "mixer-refresh-slow";
                        public const string RefreshDiagnostics = "mixer-refresh-diagnostics";
                        public const string CacheWindowDiagnostics = "mixer-cache-window-diagnostics";
                        public const string PidNameCacheTrim = "pid-name-cache-trim";
                        public const string VolumeApplyTaskFailed = "volume-apply-task-failed";
                    }
                }

                public static class Volume
                {
                    public const string SavedVolumeMatch = "saved-volume-match";
                    public const string RestoreSavedVolumeSuccess = "restore-saved-volume-success";
                    public const string RestoreSavedVolumeFailed = "restore-saved-volume-failed";
                    public const string CaptureSessionVolumesSkip = "capture-session-volumes-skip";
                    public const string CaptureSessionVolumesComplete = "capture-session-volumes-complete";
                    public const string ApplySessionVolumesSimpleSkip = "apply-session-volumes-simple-skip";
                    public const string ApplySessionVolumesSimpleScan = "apply-session-volumes-simple-scan";
                    public const string FuzzyMatchSession = "fuzzy-match-session";
                    public const string SessionVolumeApply = "session-volume-apply";
                    public const string SessionVolumeSkip = "session-volume-skip";
                    public const string ApplySessionVolumesSimpleComplete = "apply-session-volumes-simple-complete";
                    public const string MuteApply = "mute-apply";
                    public const string CleanupCacheComplete = "cleanup-cache-complete";
                    public const string FuzzyMatchRatio = "fuzzy-match-ratio";
                }

                public static class Hotkey
                {
                    public const string Unregister = "hotkeys-unregister";
                    public const string KeyboardHookUninstalled = "keyboard-hook-uninstalled";
                    public const string MouseHookUninstalled = "mouse-hook-uninstalled";
                    public const string Execute = "hotkey-execute";
                    public const string DispatchLatency = "hotkey-dispatch-latency";
                    public const string DispatchDiagnostics = "hotkey-dispatch-diagnostics";
                }

                public static class StartupCoordinator
                {
                    public const string Start = "startup-initialize-started";
                    public const string Complete = "startup-initialize-completed";
                    public const string SettingsUnavailable = "startup-settings-unavailable";
                    public const string SettingsWarnings = "startup-settings-warnings";
                    public const string HotkeysRegisterProcessed = "startup-hotkeys-register-processed";
                    public const string HotkeysRegisterFailed = "startup-hotkeys-register-failed";
                }

                public static class SingleInstance
                {
                    public const string ActivationSignalIgnored = "activation-signal-ignored";
                    public const string ActivationHandoff = "activation-handoff";
                    public const string ForwardStart = "forward-start";
                    public const string ForwardConnectFailed = "forward-connect-failed";
                    public const string ForwardResponseInvalid = "forward-response-invalid";
                    public const string ForwardResponseReceived = "forward-response-received";
                    public const string ForwardProtocolMismatch = "forward-protocol-mismatch";
                    public const string ExistingInstanceUnresponsive = "existing-instance-unresponsive";
                    public const string RecoveryRetry = "single-instance-recovery-retry";
                    public const string RecoveryTerminateStart = "single-instance-recovery-terminate-start";
                    public const string RecoveryTerminateSuccess = "single-instance-recovery-terminate-success";
                    public const string RecoveryTerminateFailed = "single-instance-recovery-terminate-failed";
                    public const string RecoveryReacquireFailed = "single-instance-recovery-reacquire-failed";
                }

                public static class Diagnostics
                {
                    public const string HotplugDiagnostics = "hotplug-diagnostics";
                    public const string MixerRefreshCoordination = "mixer-refresh-coordination";
                    public const string OverlayEmojiRenderDiagnostics = "overlay-emoji-render-diagnostics";
                    public const string ProcessLifecycleMonitorFallback = "process-lifecycle-monitor-fallback";
                    public const string SwitchSpamGuardDiagnostics = "switch-spam-guard-diagnostics";
                    public const string SessionSnapshotDiagnostics = "session-snapshot-diagnostics";
                    public const string SessionSnapshotSlow = "session-snapshot-slow";
                    public const string SessionSnapshotFastPathHit = "session-snapshot-fast-path-hit";
                }

                public static class BluetoothReconnect
                {
                    public const string Start = "bluetooth-reconnect-start";
                    public const string Skip = "bluetooth-reconnect-skip";
                    public const string Attempt = "bluetooth-reconnect-attempt";
                    public const string Source = "bluetooth-reconnect-source";
                    public const string Candidates = "bluetooth-reconnect-candidates";
                    public const string Match = "bluetooth-reconnect-match";
                    public const string PairResult = "bluetooth-reconnect-pair-result";
                    public const string FallbackStart = "bluetooth-reconnect-fallback-start";
                    public const string FallbackResult = "bluetooth-reconnect-fallback-result";
                    public const string Recheck = "bluetooth-reconnect-recheck";
                    public const string Phases = "bluetooth-reconnect-phases";
                    public const string Success = "bluetooth-reconnect-success";
                    public const string Timeout = "bluetooth-reconnect-timeout";
                    public const string CircuitOpen = "bluetooth-reconnect-circuit-open";
                    public const string Failed = "bluetooth-reconnect-failed";
                    public const string Summary = "bluetooth-reconnect-summary";
                }

                public static class Settings
                {
                    public const string InitPaths = "init-paths";
                    public const string LoadStart = "load-start";
                    public const string LoadPath = "load-path";
                    public const string LoadDefaults = "load-defaults";
                    public const string LoadDefaultsPath = "load-defaults-path";
                    public const string UpgradeSchema = "upgrade-schema";
                    public const string SaveOnLoad = "save-on-load";
                    public const string LoadSuccess = "load-success";
                    public const string SaveStart = "save-start";
                    public const string SaveTargetPath = "save-target-path";
                    public const string SaveSuccess = "save-success";
                    public const string SaveSuccessPath = "save-success-path";
                    public const string ExistsCheck = "exists-check";
                    public const string GenerateDeviceReferenceSuccess = "generate-device-reference-success";
                    public const string GenerateDeviceReferenceFailed = "generate-device-reference-failed";
                    public const string RecoveredFromBackup = "Recovered settings from backup";
                    public const string SaveSkipped = "save-skipped";
                    public const string SettingsLockAbandoned = "settings-lock-abandoned";
                    public const string SettingsLockTimeout = "settings-lock-timeout";
                }

                public static class Overlay
                {
                    public const string Position = "overlay-position";
                }

                public static class Startup
                {
                    public const string AddStartupPath = "add-startup-path";
                    public const string AddStartupSuccess = "add-startup-success";
                    public const string AddStartupValue = "add-startup-value";
                    public const string AddStartupUpdateValues = "add-startup-update-values";
                    public const string AddStartupSkip = "add-startup-skip";
                    public const string RemoveStartupSkip = "remove-startup-skip";
                    public const string IsInStartup = "is-in-startup";
                    public const string IsInStartupValidPath = "is-in-startup-valid-path";
                    public const string IsInStartupValidPathValues = "is-in-startup-valid-path-values";
                    public const string ValidateStartupPathValues = "validate-startup-path-values";
                    public const string ValidateStartupPathSkip = "validate-startup-path-skip";
                    public const string RemoveIfPresent = "remove-if-present";
                }

                public static class DeviceCache
                {
                    public const string AccessDiagnostics = "device-cache-access-diagnostics";
                    public const string CacheInit = "cache-init";
                    public const string BackgroundRefreshFailed = "background-refresh-failed";
                    public const string MaterializeDeviceFailed = "materialize-device-failed";
                    public const string CacheRefreshStart = "cache-refresh-start";
                    public const string CacheRefreshRequested = "cache-refresh-requested";
                    public const string CacheRefreshAwaitExisting = "cache-refresh-await-existing";
                    public const string GetPlaybackFallback = "get-playback-fallback";
                    public const string GetPlaybackCacheHit = "get-playback-cache-hit";
                    public const string GetRecordingFallback = "get-recording-fallback";
                    public const string GetRecordingCacheHit = "get-recording-cache-hit";
                    public const string CacheRefreshFetchFailed = "cache-refresh-fetch-failed";
                    public const string CacheRefreshComplete = "cache-refresh-complete";
                    public const string CacheRefreshSkippedUnchanged = "cache-refresh-skipped-unchanged";
                    public const string DisposeArrayEntryFailed = "dispose-array-entry-failed";
                    public const string CacheInvalidated = "cache-invalidated";
                    public const string CacheRefreshTaskFaulted = "cache-refresh-task-faulted";
                }
            }

            public static class WindowTitleHeuristics
            {
                public const int MinHexLength = 5;
                public const int MaxHexCount = 6;
                public const int MinDashLength = 30;
                public const int MinDashCount = 4;
            }

            public static readonly string[] SuffixesToRemove =
            [
                "-Win64-Shipping",
                "-Win32-Shipping",
                "-Shipping",
                "-x64",
                "_x64",
                "-x86",
                "_x86",
                "-amd64",
                "-arm64",
                "-Retail",
                "-Release",
                "-Debug",
                "-Test",
                "-Alpha",
                "-Beta",
                "-RC",
                "-Demo",
                "-Trial",
                ".Root",
                ".App",
                ".Helper",
                ".Client",
                ".Launcher",
                ".Game",
                ".exe",
                "_Data",
                "-main",
                "-node",
            ];

            public static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
            {
                "audiopilot",
                "audiopilot.clihost",
                "audiodg",
                "svchost",
                "system",
                "system32",
                "services",
                "dwm",
                "explorer",
                "searchapp",
                "startmenuexperiencehost",
                "shellexperiencehost",
                "textinputhost",
                "windowsinternal",
                "gameinputsvc",
                "widgetservice",
                "widgetsservice",
                "smartscreen",
                "backgroundtransferhost",
                "appinstaller",
                "searchindexer",
                "searchui",
                "runtimebroker",
                "wininit",
                "lsass",
                "csrss",
                "winlogon",
                "fontdrvhost",
                "conhost",
                "sched",
                "ctfmon",
                "sihost",
                "taskhostw",
                "applicationframehost",
                "systemsettings",
                "lockapp",
                "securityhealthservice",
                "securityhealthsystray",
                "wmiprvse",
                "dllhost",
                "spoolsv",
                "msiexec",
                "consent",
                "dashost",
                "gamebarpresencewriter",
                "gamebar",
                "phoneexperiencehost",
                "yourphone",
                "windowsterminal",
            };

            public static readonly HashSet<string> KnownHelperProcesses = new(StringComparer.OrdinalIgnoreCase)
            {
                "msedgewebview2",
                "chrome-gpu-process",
                "chrome-renderer",
                "chrome-utility-process",
                "chrome-plugin-host",
                "plugin-container",
                "firefox-bin",
                "brave-gpu-process",
                "brave-renderer",
                "opera-gpu-process",
                "opera-renderer",
                "vivaldi-gpu-process",
                "vivaldi-renderer",
                "arc-gpu-process",
                "arc-renderer",
                "msedge-gpu-process",
                "msedge-renderer",
                "gpu-process",
                "renderer",
                "utility",
                "cefsharp.browsersubprocess",
                "cef",
                "helper",
                "webhelper",
                "crashhandler",
                "updater",
            };

            public static readonly string[] InternalWindowPrefixes =
            [
                ".NET-BroadcastEventWindow",
                "HwndWrapper",
                "WPF",
                "MSCTFIME UI",
                "Default IME",
                "IME",
                "CiceroUIWndFrame",
                "OleMainThreadWndName",
                "DDEServerWindow",
                "DDE Server Window",
                "GDI+ Window",
                "GDI+ Hook Window",
                "SystemResourceNotifyWindow",
                "SystemTray",
                "NotifyIcon",
                "Shell_TrayWnd",
                "DesktopWindowXamlSource",
                "MediaContextNotificationWindow",
                "Chrome_WidgetWin_0",
                "Intermediate D3D Window",
            ];
        }
    }
}

using AudioPilot.Constants;

namespace AudioPilot.Tests;

public sealed class AppConstantsPolicyTests
{
    [Fact]
    public void RecoveryAndBackgroundWorkDefaults_RemainResponsiveAndBounded()
    {
        Assert.InRange(AppConstants.Timing.CircuitBreakerCooldownSeconds, 15, 60);
        Assert.InRange(AppConstants.Limits.MaxConcurrentBackgroundTasks, 8, 64);
        Assert.InRange(AppConstants.Limits.MaxDeferredBackgroundOperations, AppConstants.Limits.MaxConcurrentBackgroundTasks, 256);
    }

    [Fact]
    public void SnapshotAndHotplugWindows_AreOrderedByFreshnessAndUrgency()
    {
        Assert.True(AppConstants.Timing.SessionSnapshotFastPathCacheInteractiveMs
            <= AppConstants.Timing.SessionSnapshotFastPathCacheMs);
        Assert.True(AppConstants.Timing.SessionSnapshotFastPathCacheMs
            <= AppConstants.Timing.SessionSnapshotFastPathCacheBackgroundMs);
        Assert.True(AppConstants.Timing.SessionSnapshotFastPathCacheBackgroundMs
            < AppConstants.Timing.SessionSnapshotPrewarmReuseMs);
        Assert.True(AppConstants.Timing.HotplugRefreshFastPathDebounceMs
            < AppConstants.Timing.HotplugRefreshDebounceMs);
    }

    [Fact]
    public void RuntimeTuningDefaults_StayWithinSupportedInputBounds()
    {
        Assert.InRange(
            AppConstants.Bluetooth.ReconnectMaxAttemptsDefault,
            AppConstants.Limits.BluetoothReconnectMinAttempts,
            AppConstants.Limits.BluetoothReconnectMaxAttempts);
        Assert.InRange(
            AppConstants.Timing.BluetoothReconnectAttemptTimeoutMs,
            AppConstants.Limits.BluetoothReconnectMinAttemptTimeoutMs,
            AppConstants.Limits.BluetoothReconnectMaxAttemptTimeoutMs);
        Assert.InRange(
            AppConstants.Timing.BluetoothReconnectCooldownMs,
            AppConstants.Limits.BluetoothReconnectMinCooldownMs,
            AppConstants.Limits.BluetoothReconnectMaxCooldownMs);
        Assert.InRange(
            AppConstants.Routines.SteamBigPictureMonitorDebounceMs,
            AppConstants.Limits.SteamBigPictureMonitorDebounceMinMs,
            AppConstants.Limits.SteamBigPictureMonitorDebounceMaxMs);
        Assert.InRange(
            AppConstants.Routines.SteamBigPictureConfirmationDelayMs,
            AppConstants.Limits.SteamBigPictureConfirmationDelayMinMs,
            AppConstants.Limits.SteamBigPictureConfirmationDelayMaxMs);
    }

    [Fact]
    public void MediaRecoveryAndTelemetryDefaults_HaveSafeRelationships()
    {
        Assert.True(AppConstants.MediaOverlay.CaptureCancellationGraceMs
            < AppConstants.MediaOverlay.MaxCaptureDurationMs);
        Assert.True(AppConstants.MediaOverlay.TimelineResetToSeconds
            < AppConstants.MediaOverlay.TimelineResetFromSeconds);
        Assert.True(AppConstants.MediaOverlay.TimelineResetFromSeconds
            < AppConstants.MediaOverlay.TimelineJumpThresholdSeconds);
        Assert.True(AppConstants.MediaOverlay.TelemetryFlushEveryEvents
            <= AppConstants.Logging.MaxBatchSize);
    }

    [Fact]
    public void LoggingAndHotkeyLimits_RemainInternallyConsistent()
    {
        Assert.True(AppConstants.Logging.MaxBatchSize < AppConstants.Logging.MaxQueueSize);
        Assert.True(AppConstants.Logging.LogResetIntervalDays < AppConstants.Logging.LogBackupMaxAgeDays);
        Assert.True(AppConstants.Timing.HotkeyDebounceRetentionTicks > AppConstants.Timing.HotkeyDebounceTicks);
        Assert.True(AppConstants.Hotkeys.RoutineHotkeyIdBase
            > AppConstants.Hotkeys.MicVolumeDownHotkeyId);
        Assert.True(AppConstants.Hotkeys.RoutineHotkeyIdBase + AppConstants.Hotkeys.RoutineHotkeyIdMaxCount
            <= ushort.MaxValue);
    }
}

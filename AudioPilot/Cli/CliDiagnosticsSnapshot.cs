using AudioPilot.Logging;

namespace AudioPilot.Cli;

/// <summary>Raw diagnostic values; the formatter applies path privacy before display or export.</summary>
internal sealed record CliDiagnosticsSnapshot(
    string LogFilePath,
    bool LogFileExists,
    long LogFileBytes,
    string LogBackupDirectory,
    IReadOnlyList<string> LogBackupFiles,
    int LogBackupRetentionCount,
    int LogBackupMaxAgeDays,
    string SettingsPath,
    string SettingsBackupDirectory,
    IReadOnlyList<string> SettingsBackupFiles,
    int SettingsBackupRetentionCount,
    bool PathsRedacted = false,
    CliBluetoothReconnectSnapshot? BluetoothReconnect = null,
    CliRuntimeTuningSnapshot? RuntimeTuning = null,
    RuntimeProcessSnapshot? Process = null,
    RuntimeHealthSnapshot? RuntimeHealth = null);

internal sealed record CliBluetoothReconnectSnapshot(
    bool Enabled,
    int MaxAttempts,
    int AttemptTimeoutMs,
    int CooldownMs,
    bool OnlyLikelyBluetoothEndpoints)
{
    public static CliBluetoothReconnectSnapshot Capture(bool enabled) => new(
        enabled,
        BluetoothReconnectRuntimeConfig.MaxAttempts,
        BluetoothReconnectRuntimeConfig.AttemptTimeoutMs,
        BluetoothReconnectRuntimeConfig.CooldownMs,
        BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints);
}

internal sealed record CliRuntimeTuningSnapshot(
    int OutputSwitchDebounceMs,
    int InputSwitchDebounceMs,
    int SwitchRetryDelayMs,
    int SwitchRetryMaxDelayMs,
    int SwitchMaxRetries,
    int HotplugRefreshDebounceMs,
    int HotplugConnectedOverlaySuppressAfterSwitchMs,
    int MixerSessionRefreshDebounceMs,
    int MixerSnapshotCacheInteractiveMs,
    int MixerSnapshotCacheBackgroundMs,
    int ResumeHotkeyRetryDelayMs,
    int MixerDiagnosticsSummaryWindowSeconds,
    int MixerCacheWindowDiagnosticsLogEveryNRefreshes,
    int BluetoothReconnectSuccessObservedRecheckIntervalMs,
    int BluetoothReconnectTimeoutCircuitThreshold,
    int BluetoothReconnectTimeoutCircuitOpenMs)
{
    public static CliRuntimeTuningSnapshot Capture() => new(
        RuntimeTuningConfig.OutputSwitchDebounceMs,
        RuntimeTuningConfig.InputSwitchDebounceMs,
        RuntimeTuningConfig.SwitchRetryDelayMs,
        RuntimeTuningConfig.SwitchRetryMaxDelayMs,
        RuntimeTuningConfig.SwitchMaxRetries,
        RuntimeTuningConfig.HotplugRefreshDebounceMs,
        RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs,
        RuntimeTuningConfig.MixerSessionRefreshDebounceMs,
        RuntimeTuningConfig.MixerSnapshotCacheInteractiveMs,
        RuntimeTuningConfig.MixerSnapshotCacheBackgroundMs,
        RuntimeTuningConfig.ResumeHotkeyRetryDelayMs,
        RuntimeTuningConfig.MixerDiagnosticsSummaryWindowSeconds,
        RuntimeTuningConfig.MixerCacheWindowDiagnosticsLogEveryNRefreshes,
        RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
        RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold,
        RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs);
}

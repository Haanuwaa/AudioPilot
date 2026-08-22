using AudioPilot.Cli;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeRuntime : ICliCommandRuntime
{
    public bool SwitchOutputResult { get; set; } = true;
    public bool SwitchInputResult { get; set; } = true;
    public bool StartupEnableResult { get; set; } = true;
    public bool StartupDisableResult { get; set; } = true;
    public bool StartupOpenResult { get; set; } = true;
    public string StartupStatusText { get; set; } = "enabled";
    public string MediaStatusText { get; set; } = "media-status";
    public Func<Task<string>>? MediaStatusAsyncOverride { get; set; }
    public Func<Task<string>>? NetworkListAsyncOverride { get; set; }
    public string StatusText { get; set; } = "status";
    public string DiagnosticsStatusText { get; set; } = "diagnostics";
    public string DiagnosticsHistoryText { get; set; } = "history";
    public (bool Found, string Output) DiagnosticsHistoryDetailResult { get; set; } = (true, "history-detail");
    public bool LastDiagnosticsShowPaths { get; private set; }
    public int? LastDiagnosticsLimit { get; private set; }
    public string? LastDiagnosticsType { get; private set; }
    public string? LastDiagnosticsHistoryOpId { get; private set; }
    public bool LastRedactOutput { get; private set; }
    public string DeviceListText { get; set; } = "devices";
    public (bool Found, string Output) DeviceGetResult { get; set; } = (true, "device");
    public (bool Found, string Output) DeviceFindResult { get; set; } = (true, "device-find");
    public string CycleText { get; set; } = "cycle";
    public (bool IsValid, string Output) ValidationResult { get; set; } = (true, "valid");
    public (bool CanSwitch, string Output) CycleTestResult { get; set; } = (true, "ok");
    public (bool Success, string Output) CycleMutationResult { get; set; } = (true, "cycle-updated");
    public bool ToggleMuteMicResult { get; set; } = true;
    public bool SetMuteMicResult { get; set; } = true;
    public bool ToggleMuteSoundResult { get; set; } = true;
    public bool SetMuteSoundResult { get; set; } = true;
    public bool ToggleDeafenResult { get; set; } = true;
    public bool SetDeafenResult { get; set; } = true;
    public bool ToggleListenResult { get; set; } = true;
    public bool SetListenResult { get; set; } = true;
    public string MuteStatusText { get; set; } = "mute-status";
    public string ListenStatusText { get; set; } = "listen-status";
    public (bool Success, string Output) VolumeResult { get; set; } = (true, "volume-ok");
    public string? LastVolumeDeviceId { get; private set; }
    public float? LastVolumePercent { get; private set; }
    public string RoutineListText { get; set; } = "routines";
    public CliExecutionResult RunRoutineResult { get; set; } = new(0, "routine-run");
    public CliExecutionResult SetRoutineEnabledResult { get; set; } = new(0, "routine-updated");
    public CliExecutionResult CreateRoutineResult { get; set; } = new(0, "routine-created");
    public CliExecutionResult UpdateRoutineResult { get; set; } = new(0, "routine-updated");
    public CliExecutionResult DeleteRoutineResult { get; set; } = new(0, "routine-deleted");
    public CliExecutionResult ImportRoutinesResult { get; set; } = new(0, "routine-imported");
    public string? LastRoutineSelector { get; private set; }
    public string? LastRoutinePath { get; private set; }
    public bool LastRoutineEnabledValue { get; private set; }
    public bool LastRoutineAllowAnyPath { get; private set; }
    public bool LastReplaceImport { get; private set; }
    public (bool Found, string? Value, string? Error) ConfigGetResult { get; set; } = (true, "value", null);
    public (bool Updated, string? Error) ConfigSetResult { get; set; } = (true, null);
    public (bool Found, string? Value, string? Error) RuntimeGetResult { get; set; } = (true, "value", null);
    public (bool Updated, string? Error) RuntimeSetResult { get; set; } = (true, null);
    public (bool IsValid, string Output) ConfigValidationResult { get; set; } = (true, "configuration is valid.");
    public (bool CanSwitch, string Output) PreviewResult { get; set; } = (true, "preview");
    public string? CurrentOutputDeviceId { get; set; } = "out-1";
    public string? CurrentInputDeviceId { get; set; } = "in-1";
    public (bool Found, string Output) WaitResult { get; set; } = (false, "timeout");
    public (bool Success, string Output) LogExportResult { get; set; } = (true, "logs-exported");
    public (bool Success, string Output) BundleExportResult { get; set; } = (true, "bundle-exported");
    public Func<Task<(bool Success, string Output)>>? BundleExportAsyncOverride { get; set; }
    public (bool Success, string Output) ResetPerAppAudioResult { get; set; } = (true, "[diag-code:diagnostics-reset-per-app-audio-success] Per-application audio assignments were reset.");
    public (bool Success, string Output) ExportResult { get; set; } = (true, "exported");
    public (bool Success, string Output) ImportResult { get; set; } = (true, "imported");
    public (bool Success, string Output) RoutineExportResult { get; set; } = (true, "routines-exported");
    public bool LastExportAllowAnyPath { get; private set; }
    public string? LastExportPath { get; private set; }
    public CliDiagnosticsExportDetailLevel LastExportDetailLevel { get; private set; } = CliDiagnosticsExportDetailLevel.Summary;
    public bool LastIncludeSensitive { get; private set; }
    public bool RefreshCalled { get; private set; }
    public Exception? RefreshException { get; set; }

    public void ShowWindow() { }
    public void HideWindow() { }
    public void MediaPlayPause() { }
    public void MediaNextTrack() { }
    public void MediaPreviousTrack() { }
    public Task<string> GetMediaStatusAsync(bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return MediaStatusAsyncOverride?.Invoke() ?? Task.FromResult(MediaStatusText);
    }
    public bool ToggleMuteMic() => ToggleMuteMicResult;
    public bool SetMuteMic(bool enabled) => SetMuteMicResult;
    public bool ToggleMuteSound() => ToggleMuteSoundResult;
    public bool SetMuteSound(bool enabled) => SetMuteSoundResult;
    public bool ToggleDeafen() => ToggleDeafenResult;
    public bool SetDeafen(bool enabled) => SetDeafenResult;
    public bool ToggleListenToInput() => ToggleListenResult;
    public bool SetListenToInput(bool enabled) => SetListenResult;
    public string GetMuteStatus(string target, bool jsonOutput) => MuteStatusText;
    public string GetListenStatus(bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return ListenStatusText;
    }
    public (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput, bool redactOutput = false)
    {
        LastVolumeDeviceId = deviceId;
        LastRedactOutput = redactOutput;
        return VolumeResult;
    }
    public (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput, bool redactOutput = false)
    {
        LastVolumeDeviceId = deviceId;
        LastRedactOutput = redactOutput;
        LastVolumePercent = percent;
        return VolumeResult;
    }
    public string GetRoutineList(bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return RoutineListText;
    }
    public Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastRoutineSelector = routineSelector;
        return Task.FromResult(RunRoutineResult);
    }
    public CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastRoutineSelector = routineSelector;
        LastRoutineEnabledValue = enabled;
        return SetRoutineEnabledResult;
    }
    public CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastRoutinePath = path;
        LastRoutineAllowAnyPath = allowAnyPath;
        return CreateRoutineResult;
    }
    public CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastRoutineSelector = routineSelector;
        LastRoutinePath = path;
        LastRoutineAllowAnyPath = allowAnyPath;
        return UpdateRoutineResult;
    }
    public CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastRoutineSelector = routineSelector;
        return DeleteRoutineResult;
    }
    public CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastRoutinePath = path;
        LastReplaceImport = replaceImport;
        LastRoutineAllowAnyPath = allowAnyPath;
        return ImportRoutinesResult;
    }
    public ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse) => ValueTask.FromResult(SwitchOutputResult);
    public ValueTask<bool> SwitchInputAsync(bool reverse) => ValueTask.FromResult(SwitchInputResult);
    public Task RefreshAsync()
    {
        RefreshCalled = true;
        if (RefreshException != null)
        {
            return Task.FromException(RefreshException);
        }

        return Task.CompletedTask;
    }
    public bool SetStartupEnabled(bool enabled) => enabled ? StartupEnableResult : StartupDisableResult;
    public bool OpenStartupSettings() => StartupOpenResult;
    public string GetStartupStatus(bool jsonOutput) => StartupStatusText;
    public string GetStatus(bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return StatusText;
    }
    public string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput)
    {
        LastDiagnosticsShowPaths = showPaths;
        LastRedactOutput = redactOutput;
        return DiagnosticsStatusText;
    }
    public string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput)
    {
        LastDiagnosticsLimit = limit;
        LastDiagnosticsType = type;
        LastRedactOutput = redactOutput;
        return DiagnosticsHistoryText;
    }
    public (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput)
    {
        LastDiagnosticsHistoryOpId = opId;
        LastRedactOutput = redactOutput;
        return DiagnosticsHistoryDetailResult;
    }
    public string GetDeviceList(bool output, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return DeviceListText;
    }
    public (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return DeviceGetResult;
    }
    public (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return DeviceFindResult;
    }
    public string GetCycle(bool output, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return CycleText;
    }
    public (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return ValidationResult;
    }
    public (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return CycleTestResult;
    }
    public (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return CycleMutationResult;
    }
    public (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return CycleMutationResult;
    }
    public (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return CycleMutationResult;
    }
    public (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return PreviewResult;
    }
    public string? GetCurrentDeviceId(bool output) => output ? CurrentOutputDeviceId : CurrentInputDeviceId;
    public Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return Task.FromResult(WaitResult);
    }
    public (bool Found, string? Value, string? Error) GetConfig(string key) => ConfigGetResult;
    public string GetConfigList(bool jsonOutput) => CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), jsonOutput);
    public (bool Updated, string? Error) SetConfig(string key, string value) => ConfigSetResult;
    public (bool Found, string? Value, string? Error) GetRuntime(string key) => RuntimeGetResult;
    public string GetRuntimeList(bool jsonOutput) => CliOutputFormatter.FormatSupportedKeyList("runtime", CliRuntimeManager.GetKnownKeys(), jsonOutput);
    public (bool Updated, string? Error) SetRuntime(string key, string value) => RuntimeSetResult;
    public (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput) => ConfigValidationResult;
    public (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastExportAllowAnyPath = allowAnyPath;
        LastExportPath = path;
        LastExportDetailLevel = detailLevel;
        return LogExportResult;
    }
    public Task<(bool Success, string Output)> ExportDiagnosticBundleAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput)
    {
        LastExportAllowAnyPath = allowAnyPath;
        LastExportPath = path;
        LastExportDetailLevel = detailLevel;
        LastIncludeSensitive = includeSensitive;
        return BundleExportAsyncOverride?.Invoke() ?? Task.FromResult(BundleExportResult);
    }
    public (bool Success, string Output) ResetPerAppAudioRouting(bool jsonOutput) => ResetPerAppAudioResult;
    public (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastExportPath = path;
        LastExportAllowAnyPath = allowAnyPath;
        return RoutineExportResult;
    }
    public (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        LastExportPath = path;
        LastExportAllowAnyPath = allowAnyPath;
        return ExportResult;
    }
    public (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput)
    {
        LastRedactOutput = redactOutput;
        return ImportResult;
    }
    public Task<string> GetNetworkListAsync(bool jsonOutput, bool redactOutput) =>
        NetworkListAsyncOverride?.Invoke()
        ?? Task.FromResult(jsonOutput ? "{\"Success\":true,\"Count\":0,\"Networks\":[]}" : "No networks detected.");
}

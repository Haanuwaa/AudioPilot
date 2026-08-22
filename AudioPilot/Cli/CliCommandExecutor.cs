namespace AudioPilot.Cli
{
    public readonly record struct CliExecutionResult(int ExitCode, string? Output = null);

    public interface ICliCommandRuntime
    {
        void ShowWindow();
        void HideWindow();
        Task<bool> ShowWindowAsync()
        {
            ShowWindow();
            return Task.FromResult(true);
        }
        Task<bool> HideWindowAsync()
        {
            HideWindow();
            return Task.FromResult(true);
        }
        void MediaPlayPause();
        void MediaNextTrack();
        void MediaPreviousTrack();
        Task<string> GetMediaStatusAsync(bool jsonOutput, bool redactOutput);
        bool ToggleMuteMic();
        bool SetMuteMic(bool enabled);
        bool ToggleMuteSound();
        bool SetMuteSound(bool enabled);
        bool ToggleDeafen();
        bool SetDeafen(bool enabled);
        bool ToggleListenToInput();
        bool SetListenToInput(bool enabled);
        string GetMuteStatus(string target, bool jsonOutput);
        string GetListenStatus(bool jsonOutput, bool redactOutput);
        (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput, bool redactOutput = false);
        (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput, bool redactOutput = false);
        string GetRoutineList(bool jsonOutput, bool redactOutput);
        Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput);
        CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput);
        CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput);
        CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        Task<CliExecutionResult> SetRoutineEnabledAsync(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput) => Task.FromResult(SetRoutineEnabled(routineSelector, enabled, jsonOutput, redactOutput));
        Task<CliExecutionResult> CreateRoutineAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => Task.FromResult(CreateRoutine(path, allowAnyPath, jsonOutput, redactOutput));
        Task<CliExecutionResult> UpdateRoutineAsync(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => Task.FromResult(UpdateRoutine(routineSelector, path, allowAnyPath, jsonOutput, redactOutput));
        Task<CliExecutionResult> DeleteRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput) => Task.FromResult(DeleteRoutine(routineSelector, jsonOutput, redactOutput));
        Task<CliExecutionResult> ImportRoutinesAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => Task.FromResult(ImportRoutines(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
        ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse);
        ValueTask<bool> SwitchInputAsync(bool reverse);
        Task RefreshAsync();
        bool SetStartupEnabled(bool enabled);
        bool OpenStartupSettings();
        string GetStartupStatus(bool jsonOutput);
        string GetStatus(bool jsonOutput, bool redactOutput);
        string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput);
        string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput);
        (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput);
        string GetDeviceList(bool output, bool jsonOutput, bool redactOutput);
        (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput);
        (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput);
        string GetCycle(bool output, bool jsonOutput, bool redactOutput);
        (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput);
        (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput);
        Task<(bool Success, string Output)> AddCycleDeviceAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput) => Task.FromResult(AddCycleDevice(output, deviceId, jsonOutput, redactOutput));
        Task<(bool Success, string Output)> RemoveCycleDeviceAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput) => Task.FromResult(RemoveCycleDevice(output, deviceId, jsonOutput, redactOutput));
        Task<(bool Success, string Output)> ReorderCycleAsync(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput) => Task.FromResult(ReorderCycle(output, deviceIds, jsonOutput, redactOutput));
        (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput);
        string? GetCurrentDeviceId(bool output);
        Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput);
        (bool Found, string? Value, string? Error) GetConfig(string key);
        string GetConfigList(bool jsonOutput);
        (bool Updated, string? Error) SetConfig(string key, string value);
        Task<(bool Updated, string? Error)> SetConfigAsync(string key, string value) => Task.FromResult(SetConfig(key, value));
        (bool Found, string? Value, string? Error) GetRuntime(string key);
        string GetRuntimeList(bool jsonOutput);
        (bool Updated, string? Error) SetRuntime(string key, string value);
        (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput);
        Task<(bool Success, string Output)> ExportDiagnosticBundleAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput);
        (bool Success, string Output) ResetPerAppAudioRouting(bool jsonOutput);
        (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput);
        Task<(bool Success, string Output)> ExportRoutinesAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => Task.FromResult(ExportRoutines(path, allowAnyPath, jsonOutput, redactOutput));
        Task<(bool Success, string Output)> ExportConfigAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => Task.FromResult(ExportConfig(path, allowAnyPath, jsonOutput, redactOutput));
        Task<(bool Success, string Output)> ImportConfigAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => Task.FromResult(ImportConfig(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
        Task<string> GetNetworkListAsync(bool jsonOutput, bool redactOutput);
    }

    public static partial class CliCommandExecutor
    {
        public static async Task<CliExecutionResult> ExecuteAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            return command.Action switch
            {
                CliAction.None => new CliExecutionResult(0),
                CliAction.Show => await runtime.ShowWindowAsync()
                                        ? new CliExecutionResult(0)
                                        : BuildExecutionFailureResult(7, "window-show-failed", "AudioPilot could not display its main window.", command.JsonOutput),
                CliAction.Hide => await runtime.HideWindowAsync()
                                        ? new CliExecutionResult(0)
                                        : BuildExecutionFailureResult(7, "window-hide-failed", "AudioPilot could not hide its main window.", command.JsonOutput),
                CliAction.MediaPlayPause or CliAction.MediaNextTrack or CliAction.MediaPreviousTrack or CliAction.MediaStatus => await ExecuteMediaCommandAsync(command, runtime),
                CliAction.MuteMicToggle or CliAction.MuteMicOn or CliAction.MuteMicOff or CliAction.MuteSoundToggle or CliAction.MuteSoundOn or CliAction.MuteSoundOff or CliAction.DeafenToggle or CliAction.DeafenOn or CliAction.DeafenOff or CliAction.ListenToggle or CliAction.ListenOn or CliAction.ListenOff or CliAction.VolumeGetMaster or CliAction.VolumeGetMic or CliAction.VolumeSetMaster or CliAction.VolumeSetMic or CliAction.SwitchOutput or CliAction.SwitchInput => await ExecuteMediaAndVolumeCommandAsync(command, runtime),
                CliAction.NetworkList => await ExecuteNetworkCommandAsync(command, runtime),
                CliAction.RoutineList or CliAction.RoutineRun or CliAction.RoutineEnable or CliAction.RoutineDisable or CliAction.RoutineCreate or CliAction.RoutineUpdate or CliAction.RoutineDelete or CliAction.RoutineImport or CliAction.RoutineExport or CliAction.DiagnosticsStatus or CliAction.DiagnosticsHistory or CliAction.DiagnosticsHistoryDetail or CliAction.DiagnosticsExportLogs or CliAction.DiagnosticsExportBundle or CliAction.DiagnosticsResetPerAppAudio => await ExecuteRoutinesAndDiagnosticsAsync(command, runtime),
                CliAction.WaitForDevice or CliAction.Refresh or CliAction.StartupEnable or CliAction.StartupDisable or CliAction.StartupStatus or CliAction.StartupOpen or CliAction.Status or CliAction.DevicesListOutput or CliAction.DevicesListInput or CliAction.DevicesGetOutput or CliAction.DevicesGetInput or CliAction.DevicesFindOutput or CliAction.DevicesFindInput or CliAction.CycleShowOutput or CliAction.CycleShowInput or CliAction.CycleValidateOutput or CliAction.CycleValidateInput or CliAction.CycleTestOutput or CliAction.CycleTestInput or CliAction.CycleAddOutput or CliAction.CycleAddInput or CliAction.CycleRemoveOutput or CliAction.CycleRemoveInput or CliAction.CycleReorderOutput or CliAction.CycleReorderInput or CliAction.ConfigGet or CliAction.ConfigList or CliAction.ConfigSet or CliAction.RuntimeGet or CliAction.RuntimeList or CliAction.RuntimeSet or CliAction.ConfigValidate or CliAction.ConfigExport or CliAction.ConfigImport => await ExecuteDevicesAndConfigAsync(command, runtime),
                _ => BuildErrorResult(2, "unsupported-command", "Unsupported command.", command.JsonOutput),
            };
        }

        public static CliExecutionResult BuildRuntimeUnavailableResult(bool jsonOutput)
        {
            return BuildErrorResult(3, "app-not-ready", "App is not ready.", jsonOutput);
        }

        public static CliExecutionResult BuildExecutionFailureResult(int exitCode, string errorCode, string message, bool jsonOutput)
        {
            return BuildErrorResult(exitCode, errorCode, message, jsonOutput);
        }

        public static string BuildJsonErrorPayload(int exitCode, string errorCode, string message)
        {
            return CliOutputFormatter.SerializeCliJson(new
            {
                Error = new
                {
                    Code = errorCode,
                    Message = message,
                    ExitCode = exitCode,
                }
            });
        }

        private static CliExecutionResult BuildErrorResult(int exitCode, string errorCode, string message, bool jsonOutput)
        {
            if (!jsonOutput)
            {
                return new CliExecutionResult(exitCode, message);
            }

            string output = BuildJsonErrorPayload(exitCode, errorCode, message);
            return new CliExecutionResult(exitCode, output);
        }

        private static IReadOnlyList<string> SplitDeviceIds(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return [];
            }

            return [.. value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static id => !string.IsNullOrWhiteSpace(id))];
        }

        private static async Task<CliExecutionResult> ExecuteNetworkCommandAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (command.Action != CliAction.NetworkList)
            {
                return BuildErrorResult(2, "unsupported-network-command", "Unsupported network command.", command.JsonOutput);
            }

            return new CliExecutionResult(
                0,
                await runtime.GetNetworkListAsync(command.JsonOutput, command.RedactOutput));
        }
    }
}

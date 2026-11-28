using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot
{
    public partial class App : Application
    {
        private readonly Lazy<IAppDialogService> _dialogService = new(static () => new AppDialogService());
        internal IAppDialogService DialogService => _dialogService.Value;
        private sealed class UiDispatcherUnavailableException(Exception? innerException = null)
            : InvalidOperationException("UI dispatcher is not available.", innerException);

        private sealed class AppViewModelCliRuntime(
            AppViewModel appVm,
            IAppMainWindowManager windowManager,
            CancellationToken cancellationToken) : ICliCommandRuntime
        {
            private readonly AppViewModel _appVm = appVm;
            private readonly IAppMainWindowManager _windowManager = windowManager;

            private static bool IsUiDispatcherUnavailable(Dispatcher? dispatcher)
            {
                return AppDispatcherHelper.IsDispatcherUnavailable(dispatcher);
            }

            private static UiDispatcherUnavailableException CreateDispatcherUnavailableException(Exception? innerException = null) =>
                new(innerException);

            private T InvokeOnUi<T>(Func<T> action) =>
                AppDispatcherHelper.InvokeOnUi(Current?.Dispatcher, action, CreateDispatcherUnavailableException, cancellationToken);

            private void InvokeOnUi(Action action) =>
                AppDispatcherHelper.InvokeOnUi(Current?.Dispatcher, action, CreateDispatcherUnavailableException, cancellationToken);

            private Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action) =>
                AppDispatcherHelper.InvokeOnUiAsync(Current?.Dispatcher, action, CreateDispatcherUnavailableException, cancellationToken);

            private Task InvokeOnUiAsync(Func<Task> action) =>
                AppDispatcherHelper.InvokeOnUiAsync(Current?.Dispatcher, action, CreateDispatcherUnavailableException, cancellationToken);

            public void ShowWindow() => InvokeOnUi(_appVm.ShowWindow);
            public void HideWindow() => InvokeOnUi(() => _windowManager.Hide());
            public Task<bool> ShowWindowAsync() => InvokeOnUiAsync(() => _appVm.ShowWindowAsync(cancellationToken));
            public Task<bool> HideWindowAsync() => InvokeOnUiAsync(() => _windowManager.HideAsync(cancellationToken));
            public async Task<CliExecutionResult> ExecuteWindowLayoutAsync(CliCommand command)
            {
                WindowLayoutSnapshot layout = command.Action == CliAction.WindowStatus
                    ? await _windowManager.GetLayoutAsync(cancellationToken)
                    : await _windowManager.ChangeLayoutAsync(command.Action == CliAction.WindowReset ? new(Reset: true) : command.WindowLayout ?? new(), cancellationToken);
                return CliOutputFormatter.FormatWindowLayout(layout, command.Action, command.JsonOutput);
            }
            public Task<CliExecutionResult> PrepareExitAsync(bool jsonOutput) => InvokeOnUiAsync(() =>
            {
                if (_appVm.IsRuntimeShutdownRequested || Current is not App app || app._runtimeHost == null
                    || !ReferenceEquals(app._runtimeHost.AppViewModel, _appVm))
                    return Task.FromResult(CliCommandExecutor.BuildRuntimeUnavailableResult(jsonOutput));
                return Task.FromResult(new CliExecutionResult(0, CliOutputFormatter.FormatExitAccepted(jsonOutput))
                {
                    AfterResponse = () => _ = app.ShutdownFromCliAsync(),
                });
            });
            public Task<CliExecutionResult> ExecuteUpdateAsync(CliCommand command) => InvokeOnUiAsync(() => Services.Updates.UpdateCommandService.ExecuteAsync(command, _appVm.Updates, cancellationToken));
            public Task<CliExecutionResult> ExecuteAppAudioAsync(CliCommand command) => CliAppAudioOperation.ExecuteAsync(command, cancellationToken);
            public Task<CliExecutionResult> ExecuteAppRoutingAsync(CliCommand command) => InvokeOnUiAsync(() => _appVm.ExecuteApplicationRoutingFromCliAsync(command, cancellationToken));
            public void MediaPlayPause() => InvokeOnUi(() => _appVm.MediaPlayPauseFromCli());
            public void MediaNextTrack() => InvokeOnUi(() => _appVm.MediaNextTrackFromCli());
            public void MediaPreviousTrack() => InvokeOnUi(() => _appVm.MediaPreviousTrackFromCli());
            public Task<MediaSeekResult> MediaSeekAsync(bool backward, decimal? seconds) => InvokeOnUiAsync(() => _appVm.MediaSeekFromCliAsync(backward, seconds, cancellationToken));
            public Task<MediaPlaybackResult> MediaSetPlayingAsync(bool playing) => _appVm.MediaSetPlayingFromCliAsync(playing, cancellationToken);
            public Task<string> GetMediaStatusAsync(bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.GetMediaStatusFromCliAsync(jsonOutput, redactOutput));
            public Task<bool> ToggleMuteMicAsync() => InvokeOnUiAsync(() => _appVm.ToggleMuteMicFromCliAsync(cancellationToken));
            public Task<bool> SetMuteMicAsync(bool enabled) => InvokeOnUiAsync(() => _appVm.SetMuteMicFromCliAsync(enabled, cancellationToken));
            public Task<bool> ToggleMuteSoundAsync() => InvokeOnUiAsync(() => _appVm.ToggleMuteSoundFromCliAsync(cancellationToken));
            public Task<bool> SetMuteSoundAsync(bool enabled) => InvokeOnUiAsync(() => _appVm.SetMuteSoundFromCliAsync(enabled, cancellationToken));
            public Task<bool> ToggleDeafenAsync() => InvokeOnUiAsync(() => _appVm.ToggleDeafenFromCliAsync(cancellationToken));
            public Task<bool> SetDeafenAsync(bool enabled) => InvokeOnUiAsync(() => _appVm.SetDeafenFromCliAsync(enabled, cancellationToken));
            public bool ToggleListenToInput()
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _appVm.ToggleListenToInputFromCli();
            }
            public bool SetListenToInput(bool enabled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _appVm.SetListenToInputFromCli(enabled);
            }
            public string GetMuteStatus(string target, bool jsonOutput) => InvokeOnUi(() => _appVm.GetMuteStatusFromCli(target, jsonOutput));
            public string GetListenStatus(bool jsonOutput, bool redactOutput)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _appVm.GetListenStatusFromCli(jsonOutput, redactOutput);
            }
            public (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput, bool redactOutput = false) => InvokeOnUi(() => _appVm.GetVolumeFromCli(playback, deviceId, jsonOutput, redactOutput));
            public (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput, bool redactOutput = false, bool relative = false) => InvokeOnUi(() => _appVm.SetVolumeFromCli(playback, deviceId, percent, jsonOutput, redactOutput, relative));
            public IReadOnlyList<AudioRoutine> GetRoutinesSnapshot() => InvokeOnUi(_appVm.GetRoutinesSnapshotForCli);
            public Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.RunRoutineFromCliAsync(routineSelector, jsonOutput, redactOutput, cancellationToken));
            public Task<CliExecutionResult> CheckRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.CheckRoutineFromCliAsync(routineSelector, jsonOutput, redactOutput, cancellationToken));
            public CliExecutionResult PauseAutomaticRoutines(int? minutes, bool jsonOutput) => InvokeOnUi(() => _appVm.SetAutomaticRoutinePauseFromCli(true, minutes, jsonOutput));
            public CliExecutionResult ResumeAutomaticRoutines(bool jsonOutput) => InvokeOnUi(() => _appVm.SetAutomaticRoutinePauseFromCli(false, null, jsonOutput));
            public CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.SetRoutineEnabledFromCli(routineSelector, enabled, jsonOutput, redactOutput));
            public CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.CreateRoutineFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.UpdateRoutineFromCli(routineSelector, path, allowAnyPath, jsonOutput, redactOutput));
            public CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.DeleteRoutineFromCli(routineSelector, jsonOutput, redactOutput));
            public CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ImportRoutinesFromCli(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
            public Task<CliExecutionResult> SetRoutineEnabledAsync(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.SetRoutineEnabledFromCliAsync(routineSelector, enabled, jsonOutput, redactOutput, cancellationToken));
            public Task<CliExecutionResult> CreateRoutineAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.CreateRoutineFromCliAsync(path, allowAnyPath, jsonOutput, redactOutput, cancellationToken));
            public Task<CliExecutionResult> UpdateRoutineAsync(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.UpdateRoutineFromCliAsync(routineSelector, path, allowAnyPath, jsonOutput, redactOutput, cancellationToken));
            public Task<CliExecutionResult> DeleteRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.DeleteRoutineFromCliAsync(routineSelector, jsonOutput, redactOutput, cancellationToken));
            public Task<CliExecutionResult> ImportRoutinesAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ImportRoutinesFromCliAsync(path, replaceImport, allowAnyPath, jsonOutput, redactOutput, cancellationToken));
            public async ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse) => await InvokeOnUiAsync(async () => await _appVm.SwitchOutputFromCliAsync(muteMic, muteSound, deafen, reverse));
            public async ValueTask<bool> SwitchInputAsync(bool reverse) => await InvokeOnUiAsync(async () => await _appVm.SwitchInputFromCliAsync(reverse));
            public Task<CliExecutionResult> SwitchToDeviceAsync(CliCommand command) => InvokeOnUiAsync(() => _appVm.SwitchToDeviceFromCliAsync(command));
            public Task RefreshAsync() => InvokeOnUiAsync(_appVm.RefreshFromCliAsync);
            public Task<bool> SetStartupEnabledAsync(bool enabled) => InvokeOnUiAsync(() => _appVm.SetStartupEnabledFromCliAsync(enabled, cancellationToken));
            public bool OpenStartupSettings() => InvokeOnUi(_appVm.OpenStartupSettingsFromCli);
            public string GetStartupStatus(bool jsonOutput) => InvokeOnUi(() => _appVm.GetStartupStatusFromCli(jsonOutput));
            public string GetStatus(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetStatusFromCli(jsonOutput, redactOutput));
            public string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsStatusFromCli(jsonOutput, showPaths, redactOutput));
            public Task<(bool Success, string Output)> TestProcessMonitorAsync(bool jsonOutput) => _appVm.TestProcessMonitorFromCliAsync(jsonOutput, cancellationToken);
            public string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsHistoryFromCli(jsonOutput, limit, type, redactOutput));
            public (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsHistoryDetailFromCli(opId, jsonOutput, redactOutput));
            public (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportLogsFromCli(path, allowAnyPath, detailLevel, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> ExportLogsAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput) =>
                InvokeOnUiAsync(() => _appVm.ExportLogsFromCliAsync(path, allowAnyPath, detailLevel, jsonOutput, redactOutput, cancellationToken));
            public Task<(bool Success, string Output)> ExportDiagnosticBundleAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput) =>
                InvokeOnUiAsync(() => _appVm.ExportDiagnosticBundleFromCliAsync(path, allowAnyPath, detailLevel, includeSensitive, jsonOutput, cancellationToken));
            public Task<(bool Success, string Output)> ResetPerAppAudioRoutingAsync(bool jsonOutput) =>
                InvokeOnUiAsync(() => _appVm.ResetPerAppAudioRoutingFromCliAsync(jsonOutput, cancellationToken));
            public string GetDeviceList(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDeviceListFromCli(output, jsonOutput, redactOutput));
            public (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDeviceFromCli(output, selector, jsonOutput, redactOutput));
            public (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.FindDevicesFromCli(output, query, jsonOutput, redactOutput));
            public string GetCycle(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleFromCli(output, jsonOutput, redactOutput));
            public (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleValidationFromCli(output, jsonOutput, redactOutput));
            public (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleTestFromCli(output, jsonOutput, redactOutput));
            public (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.AddCycleDeviceFromCli(output, deviceId, jsonOutput, redactOutput));
            public (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.RemoveCycleDeviceFromCli(output, deviceId, jsonOutput, redactOutput));
            public (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ReorderCycleFromCli(output, deviceIds, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> AddCycleDeviceAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.AddCycleDeviceFromCliAsync(output, deviceId, jsonOutput, redactOutput, cancellationToken));
            public Task<(bool Success, string Output)> RemoveCycleDeviceAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.RemoveCycleDeviceFromCliAsync(output, deviceId, jsonOutput, redactOutput, cancellationToken));
            public Task<(bool Success, string Output)> ReorderCycleAsync(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ReorderCycleFromCliAsync(output, deviceIds, jsonOutput, redactOutput, cancellationToken));
            public (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.PreviewSwitchFromCli(output, reverse, jsonOutput, redactOutput));
            public string? GetCurrentDeviceId(bool output) => InvokeOnUi(() => _appVm.GetCurrentDeviceIdFromCli(output));
            public Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput) =>
                InvokeOnUiAsync(() => _appVm.WaitForDeviceFromCliAsync(deviceId, timeoutMs, outputOnly, inputOnly, jsonOutput, redactOutput, cancellationToken));
            public (bool Found, string? Value, string? Error) GetConfig(string key) => InvokeOnUi(() => _appVm.GetConfigFromCli(key));
            public string GetConfigList(bool jsonOutput) => InvokeOnUi(() => CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), jsonOutput));
            public (bool Updated, string? Error) SetConfig(string key, string value) => InvokeOnUi(() => _appVm.SetConfigFromCli(key, value));
            public Task<(bool Updated, string? Error)> SetConfigAsync(string key, string value) => InvokeOnUiAsync(() => _appVm.SetConfigFromCliAsync(key, value, cancellationToken));
            public (bool Found, string? Value, string? Error) GetRuntime(string key) => InvokeOnUi(() => AppViewModel.GetRuntimeFromCli(key));
            public string GetRuntimeList(bool jsonOutput) => InvokeOnUi(() => CliOutputFormatter.FormatSupportedKeyList("runtime", CliRuntimeManager.GetKnownKeys(), jsonOutput));
            public (bool Updated, string? Error) SetRuntime(string key, string value) => InvokeOnUi(() => AppViewModel.SetRuntimeFromCli(key, value));
            public Task<CliExecutionResult> ValidateConfigFileAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) =>
                InvokeOnUiAsync(() => _appVm.ValidateConfigFileFromCliAsync(path, allowAnyPath, jsonOutput, redactOutput, cancellationToken));
            public (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetConfigValidationFromCli(jsonOutput, redactOutput));
            public (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportRoutinesFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportConfigFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ImportConfigFromCli(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> ExportRoutinesAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ExportRoutinesFromCliAsync(path, allowAnyPath, jsonOutput, redactOutput, cancellationToken));
            public Task<(bool Success, string Output)> ExportConfigAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ExportConfigFromCliAsync(path, allowAnyPath, jsonOutput, redactOutput, cancellationToken));
            public Task<(bool Success, string Output)> ImportConfigAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ImportConfigFromCliAsync(path, replaceImport, allowAnyPath, jsonOutput, redactOutput, cancellationToken));
            public Task<(bool Success, string Output)> PreviewConfigImportAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) =>
                _appVm.PreviewConfigImportFromCliAsync(path, replaceImport, allowAnyPath, jsonOutput, redactOutput, cancellationToken);
            public Task<string> GetNetworkListAsync(bool jsonOutput, bool redactOutput)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _appVm.GetNetworkListFromCliAsync(jsonOutput, redactOutput);
            }
        }

        private AppRuntimeHost? _runtimeHost;
        private Logger _logger = null!;
        private readonly TaskCompletionSource<bool> _runtimeReadyForActivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _unobservedTaskExceptionHandlerRegistered;
        private SingleInstanceHelper? _singleInstanceWithLifetimeHandlers;
        private SessionEndingNotifications? _sessionEndingNotifications;
        private volatile bool _sessionEnding;
        private int _sessionEndingStarted;
        private readonly CancellationTokenSource _startupCancellation = new();
        private Task? _updateLifecycleTask;
        private Task? _updateAttachmentTask;

        internal static bool IsSessionEnding => Current is App { _sessionEnding: true } || SessionEndingState.IsEnding;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AttachUnobservedTaskExceptionHandler();
            try
            {
                _sessionEndingNotifications = new SessionEndingNotifications(OnEndSessionNotification, OnExternalCloseRequested);
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to register session-ending listener", nameof(OnStartup), ex);
            }
            _ = RunStartupObservedAsync(e.Args);
        }

        private async Task RunStartupObservedAsync(string[] args)
        {
            try
            {
                Services.Updates.AppUpdateLifecycleSession.InitializeFromEnvironment();
                _logger = Logger.Instance;
                await RunStartupAsync(args);
            }
            catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested
                || _runtimeHost?.AppViewModel.IsRuntimeShutdownRequested == true)
            {
                _runtimeReadyForActivation.TrySetResult(false);
            }
            catch (Exception ex)
            {
                _runtimeReadyForActivation.TrySetResult(false);
                try
                {
                    if (Environment.GetEnvironmentVariable(UpdateProtocol.UpdateLifecycleWire.EnvironmentVariable) != null)
                    {
                        TryLogLifecycleWarning("Opt-in update lifecycle startup failed", nameof(RunStartupObservedAsync), ex);
                        return;
                    }
                    if (_logger == null)
                    {
                        if (!_sessionEnding)
                        {
                            NativeAppDialogFallback.ShowStartupError(ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException
                                ? "AudioPilot could not initialize logging. Check that the app's data folder and %LOCALAPPDATA%\\AudioPilot are available and writable."
                                : "AudioPilot failed to start before logging was available.");
                        }
                    }
                    else
                    {
                        try { _logger.Fatal("App", "Unexpected application startup failure", nameof(RunStartupObservedAsync), ex); }
                        catch (Exception loggingError) { TryLogLifecycleWarning("Failed to log startup failure", nameof(RunStartupObservedAsync), loggingError); }
                        if (!_sessionEnding)
                        {
                            await DialogService.ShowErrorAsync(
                                "AudioPilot failed to start. Please check AudioPilot.log for details.",
                                DialogText.Captions.StartupError);
                        }
                    }
                }
                catch (Exception reportingException)
                {
                    TryLogLifecycleWarning("Failed to report startup failure", nameof(RunStartupObservedAsync), reportingException);
                }
                finally
                {
                    ShutdownWithCode(3);
                }
            }
        }

        private async Task RunStartupAsync(string[] args)
        {
            var startupStopwatch = Stopwatch.StartNew();
            bool stallRecovery = UiStallRecoveryLaunch.TryParse(args, out SingleInstanceOwnerIdentity stalledOwner);
            if (stallRecovery)
            {
                if (!TryEnterUiRecovery(stalledOwner)) { ShutdownWithCode(0); return; }
                args = [];
            }
            bool shellLaunch = ShellActivation.TryParseArguments(args, out ShellAction shellAction);
            string? shellPayload = shellLaunch ? ShellActivation.ToPayload(shellAction) : null;

            bool prefersJson = CliHostUtilities.PrefersJson(args);
            string cliExecutableName = CliHostUtilities.DefaultCliExecutableName;

            if (!CliCommand.TryParse(shellLaunch ? [] : args, out CliCommand startupCommand, out string? cliError))
            {
                CliHostUtilities.WriteCliError(
                    prefersJson ? Console.Out : Console.Error,
                    exitCode: 2,
                    errorCode: "invalid-usage",
                    message: CliHostUtilities.FormatParseError(cliError, args),
                    jsonOutput: prefersJson,
                    includeUsage: !prefersJson,
                    helpExecutablePathOrName: cliExecutableName);
                ShutdownWithCode(2);
                return;
            }

            if (startupCommand.Action == CliAction.Help)
            {
                Console.WriteLine(CliCommand.GetHelpText(startupCommand.Key));
                ShutdownWithCode(0);
                return;
            }

            if (startupCommand.Action == CliAction.Version)
            {
                Console.WriteLine($"AudioPilot {GetType().Assembly.GetName().Version}");
                ShutdownWithCode(0);
                return;
            }

            if (!startupCommand.IsNoOpLaunch)
            {
                CliHostUtilities.WriteCliError(
                    startupCommand.JsonOutput ? Console.Out : Console.Error,
                    exitCode: 2,
                    errorCode: "cli-host-required",
                    message: $"Run CLI commands with {cliExecutableName}.",
                    jsonOutput: startupCommand.JsonOutput,
                    includeUsage: !startupCommand.JsonOutput,
                    helpExecutablePathOrName: cliExecutableName);
                ShutdownWithCode(2);
                return;
            }

            ApplicationSingleInstanceStartupState singleInstanceStartup = await ApplicationBootstrapper.InitializeSingleInstanceAsync(shellPayload,
                showUserErrors: false, signalExistingInstance: !stallRecovery, logonLaunch: startupCommand.IsLogonLaunch,
                cancellationToken: _startupCancellation.Token);
            if (stallRecovery && singleInstanceStartup.AcquireResult.Acquired)
            {
                singleInstanceStartup.Helper.Dispose();
                ShutdownWithCode(0);
                return;
            }
            if (!singleInstanceStartup.AcquireResult.Acquired)
            {
                if (startupCommand.IsLogonLaunch)
                {
                    singleInstanceStartup.Helper.Dispose();
                    ShutdownWithCode(singleInstanceStartup.AcquireResult.Disposition == SingleInstanceAcquireDisposition.AcquisitionFailed ? 3 : 0);
                    return;
                }

                if (!SingleInstanceStartupRecoveryCoordinator.NeedsRecovery(singleInstanceStartup.AcquireResult))
                {
                    int activationExitCode = singleInstanceStartup.AcquireResult.ResponseExitCode.GetValueOrDefault();
                    if (activationExitCode != 0)
                    {
                        _logger.Warning(
                            "App",
                            () => $"single-instance-activation-rejected | exitCode={activationExitCode} errorCode={(singleInstanceStartup.AcquireResult.ResponseErrorCode ?? "unknown")}");
                        if (!_sessionEnding)
                        {
                            await DialogService.ShowAsync(AppDialogRequest.Acknowledge(
                                BuildActivationHandoffFailureMessage(singleInstanceStartup.AcquireResult.ResponseErrorCode),
                                DialogText.Captions.StartupError, AppDialogKind.Error));
                        }
                        singleInstanceStartup.Helper.Dispose();
                        ShutdownWithCode(activationExitCode);
                        return;
                    }

                    if (_logger.IsEnabled(LogLevel.Info))
                    {
                        _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.ActivationHandoff);
                    }

                    singleInstanceStartup.Helper.Dispose();
                    ShutdownWithCode(0);
                    return;
                }

                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.RecoveryRequired} | disposition={singleInstanceStartup.AcquireResult.Disposition} failureKind={singleInstanceStartup.AcquireResult.FailureKind} responseErrorCode={singleInstanceStartup.AcquireResult.ResponseErrorCode ?? "none"}");
                }

                var recoveryCoordinator = new SingleInstanceStartupRecoveryCoordinator(
                    _logger,
                    DialogService);
                SingleInstanceStartupRecoveryResult recoveryResult = await recoveryCoordinator.ResolveAsync(
                    singleInstanceStartup.AcquireResult,
                    () => singleInstanceStartup.Helper.TryAcquireDetailed(shellPayload, showUserErrors: false),
                    () => singleInstanceStartup.Helper.TryAcquireDetailed(showUserErrors: false, signalExistingInstance: false),
                    singleInstanceStartup.Helper.ProbeExistingOwnerAsync,
                    stalledOwner: stallRecovery ? stalledOwner : null,
                    cancellationToken: _startupCancellation.Token);

                if (!recoveryResult.ContinueStartup)
                {
                    singleInstanceStartup.Helper.Dispose();
                    ShutdownWithCode(recoveryResult.ExitCode);
                    return;
                }
            }

            var singleInstance = singleInstanceStartup.Helper;
            Interlocked.Exchange(ref _uiRecoveryLease, null)?.Dispose();
            AttachSingleInstanceLifetimeHandlers(singleInstance);

            try
            {
                AppShellIntegration.InitializeIdentity();
                if (Services.Updates.AppUpdateLifecycleSession.Current is { } authenticatingSession)
                    await authenticatingSession.AuthenticateAsync(_startupCancellation.Token);
                AppRuntimeHost runtimeHost = await AppRuntimeHost.CreateAndInitializeAsync(
                    this,
                    singleInstance,
                    DialogService,
                    _logger,
                    composedHost => _runtimeHost = composedHost,
                    _startupCancellation.Token,
                    isLogonLaunch: startupCommand.IsLogonLaunch);
                _runtimeReadyForActivation.TrySetResult(true);
                runtimeHost.QueueMediaWarmup();
                StartUiResponsivenessMonitor(runtimeHost);
                PropertyChangedEventManager.AddHandler(LocalizationService.Default, OnShellLanguageChanged, nameof(LocalizationService.Revision));
                if (Services.Updates.AppUpdateLifecycleSession.Current is { } updateSession)
                    _updateLifecycleTask = ObserveUpdateLifecycleAsync(updateSession, runtimeHost);
                _updateAttachmentTask = ObserveUpdateAttachmentsAsync(runtimeHost);

                if (shellLaunch) _ = await ExecuteShellActionAsync(runtimeHost, Dispatcher, shellAction, runtimeHost.AppViewModel.ShutdownToken);
                QueueJumpListRegistration();
            }
            catch (AppRuntimeStartupAbortedException ex)
            {
                _runtimeReadyForActivation.TrySetResult(false);
                if (ex.Outcome == AppRuntimeStartupInitializationOutcome.Fatal)
                {
                    _logger.Fatal(
                        "App",
                        () => $"Application runtime startup aborted | outcome={ex.Outcome}",
                        nameof(RunStartupAsync),
                        ex);
                }
                else
                {
                    _logger.Warning(
                        "App",
                        () => $"Application runtime startup aborted | outcome={ex.Outcome}",
                        nameof(RunStartupAsync),
                        ex);
                }

                ShutdownWithCode(3);
                return;
            }
            startupStopwatch.Stop();
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", () => $"Startup pipeline reached runtime-ready state in {startupStopwatch.Elapsed.TotalMilliseconds:F1}ms | mainWindowCreated={_runtimeHost?.WindowManager.IsCreated == true}");
            }
        }

        private async Task ObserveUpdateLifecycleAsync(Services.Updates.AppUpdateLifecycleSession session, AppRuntimeHost host)
        {
            try { await session.RunAsync(host, _startupCancellation.Token); }
            catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException)
            {
                if (!_startupCancellation.IsCancellationRequested)
                    TryLogLifecycleWarning("Opt-in lifecycle control ended without a confirmed update shutdown", nameof(ObserveUpdateLifecycleAsync), ex);
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Opt-in lifecycle channel failed", nameof(ObserveUpdateLifecycleAsync), ex);
                host.CancelUpdatePreparation();
            }
            finally { session.Dispose(); }
        }

        private async Task ObserveUpdateAttachmentsAsync(AppRuntimeHost host)
        {
            try { await Services.Updates.AppUpdateLifecycleSession.ListenForAttachmentsAsync(host, _startupCancellation.Token); }
            catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested) { }
            catch (Exception error) { TryLogLifecycleWarning("Opt-in updater attachment listener failed", nameof(ObserveUpdateAttachmentsAsync), error); }
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);
            if (e.Cancel) return;
            // WPF schedules CriticalShutdown immediately when OnSessionEnding returns with Cancel == false,
            // before WM_ENDSESSION arrives. Run the full bounded session-ending preparation here.
            BeginSessionEnding("on-session-ending");
        }

        private void OnEndSessionNotification(bool ending)
        {
            if (!ending)
            {
                _logger?.Info("App", "session-ending-cancelled");
                return;
            }

            // Idempotent fallback if Windows delivers WM_ENDSESSION directly without OnSessionEnding.
            BeginSessionEnding("wm-endsession");
        }

        private void BeginSessionEnding(string source)
        {
            _sessionEnding = true;
            SessionEndingState.MarkEnding();
            if (!_startupCancellation.IsCancellationRequested) _startupCancellation.Cancel();
            if (Interlocked.Exchange(ref _sessionEndingStarted, 1) != 0)
            {
                return;
            }

            AppRuntimeHost? runtimeHost = _runtimeHost;
            if (runtimeHost == null)
            {
                if (_dialogService.IsValueCreated)
                {
                    _ = _dialogService.Value.DisposeAsync().AsTask();
                }

                return;
            }

            try
            {
                bool prepared = AppSessionEndingCoordinator.Prepare(false, Dispatcher,
                    runtimeHost.PrepareForSessionEndingAsync,
                    TimeSpan.FromMilliseconds(AppConstants.Timing.SessionEndingPreparationTimeoutMs),
                    ex => TryLogLifecycleWarning("Session-ending preparation failed", source, ex));
                if (!prepared) _logger?.Warning("App", () => $"session-ending-preparation-timeout | source={source} continuing=true");
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Session-ending preparation could not complete", source, ex);
            }
        }

        private DispatcherOperation? _jumpListRegistration;

        private void OnShellLanguageChanged(object? sender, PropertyChangedEventArgs e) => QueueJumpListRegistration();

        private void QueueJumpListRegistration()
        {
            if (_startupCancellation.IsCancellationRequested || AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher)
                || _jumpListRegistration?.Status == DispatcherOperationStatus.Pending) return;

            _jumpListRegistration = Dispatcher.InvokeAsync(() =>
            {
                _jumpListRegistration = null;
                if (!_startupCancellation.IsCancellationRequested && _runtimeHost?.AppViewModel.IsRuntimeShutdownRequested == false)
                    AppShellIntegration.RegisterJumpList(this);
            }, DispatcherPriority.ApplicationIdle, _startupCancellation.Token);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _ = StopUiResponsivenessMonitorAsync().ContinueWith(task =>
                TryLogLifecycleWarning("Failed to stop UI responsiveness monitoring", nameof(OnExit), task.Exception!),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Interlocked.Exchange(ref _uiRecoveryLease, null)?.Dispose();
            PropertyChangedEventManager.RemoveHandler(LocalizationService.Default, OnShellLanguageChanged, nameof(LocalizationService.Revision));
            _jumpListRegistration?.Abort();
            _jumpListRegistration = null;
            try { Services.Updates.AppUpdateLifecycleSession.Current?.Dispose(); }
            catch (Exception error) { TryLogLifecycleWarning("Failed to close the opt-in lifecycle channel", nameof(OnExit), error); }
            if (_updateLifecycleTask?.IsFaulted == true) _ = _updateLifecycleTask.Exception;
            if (_updateAttachmentTask?.IsFaulted == true) _ = _updateAttachmentTask.Exception;
            if (!_startupCancellation.IsCancellationRequested) _startupCancellation.Cancel();
            DetachPilotPresentationHandlers();
            _runtimeReadyForActivation.TrySetResult(false);
            AppRuntimeHost? runtimeHost = Interlocked.Exchange(ref _runtimeHost, null);
            try
            {
                runtimeHost?.BeginEmergencyShutdown();
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to enter the emergency runtime shutdown barrier", nameof(OnExit), ex);
            }

            try
            {
                DetachLifetimeEventHandlers();
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to detach lifetime event handlers during app shutdown", nameof(OnExit), ex);
            }

            try
            {
                ApplicationBootstrapper.DisposeSingleInstance();
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to dispose single-instance resources during app shutdown", nameof(OnExit), ex);
            }

            Interlocked.Exchange(ref _sessionEndingNotifications, null)?.Dispose();
            if (_dialogService.IsValueCreated)
            {
                try { _ = _dialogService.Value.DisposeAsync().AsTask(); }
                catch (Exception ex) { TryLogLifecycleWarning("Failed to dispose dialog service during app shutdown", nameof(OnExit), ex); }
            }

            try
            {
                // Session-ending priorities already used their short budget. Do not
                // extend Windows shutdown by waiting again for slow log-file I/O.
                if (_logger != null)
                {
                    if (_sessionEnding) _ = _logger.DisposeAsync().AsTask();
                    else _logger.Dispose();
                }
            }
            catch (Exception ex)
            {
                LifecycleFallbackDiagnostics.Write("App", "Failed to dispose logger during app shutdown", nameof(OnExit), ex);
            }

            _startupCancellation.Dispose();
            base.OnExit(e);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                _logger?.Error("App", "Unobserved task exception occurred", nameof(OnUnobservedTaskException), e.Exception);
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to log unobserved task exception", nameof(OnUnobservedTaskException), ex);
            }

            try
            {
                e.SetObserved();
            }
            catch (Exception ex)
            {
                TryLogLifecycleWarning("Failed to mark unobserved task exception as observed", nameof(OnUnobservedTaskException), ex);
            }
        }

        private void TryLogLifecycleWarning(string message, string operation, Exception ex)
        {
            try
            {
                if (_logger != null) _logger.Warning("App", message, operation, ex);
                else LifecycleFallbackDiagnostics.Write("App", message, operation, ex);
            }
            catch (Exception loggingEx)
            {
                LifecycleFallbackDiagnostics.Write("App", message, operation, ex, loggingEx);
            }
        }

        private void AttachUnobservedTaskExceptionHandler()
        {
            if (_unobservedTaskExceptionHandlerRegistered)
            {
                return;
            }

            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            _unobservedTaskExceptionHandlerRegistered = true;
        }

        private void AttachSingleInstanceLifetimeHandlers(SingleInstanceHelper singleInstance)
        {
            ArgumentNullException.ThrowIfNull(singleInstance);

            if (ReferenceEquals(_singleInstanceWithLifetimeHandlers, singleInstance))
            {
                return;
            }

            if (_singleInstanceWithLifetimeHandlers != null)
            {
                _singleInstanceWithLifetimeHandlers.ActivationRequestedAsync -= OnActivationRequestedAsync;
                _singleInstanceWithLifetimeHandlers.OwnerStateRequested -= OnSingleInstanceOwnerStateRequestedAsync;
                _singleInstanceWithLifetimeHandlers.CommandRequested -= OnCommandRequested;
            }

            singleInstance.ActivationRequestedAsync += OnActivationRequestedAsync;
            singleInstance.OwnerStateRequested += OnSingleInstanceOwnerStateRequestedAsync;
            singleInstance.CommandRequested += OnCommandRequested;
            _singleInstanceWithLifetimeHandlers = singleInstance;
            singleInstance.EnsureActivationListenerStarted();
        }

        private void DetachLifetimeEventHandlers()
        {
            if (_unobservedTaskExceptionHandlerRegistered)
            {
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                _unobservedTaskExceptionHandlerRegistered = false;
            }

            if (_singleInstanceWithLifetimeHandlers != null)
            {
                _singleInstanceWithLifetimeHandlers.ActivationRequestedAsync -= OnActivationRequestedAsync;
                _singleInstanceWithLifetimeHandlers.OwnerStateRequested -= OnSingleInstanceOwnerStateRequestedAsync;
                _singleInstanceWithLifetimeHandlers.CommandRequested -= OnCommandRequested;
                _singleInstanceWithLifetimeHandlers = null;
            }
        }

        private async Task<SingleInstanceOwnerState> OnSingleInstanceOwnerStateRequestedAsync(CancellationToken cancellationToken)
        {
            bool IsStopping() => _startupCancellation.IsCancellationRequested
                || _runtimeHost?.AppViewModel.IsRuntimeShutdownRequested == true
                || AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher);

            if (IsStopping()) return SingleInstanceOwnerState.Stopping;
            if (!_runtimeReadyForActivation.Task.IsCompleted) return SingleInstanceOwnerState.Starting;
            if (!_runtimeReadyForActivation.Task.Result) return SingleInstanceOwnerState.Stopping;
            if (_runtimeHost?.IsUpdateAdmissionBlocked == true || Debugger.IsAttached) return SingleInstanceOwnerState.Starting;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(AppConstants.Timing.SingleInstanceUiProbeTimeoutMs);
            try
            {
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Input, deadline.Token).Task.ConfigureAwait(false);
                return IsStopping() ? SingleInstanceOwnerState.Stopping : SingleInstanceOwnerState.Responsive;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return IsStopping() ? SingleInstanceOwnerState.Stopping : SingleInstanceOwnerState.Unresponsive;
            }
        }

        private bool IsRuntimeReady => _runtimeReadyForActivation.Task.IsCompletedSuccessfully && _runtimeReadyForActivation.Task.Result;

        private async Task<bool> OnActivationRequestedAsync(CancellationToken cancellationToken)
        {
            if (!IsRuntimeReady)
            {
                try
                {
                    bool runtimeReady = await _runtimeReadyForActivation.Task.WaitAsync(
                        TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceResponseTimeoutMs - 1000), cancellationToken);
                    if (!runtimeReady)
                    {
                        return false;
                    }

                }
                catch (TimeoutException ex)
                {
                    _logger.Warning("App", "Activation request timed out while application runtime was starting", nameof(OnActivationRequestedAsync), ex);
                    return false;
                }
            }

            AppRuntimeHost? runtimeHost = _runtimeHost;
            Dispatcher dispatcher = Dispatcher;
            if (runtimeHost == null || runtimeHost.IsUpdateAdmissionBlocked || AppDispatcherHelper.IsDispatcherUnavailable(dispatcher))
            {
                return false;
            }

            try
            {
                if (dispatcher.CheckAccess())
                {
                    return await runtimeHost.AppViewModel.ShowWindowAsync(cancellationToken);
                }

                return await dispatcher.InvokeAsync(
                    () => runtimeHost.IsUpdateAdmissionBlocked ? Task.FromResult(false) : runtimeHost.AppViewModel.ShowWindowAsync(cancellationToken),
                    DispatcherPriority.Input, cancellationToken).Task.Unwrap();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(dispatcher))
            {
                _logger.Warning("App", "Activation request ignored because UI dispatcher is shutting down", nameof(OnActivationRequestedAsync), ex);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning("App", "Activation request failed to show the main window", nameof(OnActivationRequestedAsync), ex);
                return false;
            }
        }

        private async Task<SingleInstanceCommandResult> OnCommandRequested(string payload, CancellationToken cancellationToken)
        {
            if (_runtimeHost?.IsUpdateAdmissionBlocked == true)
                return new SingleInstanceCommandResult(3, ErrorCode: "update-preparation-active", ProtocolVersion: SingleInstanceHelper.ResponseProtocolVersion);
            if (ShellActivation.TryParsePayload(payload, out ShellAction shellAction))
            {
                if (!await _runtimeReadyForActivation.Task.WaitAsync(cancellationToken))
                    return new SingleInstanceCommandResult(3, ErrorCode: "runtime-host-unavailable", ProtocolVersion: SingleInstanceHelper.ResponseProtocolVersion);
                bool activated = await ExecuteShellActionAsync(_runtimeHost, Dispatcher, shellAction, cancellationToken);
                return new SingleInstanceCommandResult(activated ? 0 : 3, ErrorCode: activated ? null : "activation-failed",
                    ProtocolVersion: SingleInstanceHelper.ResponseProtocolVersion);
            }
            if (!IsRuntimeReady || _runtimeHost == null)
            {
                return new SingleInstanceCommandResult(3, ErrorCode: "runtime-host-unavailable", ErrorMessage: "AudioPilot is still starting.", ProtocolVersion: SingleInstanceHelper.ResponseProtocolVersion);
            }

            if (!CliCommand.TryFromPipePayload(payload, out CliCommand command, out string? failureReason, out int? protocolVersion))
            {
                _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.App.CliForwardParseFailed} | reason={(failureReason ?? "invalid-payload")} protocolVersion={(protocolVersion?.ToString() ?? "unknown")}");
                return new SingleInstanceCommandResult(
                    6,
                    ErrorCode: "forwarded-protocol-mismatch",
                    ErrorMessage: "The running AudioPilot instance uses an incompatible CLI forwarding protocol.",
                    ProtocolVersion: SingleInstanceHelper.ResponseProtocolVersion);
            }

            using var trace = OperationTrace.Start("cli-forward", _logger);
            string opId = trace.Id;
            var stopwatch = Stopwatch.StartNew();
            _logger.Info("App", () => $"{AppConstants.Audio.LogEvents.App.CliForwardStart} | opId={opId} action={command.Action}");

            cancellationToken.ThrowIfCancellationRequested();
            var executionResult = await ExecuteCliCommandAsync(command, cancellationToken);
            stopwatch.Stop();
            trace.Complete(executionResult.ExitCode == 0 ? "success" : $"exit-{executionResult.ExitCode}");
            _logger.Info(
                "App",
                () => $"{AppConstants.Audio.LogEvents.App.CliForwardComplete} | opId={opId} action={command.Action} exitCode={executionResult.ExitCode} durationMs={stopwatch.Elapsed.TotalMilliseconds:F1}");
            return new SingleInstanceCommandResult(executionResult.ExitCode, executionResult.Output, ProtocolVersion: SingleInstanceHelper.ResponseProtocolVersion)
            {
                AfterResponse = executionResult.AfterResponse,
            };
        }

        internal static async Task<bool> ExecuteShellActionAsync(
            AppRuntimeHost? host, Dispatcher dispatcher, ShellAction action, CancellationToken cancellationToken)
        {
            if (host == null || host.IsUpdateAdmissionBlocked || host.AppViewModel.IsRuntimeShutdownRequested || AppDispatcherHelper.IsDispatcherUnavailable(dispatcher)) return false;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, host.AppViewModel.ShutdownToken);
            try
            {
                return await dispatcher.InvokeAsync(async () =>
                {
                    if (host.IsUpdateAdmissionBlocked || host.AppViewModel.IsRuntimeShutdownRequested) return false;
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (action == ShellAction.DevicePicker)
                    {
                        host.AppViewModel.ShowQuickDevicePicker();
                        return true;
                    }
                    if (action == ShellAction.VolumeMixer)
                    {
                        host.AppViewModel.ShowQuickMixer();
                        return true;
                    }
                    return await host.WindowManager.ShowAsync(MainWindowOpenTarget.Routines, cancellationToken: lifetime.Token);
                }, DispatcherPriority.Input, lifetime.Token).Task.Unwrap();
            }
            catch (OperationCanceledException) when (host.AppViewModel.IsRuntimeShutdownRequested)
            {
                return false;
            }
        }

        private async Task<CliExecutionResult> ExecuteCliCommandAsync(CliCommand command, CancellationToken cancellationToken)
        {
            AppRuntimeHost? runtimeHost = _runtimeHost;
            if (runtimeHost == null || runtimeHost.AppViewModel.IsRuntimeShutdownRequested)
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }

            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtimeHost.AppViewModel.ShutdownToken);
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                return await CliCommandExecutor.ExecuteAsync(
                    command,
                    new AppViewModelCliRuntime(runtimeHost.AppViewModel, runtimeHost.WindowManager, lifetime.Token));
            }
            catch (UiDispatcherUnavailableException)
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }
            catch (OperationCanceledException) when (runtimeHost.AppViewModel.ShutdownToken.IsCancellationRequested)
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && command.Action == CliAction.Refresh)
            {
                _logger.Error("App", AppConstants.Audio.LogEvents.ViewModel.RefreshFailed, nameof(ExecuteCliCommandAsync), ex);
                return CliCommandExecutor.BuildExecutionFailureResult(7, "refresh-failed", "Refresh command failed.", command.JsonOutput);
            }
        }

        internal static LocalizedText BuildActivationHandoffFailureMessage(string? errorCode)
        {
            if (string.Equals(errorCode, SingleInstanceHelper.HeadlessBusyErrorCode, StringComparison.Ordinal))
                return new("Message_ACommandLineOperationIsStillRunning");
            return string.Equals(errorCode, "activation-failed", StringComparison.Ordinal)
                ? new("Message_TheRunningAudioPilotInstanceCouldNotShow")
                : new("Message_TheRunningAudioPilotInstanceCouldNotProcess");
        }

        private void ShutdownWithCode(int exitCode)
        {
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
        }

        internal Task RequestRuntimeShutdownAsync(string reason)
        {
            if (!_startupCancellation.IsCancellationRequested) _startupCancellation.Cancel();
            AppRuntimeHost? runtimeHost = _runtimeHost;
            if (runtimeHost != null)
            {
                return runtimeHost.RequestShutdownAsync(reason);
            }

            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Shutdown();
            }

            return Task.CompletedTask;
        }

        private async Task ShutdownFromCliAsync()
        {
            try
            {
                if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher)) return;
                await Dispatcher.InvokeAsync(() => RequestRuntimeShutdownAsync("cli-exit"), DispatcherPriority.Send).Task.Unwrap();
            }
            catch (Exception ex) { TryLogLifecycleWarning("CLI shutdown failed", nameof(ShutdownFromCliAsync), ex); }
        }

        private void OnExternalCloseRequested()
        {
            _ = ShutdownFromExternalCloseAsync();
        }

        private async Task ShutdownFromExternalCloseAsync()
        {
            try
            {
                if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher)) return;
                await Dispatcher.InvokeAsync(() => RequestRuntimeShutdownAsync("external-close"), DispatcherPriority.Send).Task.Unwrap();
            }
            catch (Exception ex) { TryLogLifecycleWarning("External close shutdown failed", nameof(ShutdownFromExternalCloseAsync), ex); }
        }
    }

    internal readonly record struct ApplicationSingleInstanceStartupState(
        SingleInstanceHelper Helper,
        SingleInstanceAcquireResult AcquireResult);

    public static class ApplicationBootstrapper
    {
        private static SingleInstanceHelper? _singleInstance;

        internal static async Task<ApplicationSingleInstanceStartupState> InitializeSingleInstanceAsync(
            string? payloadToExistingInstance = null,
            bool showUserErrors = true,
            bool signalExistingInstance = true,
            bool logonLaunch = false,
            CancellationToken cancellationToken = default)
        {
            var helper = new SingleInstanceHelper(isHeadless: false, deferActivationListener: true);
            _singleInstance = helper;
            Task<SingleInstanceAcquireResult> TryAcquireAsync(CancellationToken token) => logonLaunch
                ? helper.TryAcquireForLogonAsync(token)
                : helper.TryAcquireDetailedAsync(payloadToExistingInstance, showUserErrors, signalExistingInstance, token);
            SingleInstanceAcquireResult initial = await TryAcquireAsync(cancellationToken);
            SingleInstanceAcquireResult result = await SingleInstanceStartupRecoveryCoordinator.WaitForStartupHandoffAsync(
                initial, TryAcquireAsync, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(helper, result);
        }

        internal static void DisposeSingleInstance()
        {
            Interlocked.Exchange(ref _singleInstance, null)?.Dispose();
        }
    }
}

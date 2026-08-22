using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot
{
    public partial class App : Application
    {
        internal IAppDialogService DialogService { get; } = new AppDialogService();
        private sealed class UiDispatcherUnavailableException(Exception? innerException = null)
            : InvalidOperationException("UI dispatcher is not available.", innerException);

        private sealed class AppViewModelCliRuntime(
            AppViewModel appVm,
            IAppMainWindowManager windowManager) : ICliCommandRuntime
        {
            private readonly AppViewModel _appVm = appVm;
            private readonly IAppMainWindowManager _windowManager = windowManager;

            private static bool IsUiDispatcherUnavailable(Dispatcher? dispatcher)
            {
                return AppDispatcherHelper.IsDispatcherUnavailable(dispatcher);
            }

            private static UiDispatcherUnavailableException CreateDispatcherUnavailableException(Exception? innerException = null)
            {
                return new UiDispatcherUnavailableException(innerException);
            }

            private static T InvokeOnUi<T>(Func<T> action)
            {
                var dispatcher = Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return action();
                }

                if (dispatcher.CheckAccess())
                {
                    return action();
                }

                if (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException();
                }

                try
                {
                    return dispatcher.Invoke(action);
                }
                catch (InvalidOperationException ex) when (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException(ex);
                }
            }

            private static void InvokeOnUi(Action action)
            {
                var dispatcher = Current?.Dispatcher;
                if (dispatcher == null)
                {
                    action();
                    return;
                }

                if (dispatcher.CheckAccess())
                {
                    action();
                    return;
                }

                if (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException();
                }

                try
                {
                    dispatcher.Invoke(action);
                }
                catch (InvalidOperationException ex) when (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException(ex);
                }
            }

            private static Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action)
            {
                var dispatcher = Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return action();
                }

                if (dispatcher.CheckAccess())
                {
                    return action();
                }

                if (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException();
                }

                try
                {
                    return dispatcher.InvokeAsync(action).Task.Unwrap();
                }
                catch (InvalidOperationException ex) when (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException(ex);
                }
            }

            private static Task InvokeOnUiAsync(Func<Task> action)
            {
                var dispatcher = Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return action();
                }

                if (dispatcher.CheckAccess())
                {
                    return action();
                }

                if (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException();
                }

                try
                {
                    return dispatcher.InvokeAsync(action).Task.Unwrap();
                }
                catch (InvalidOperationException ex) when (IsUiDispatcherUnavailable(dispatcher))
                {
                    throw CreateDispatcherUnavailableException(ex);
                }
            }

            public void ShowWindow() => InvokeOnUi(_appVm.ShowWindow);
            public void HideWindow() => InvokeOnUi(_appVm.MinimizeWindow);
            public Task<bool> ShowWindowAsync() => InvokeOnUiAsync(() => _appVm.ShowWindowAsync());
            public Task<bool> HideWindowAsync() => InvokeOnUiAsync(async () =>
            {
                _appVm.MinimizeWindow();
                await Task.Yield();
                return !_windowManager.IsVisible;
            });
            public void MediaPlayPause() => InvokeOnUi(() => _appVm.MediaPlayPauseFromCli());
            public void MediaNextTrack() => InvokeOnUi(() => _appVm.MediaNextTrackFromCli());
            public void MediaPreviousTrack() => InvokeOnUi(() => _appVm.MediaPreviousTrackFromCli());
            public Task<string> GetMediaStatusAsync(bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.GetMediaStatusFromCliAsync(jsonOutput, redactOutput));
            public bool ToggleMuteMic() => InvokeOnUi(_appVm.ToggleMuteMicFromCli);
            public bool SetMuteMic(bool enabled) => InvokeOnUi(() => _appVm.SetMuteMicFromCli(enabled));
            public bool ToggleMuteSound() => InvokeOnUi(_appVm.ToggleMuteSoundFromCli);
            public bool SetMuteSound(bool enabled) => InvokeOnUi(() => _appVm.SetMuteSoundFromCli(enabled));
            public bool ToggleDeafen() => InvokeOnUi(_appVm.ToggleDeafenFromCli);
            public bool SetDeafen(bool enabled) => InvokeOnUi(() => _appVm.SetDeafenFromCli(enabled));
            public bool ToggleListenToInput() => InvokeOnUi(_appVm.ToggleListenToInputFromCli);
            public bool SetListenToInput(bool enabled) => InvokeOnUi(() => _appVm.SetListenToInputFromCli(enabled));
            public string GetMuteStatus(string target, bool jsonOutput) => InvokeOnUi(() => _appVm.GetMuteStatusFromCli(target, jsonOutput));
            public string GetListenStatus(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetListenStatusFromCli(jsonOutput, redactOutput));
            public (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput, bool redactOutput = false) => InvokeOnUi(() => _appVm.GetVolumeFromCli(playback, deviceId, jsonOutput, redactOutput));
            public (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput, bool redactOutput = false) => InvokeOnUi(() => _appVm.SetVolumeFromCli(playback, deviceId, percent, jsonOutput, redactOutput));
            public string GetRoutineList(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetRoutineListFromCli(jsonOutput, redactOutput));
            public Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.RunRoutineFromCliAsync(routineSelector, jsonOutput, redactOutput));
            public CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.SetRoutineEnabledFromCli(routineSelector, enabled, jsonOutput, redactOutput));
            public CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.CreateRoutineFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.UpdateRoutineFromCli(routineSelector, path, allowAnyPath, jsonOutput, redactOutput));
            public CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.DeleteRoutineFromCli(routineSelector, jsonOutput, redactOutput));
            public CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ImportRoutinesFromCli(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
            public Task<CliExecutionResult> SetRoutineEnabledAsync(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.SetRoutineEnabledFromCliAsync(routineSelector, enabled, jsonOutput, redactOutput));
            public Task<CliExecutionResult> CreateRoutineAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.CreateRoutineFromCliAsync(path, allowAnyPath, jsonOutput, redactOutput));
            public Task<CliExecutionResult> UpdateRoutineAsync(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.UpdateRoutineFromCliAsync(routineSelector, path, allowAnyPath, jsonOutput, redactOutput));
            public Task<CliExecutionResult> DeleteRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.DeleteRoutineFromCliAsync(routineSelector, jsonOutput, redactOutput));
            public Task<CliExecutionResult> ImportRoutinesAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ImportRoutinesFromCliAsync(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
            public async ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse) => await InvokeOnUiAsync(async () => await _appVm.SwitchOutputFromCliAsync(muteMic, muteSound, deafen, reverse));
            public async ValueTask<bool> SwitchInputAsync(bool reverse) => await InvokeOnUiAsync(async () => await _appVm.SwitchInputFromCliAsync(reverse));
            public Task RefreshAsync() => InvokeOnUiAsync(_appVm.RefreshFromCliAsync);
            public bool SetStartupEnabled(bool enabled) => InvokeOnUi(() => _appVm.SetStartupEnabledFromCli(enabled));
            public bool OpenStartupSettings() => InvokeOnUi(_appVm.OpenStartupSettingsFromCli);
            public string GetStartupStatus(bool jsonOutput) => InvokeOnUi(() => _appVm.GetStartupStatusFromCli(jsonOutput));
            public string GetStatus(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetStatusFromCli(jsonOutput, redactOutput));
            public string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsStatusFromCli(jsonOutput, showPaths, redactOutput));
            public string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsHistoryFromCli(jsonOutput, limit, type, redactOutput));
            public (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDiagnosticsHistoryDetailFromCli(opId, jsonOutput, redactOutput));
            public (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportLogsFromCli(path, allowAnyPath, detailLevel, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> ExportDiagnosticBundleAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput) =>
                InvokeOnUiAsync(() => _appVm.ExportDiagnosticBundleFromCliAsync(path, allowAnyPath, detailLevel, includeSensitive, jsonOutput));
            public (bool Success, string Output) ResetPerAppAudioRouting(bool jsonOutput) => InvokeOnUi(() => _appVm.ResetPerAppAudioRoutingFromCli(jsonOutput));
            public string GetDeviceList(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDeviceListFromCli(output, jsonOutput, redactOutput));
            public (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetDeviceFromCli(output, selector, jsonOutput, redactOutput));
            public (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.FindDevicesFromCli(output, query, jsonOutput, redactOutput));
            public string GetCycle(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleFromCli(output, jsonOutput, redactOutput));
            public (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleValidationFromCli(output, jsonOutput, redactOutput));
            public (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetCycleTestFromCli(output, jsonOutput, redactOutput));
            public (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.AddCycleDeviceFromCli(output, deviceId, jsonOutput, redactOutput));
            public (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.RemoveCycleDeviceFromCli(output, deviceId, jsonOutput, redactOutput));
            public (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ReorderCycleFromCli(output, deviceIds, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> AddCycleDeviceAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.AddCycleDeviceFromCliAsync(output, deviceId, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> RemoveCycleDeviceAsync(bool output, string deviceId, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.RemoveCycleDeviceFromCliAsync(output, deviceId, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> ReorderCycleAsync(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ReorderCycleFromCliAsync(output, deviceIds, jsonOutput, redactOutput));
            public (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.PreviewSwitchFromCli(output, reverse, jsonOutput, redactOutput));
            public string? GetCurrentDeviceId(bool output) => InvokeOnUi(() => _appVm.GetCurrentDeviceIdFromCli(output));
            public Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput) =>
                InvokeOnUiAsync(() => _appVm.WaitForDeviceFromCliAsync(deviceId, timeoutMs, outputOnly, inputOnly, jsonOutput, redactOutput, _appVm.ShutdownToken));
            public (bool Found, string? Value, string? Error) GetConfig(string key) => InvokeOnUi(() => _appVm.GetConfigFromCli(key));
            public string GetConfigList(bool jsonOutput) => InvokeOnUi(() => CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), jsonOutput));
            public (bool Updated, string? Error) SetConfig(string key, string value) => InvokeOnUi(() => _appVm.SetConfigFromCli(key, value));
            public Task<(bool Updated, string? Error)> SetConfigAsync(string key, string value) => InvokeOnUiAsync(() => _appVm.SetConfigFromCliAsync(key, value));
            public (bool Found, string? Value, string? Error) GetRuntime(string key) => InvokeOnUi(() => AppViewModel.GetRuntimeFromCli(key));
            public string GetRuntimeList(bool jsonOutput) => InvokeOnUi(() => CliOutputFormatter.FormatSupportedKeyList("runtime", CliRuntimeManager.GetKnownKeys(), jsonOutput));
            public (bool Updated, string? Error) SetRuntime(string key, string value) => InvokeOnUi(() => AppViewModel.SetRuntimeFromCli(key, value));
            public (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.GetConfigValidationFromCli(jsonOutput, redactOutput));
            public (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportRoutinesFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ExportConfigFromCli(path, allowAnyPath, jsonOutput, redactOutput));
            public (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUi(() => _appVm.ImportConfigFromCli(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> ExportRoutinesAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ExportRoutinesFromCliAsync(path, allowAnyPath, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> ExportConfigAsync(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ExportConfigFromCliAsync(path, allowAnyPath, jsonOutput, redactOutput));
            public Task<(bool Success, string Output)> ImportConfigAsync(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => InvokeOnUiAsync(() => _appVm.ImportConfigFromCliAsync(path, replaceImport, allowAnyPath, jsonOutput, redactOutput));
            public Task<string> GetNetworkListAsync(bool jsonOutput, bool redactOutput) => _appVm.GetNetworkListFromCliAsync(jsonOutput, redactOutput);
        }

        private AppRuntimeHost? _runtimeHost;
        private readonly Logger _logger = Logger.Instance;
        private readonly TaskCompletionSource<bool> _runtimeReadyForActivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _unobservedTaskExceptionHandlerRegistered;
        private SingleInstanceHelper? _singleInstanceWithLifetimeHandlers;
        private int _pendingActivationRequest;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AttachUnobservedTaskExceptionHandler();
            _ = RunStartupObservedAsync(e.Args);
        }

        private async Task RunStartupObservedAsync(string[] args)
        {
            try
            {
                await RunStartupAsync(args);
            }
            catch (Exception ex)
            {
                _runtimeReadyForActivation.TrySetResult(false);
                _logger.Fatal("App", "Unexpected application startup failure", nameof(RunStartupObservedAsync), ex);
                await DialogService.ShowErrorAsync(
                    "AudioPilot failed to start. Please check AudioPilot.log for details.",
                    DialogText.Captions.StartupError);
                ShutdownWithCode(3);
            }
        }

        private async Task RunStartupAsync(string[] args)
        {
            var startupStopwatch = Stopwatch.StartNew();

            bool prefersJson = CliHostUtilities.PrefersJson(args);
            string cliExecutableName = CliHostUtilities.ResolveCliExecutableName(typeof(App).Assembly.Location);

            if (!CliCommand.TryParse(args, out CliCommand startupCommand, out string? cliError))
            {
                CliHostUtilities.WriteCliError(
                    Console.Error,
                    exitCode: 2,
                    errorCode: "invalid-usage",
                    message: cliError ?? "Invalid CLI usage.",
                    jsonOutput: prefersJson,
                    includeUsage: !prefersJson,
                    helpExecutablePathOrName: cliExecutableName);
                ShutdownWithCode(2);
                return;
            }

            if (startupCommand.Action == CliAction.Help)
            {
                Console.WriteLine(CliCommand.UsageText);
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
                    Console.Error,
                    exitCode: 2,
                    errorCode: "cli-host-required",
                    message: $"Run CLI commands with {cliExecutableName}.",
                    jsonOutput: startupCommand.JsonOutput,
                    includeUsage: !startupCommand.JsonOutput,
                    helpExecutablePathOrName: cliExecutableName);
                ShutdownWithCode(2);
                return;
            }

            ApplicationSingleInstanceStartupState singleInstanceStartup = ApplicationBootstrapper.InitializeSingleInstance(showUserErrors: false);
            if (!singleInstanceStartup.AcquireResult.Acquired)
            {
                if (singleInstanceStartup.AcquireResult.ExistingHealthy)
                {
                    int activationExitCode = singleInstanceStartup.AcquireResult.ResponseExitCode.GetValueOrDefault();
                    if (activationExitCode != 0)
                    {
                        _logger.Warning(
                            "App",
                            () => $"single-instance-activation-rejected | exitCode={activationExitCode} errorCode={(singleInstanceStartup.AcquireResult.ResponseErrorCode ?? "unknown")}");
                        await DialogService.ShowErrorAsync(
                            BuildActivationHandoffFailureMessage(singleInstanceStartup.AcquireResult.ResponseErrorCode),
                            DialogText.Captions.StartupError);
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
                    _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ExistingInstanceUnresponsive} | failureKind={singleInstanceStartup.AcquireResult.FailureKind}");
                }

                var recoveryCoordinator = new SingleInstanceStartupRecoveryCoordinator(
                    new SingleInstanceProcessRecoveryHelper(_logger),
                    _logger,
                    DialogService);
                SingleInstanceStartupRecoveryResult recoveryResult = await recoveryCoordinator.ResolveAsync(
                    () => singleInstanceStartup.Helper.TryAcquireDetailed(showUserErrors: false));

                if (!recoveryResult.ContinueStartup)
                {
                    singleInstanceStartup.Helper.Dispose();
                    ShutdownWithCode(recoveryResult.ExitCode);
                    return;
                }
            }

            var singleInstance = singleInstanceStartup.Helper;
            AttachSingleInstanceLifetimeHandlers(singleInstance);

            try
            {
                _runtimeHost = await AppRuntimeHost.CreateAndInitializeAsync(
                    this,
                    singleInstance,
                    DialogService,
                    _logger);
                _runtimeReadyForActivation.TrySetResult(true);

                if (Interlocked.Exchange(ref _pendingActivationRequest, 0) != 0)
                {
                    _ = await _runtimeHost.AppViewModel.ShowWindowAsync();
                }
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
            catch (Exception ex)
            {
                _runtimeReadyForActivation.TrySetResult(false);
                _logger.Fatal("App", "Failed to initialize application runtime", nameof(RunStartupAsync), ex);
                await DialogService.ShowErrorAsync("AudioPilot failed to start. Please check AudioPilot.log for details.", DialogText.Captions.StartupError);
                ShutdownWithCode(3);
                return;
            }

            startupStopwatch.Stop();
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", () => $"Startup pipeline reached runtime-ready state in {startupStopwatch.Elapsed.TotalMilliseconds:F1}ms | mainWindowCreated={_runtimeHost.WindowManager.IsCreated}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
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

            try
            {
                _logger.Dispose();
            }
            catch (Exception ex)
            {
                LifecycleFallbackDiagnostics.Write("App", "Failed to dispose logger during app shutdown", nameof(OnExit), ex);
            }

            base.OnExit(e);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                _logger.Error("App", "Unobserved task exception occurred", nameof(OnUnobservedTaskException), e.Exception);
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
                _logger.Warning("App", message, operation, ex);
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
                _singleInstanceWithLifetimeHandlers.CommandRequested -= OnCommandRequested;
            }

            singleInstance.ActivationRequestedAsync += OnActivationRequestedAsync;
            singleInstance.CommandRequested += OnCommandRequested;
            _singleInstanceWithLifetimeHandlers = singleInstance;
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
                _singleInstanceWithLifetimeHandlers.CommandRequested -= OnCommandRequested;
                _singleInstanceWithLifetimeHandlers = null;
            }
        }

        private async Task<bool> OnActivationRequestedAsync()
        {
            AppRuntimeHost? runtimeHost = _runtimeHost;
            if (runtimeHost == null)
            {
                Interlocked.Exchange(ref _pendingActivationRequest, 1);
                try
                {
                    bool runtimeReady = await _runtimeReadyForActivation.Task.WaitAsync(
                        TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceResponseTimeoutMs - 1000));
                    if (!runtimeReady)
                    {
                        return false;
                    }

                    runtimeHost = _runtimeHost;
                    if (runtimeHost == null)
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

            Dispatcher dispatcher = Dispatcher;
            if (AppDispatcherHelper.IsDispatcherUnavailable(dispatcher))
            {
                return false;
            }

            try
            {
                if (dispatcher.CheckAccess())
                {
                    return await runtimeHost.AppViewModel.ShowWindowAsync();
                }

                return await dispatcher.InvokeAsync(
                    () => runtimeHost.AppViewModel.ShowWindowAsync(),
                    DispatcherPriority.Input).Task.Unwrap();
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

        private async Task<SingleInstanceCommandResult> OnCommandRequested(string payload)
        {
            if (_runtimeHost == null)
            {
                return new SingleInstanceCommandResult(3, ErrorCode: "runtime-host-unavailable", ErrorMessage: "AudioPilot is still starting.", ProtocolVersion: 2);
            }

            if (!CliCommand.TryFromPipePayload(payload, out CliCommand command, out string? failureReason, out int? protocolVersion))
            {
                _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.App.CliForwardParseFailed} | reason={(failureReason ?? "invalid-payload")} protocolVersion={(protocolVersion?.ToString() ?? "unknown")}");
                return new SingleInstanceCommandResult(
                    6,
                    ErrorCode: "forwarded-protocol-mismatch",
                    ErrorMessage: "The running AudioPilot instance uses an incompatible CLI forwarding protocol.",
                    ProtocolVersion: 1);
            }

            string opId = Guid.NewGuid().ToString("N")[..8];
            var stopwatch = Stopwatch.StartNew();
            _logger.Info("App", () => $"{AppConstants.Audio.LogEvents.App.CliForwardStart} | opId={opId} action={command.Action}");

            var executionResult = await ExecuteCliCommandAsync(command);
            stopwatch.Stop();
            _logger.Info(
                "App",
                () => $"{AppConstants.Audio.LogEvents.App.CliForwardComplete} | opId={opId} action={command.Action} exitCode={executionResult.ExitCode} durationMs={stopwatch.Elapsed.TotalMilliseconds:F1}");
            return new SingleInstanceCommandResult(executionResult.ExitCode, executionResult.Output, ProtocolVersion: 1);
        }

        private async Task<CliExecutionResult> ExecuteCliCommandAsync(CliCommand command)
        {
            AppRuntimeHost? runtimeHost = _runtimeHost;
            if (runtimeHost == null)
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }

            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }

            try
            {
                return await CliCommandExecutor.ExecuteAsync(
                    command,
                    new AppViewModelCliRuntime(runtimeHost.AppViewModel, runtimeHost.WindowManager));
            }
            catch (UiDispatcherUnavailableException)
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }
            catch (OperationCanceledException) when (runtimeHost.AppViewModel.ShutdownToken.IsCancellationRequested)
            {
                return CliCommandExecutor.BuildRuntimeUnavailableResult(command.JsonOutput);
            }
            catch (Exception ex) when (command.Action == CliAction.Refresh)
            {
                _logger.Error("App", AppConstants.Audio.LogEvents.ViewModel.RefreshFailed, nameof(ExecuteCliCommandAsync), ex);
                return CliCommandExecutor.BuildExecutionFailureResult(7, "refresh-failed", "Refresh command failed.", command.JsonOutput);
            }
        }

        internal static string BuildActivationHandoffFailureMessage(string? errorCode)
        {
            return string.Equals(errorCode, "activation-failed", StringComparison.Ordinal)
                ? "The running AudioPilot instance could not show its main window. Try opening it from the tray menu, or exit it and start AudioPilot again."
                : "The running AudioPilot instance could not process the request. Try again, or exit it and start AudioPilot again.";
        }

        private void ShutdownWithCode(int exitCode)
        {
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
        }

        internal Task RequestRuntimeShutdownAsync(string reason)
        {
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
    }

    internal readonly record struct ApplicationSingleInstanceStartupState(
        SingleInstanceHelper Helper,
        SingleInstanceAcquireResult AcquireResult);

    public static class ApplicationBootstrapper
    {
        private static SingleInstanceHelper? _singleInstance;

        internal static ApplicationSingleInstanceStartupState InitializeSingleInstance(
            string? payloadToExistingInstance = null,
            bool showUserErrors = true)
        {
            _singleInstance = new SingleInstanceHelper();
            return new ApplicationSingleInstanceStartupState(
                _singleInstance,
                _singleInstance.TryAcquireDetailed(payloadToExistingInstance, showUserErrors));
        }

        public static bool ShouldStart(string? payloadToExistingInstance = null)
        {
            return InitializeSingleInstance(payloadToExistingInstance).AcquireResult.Acquired;
        }

        public static SingleInstanceHelper GetSingleInstance()
        {
            return _singleInstance ?? throw new InvalidOperationException("SingleInstanceHelper not initialized");
        }

        internal static void DisposeSingleInstance()
        {
            Interlocked.Exchange(ref _singleInstance, null)?.Dispose();
        }
    }
}

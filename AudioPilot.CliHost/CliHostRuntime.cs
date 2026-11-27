using System.Runtime.InteropServices;
using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.CliHost
{
    internal interface ICliHostSingleInstance : IDisposable
    {
        bool TryAcquire(string? payloadToExistingInstance = null);
        bool LastSignalExistingSucceeded { get; }
        SingleInstanceSignalFailureKind LastSignalFailureKind { get; }
        int? LastSignalExitCode { get; }
        string? LastSignalOutput { get; }
        string? LastSignalErrorCode { get; }
        string? LastSignalErrorMessage { get; }
        int? LastSignalProtocolVersion { get; }
    }

    internal interface ICliHeadlessCommandRunner : IDisposable
    {
        CliExecutionResult Execute(CliCommand command);
    }

    internal sealed record CliHostRuntimeDependencies(
        Func<ICliHostSingleInstance> CreateSingleInstance,
        Func<ICliHeadlessCommandRunner> CreateHeadlessRunner,
        Func<bool> ShouldForceHeadlessFailure,
        TextWriter StandardOutput,
        TextWriter StandardError,
        Func<IDisposable?>? AcquireMutationLease = null);

    internal static class CliHostRuntime
    {
        private static readonly Lazy<Logger> LazyLogger = new(static () => Logger.Instance);
        private static Logger Logger => LazyLogger.Value;
        private readonly record struct HeadlessFailureMapping(string ErrorCode, string Message, string LogContext);

        internal static int Execute(string[] args, CliHostRuntimeDependencies dependencies)
        {
            bool prefersJson = CliHostUtilities.PrefersJson(args);
            string? suggestedHelpTopic = CliHostUtilities.InferHelpTopic(args);
            string helpExecutableName = CliHostUtilities.ResolveCliExecutableName();

            if (!CliCommand.TryParse(args, out CliCommand command, out string? cliError))
            {
                CliHostUtilities.WriteCliError(
                    prefersJson ? dependencies.StandardOutput : dependencies.StandardError,
                    2,
                    "invalid-usage",
                    CliHostUtilities.FormatParseError(cliError, args),
                    prefersJson,
                    includeUsage: !prefersJson && suggestedHelpTopic == null,
                    suggestedHelpTopic: suggestedHelpTopic,
                    helpExecutablePathOrName: helpExecutableName);
                return 2;
            }

            if (command.Action == CliAction.Help)
            {
                return DeliverResult(new(0, CliCommand.GetHelpText(command.Key)), command, dependencies);
            }

            if (command.Action == CliAction.Completion)
            {
                return DeliverResult(new(0, CliShellCompletionGenerator.GetScript(command.Key ?? "powershell")), command, dependencies);
            }

            if (command.Action == CliAction.Version)
            {
                return DeliverResult(new(0, $"AudioPilot {typeof(CliCommand).Assembly.GetName().Version}"), command, dependencies);
            }

            if (command.Action == CliAction.ConfigList)
            {
                string output = CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), command.JsonOutput);
                return DeliverResult(new(0, output), command, dependencies);
            }

            if (command.IsNoOpLaunch)
            {
                CliHostUtilities.WriteCliError(command.JsonOutput ? dependencies.StandardOutput : dependencies.StandardError, 2, "invalid-usage", "Missing command.", command.JsonOutput, includeUsage: true, helpExecutablePathOrName: helpExecutableName);
                return 2;
            }

            var singleInstance = dependencies.CreateSingleInstance();
            try
            {
                bool acquired = singleInstance.TryAcquire(command.ToPipePayload());
                if (!acquired)
                {
                    bool forwarded = singleInstance.LastSignalExistingSucceeded;
                    if (!forwarded)
                    {
                        string errorCode = singleInstance.LastSignalFailureKind switch
                        {
                            SingleInstanceSignalFailureKind.ConnectionFailed => "ui-host-unresponsive",
                            SingleInstanceSignalFailureKind.OutcomeUnknown => "ui-host-outcome-unknown",
                            SingleInstanceSignalFailureKind.InvalidResponse => "ui-host-invalid-response",
                            SingleInstanceSignalFailureKind.AcquisitionFailed => "single-instance-acquisition-failed",
                            _ => "forwarding-failed",
                        };
                        string message = singleInstance.LastSignalFailureKind switch
                        {
                            SingleInstanceSignalFailureKind.ConnectionFailed => "The running AudioPilot UI host is not responding.",
                            SingleInstanceSignalFailureKind.OutcomeUnknown => "The request may have reached AudioPilot, but no result was received. Its outcome is unknown; check current state before retrying.",
                            SingleInstanceSignalFailureKind.InvalidResponse => "The running AudioPilot UI host returned an invalid forwarding response.",
                            SingleInstanceSignalFailureKind.AcquisitionFailed => "AudioPilot could not check whether another instance is running.",
                            _ => "Command forwarding failed (existing instance unreachable).",
                        };
                        if (Logger.IsEnabled(LogLevel.Warning))
                        {
                            Logger.Warning("CliHostRuntime", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardConnectFailed} | action={command.Action} failureKind={singleInstance.LastSignalFailureKind}");
                        }
                        CliHostUtilities.WriteCliError(command.JsonOutput ? dependencies.StandardOutput : dependencies.StandardError, 4, errorCode, message, command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                        return 4;
                    }

                    if (IsProtocolMismatch(singleInstance))
                    {
                        string mismatchMessage = singleInstance.LastSignalErrorMessage
                            ?? singleInstance.LastSignalOutput
                            ?? "The running AudioPilot instance uses an incompatible CLI forwarding protocol.";
                        if (Logger.IsEnabled(LogLevel.Warning))
                        {
                            Logger.Warning("CliHostRuntime", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardProtocolMismatch} | action={command.Action} responseProtocolVersion={(singleInstance.LastSignalProtocolVersion?.ToString() ?? "unknown")}");
                        }
                        CliHostUtilities.WriteCliError(command.JsonOutput ? dependencies.StandardOutput : dependencies.StandardError, 6, "forwarded-protocol-mismatch", mismatchMessage, command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                        return 6;
                    }

                    if (singleInstance.LastSignalExitCode is int forwardedExitCode
                        && forwardedExitCode != 0
                        && !string.IsNullOrWhiteSpace(singleInstance.LastSignalErrorCode)
                        && string.IsNullOrWhiteSpace(singleInstance.LastSignalOutput))
                    {
                        return DeliverResult(CliCommandExecutor.BuildExecutionFailureResult(
                            forwardedExitCode,
                            singleInstance.LastSignalErrorCode,
                            singleInstance.LastSignalErrorMessage ?? "Forwarded command failed.",
                            command.JsonOutput), command, dependencies);
                    }

                    return DeliverResult(new CliExecutionResult(singleInstance.LastSignalExitCode ?? 0, singleInstance.LastSignalOutput), command, dependencies);
                }

                if (CliHostUtilities.IsUiOnlyAction(command.Action))
                {
                    string message = command.Action == CliAction.RuntimeSet
                        ? "Runtime changes require a running AudioPilot instance. Start AudioPilot first, or use config set for supported persistent settings."
                        : "No running UI host instance is available to execute this command.";
                    CliHostUtilities.WriteCliError(command.JsonOutput ? dependencies.StandardOutput : dependencies.StandardError, 3, "ui-host-unavailable", message, command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                    return 3;
                }

                ICliHeadlessCommandRunner? runner = null;
                IDisposable? mutationLease = null;
                CliExecutionResult result;
                try
                {
                    if (RequiresMutationLease(command))
                        mutationLease = (dependencies.AcquireMutationLease ?? (() => UpdateMutationLease.Acquire(AppContext.BaseDirectory)))();
                    if (dependencies.ShouldForceHeadlessFailure())
                    {
                        throw new InvalidOperationException("Forced headless runtime failure for test.");
                    }

                    runner = dependencies.CreateHeadlessRunner();
                    result = runner.Execute(command);
                }
                catch (UpdateMutationBlockedException error)
                {
                    return DeliverResult(CliCommandExecutor.BuildExecutionFailureResult(3, error.ErrorCode, error.Message, command.JsonOutput), command, dependencies);
                }
                catch (Exception ex)
                {
                    HeadlessFailureMapping failure = MapHeadlessFailure(command, ex);
                    Logger.Error(
                        "CliHostRuntime",
                        () => $"headless-command-failed | action={command.Action} context={failure.LogContext}",
                        nameof(Execute),
                        ex);

                    if (command.Action == CliAction.Refresh)
                    {
                        CliHostUtilities.WriteCliError(command.JsonOutput ? dependencies.StandardOutput : dependencies.StandardError, 7, "refresh-failed", "Refresh command failed.", command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                        return 7;
                    }

                    CliHostUtilities.WriteCliError(command.JsonOutput ? dependencies.StandardOutput : dependencies.StandardError, 7, failure.ErrorCode, failure.Message, command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                    return 7;
                }
                finally
                {
                    DisposeSafely(runner, command, "headless-runner");
                    DisposeSafely(mutationLease, command, "update-mutation-lease");
                }
                return DeliverResult(result, command, dependencies);
            }
            finally
            {
                DisposeSafely(singleInstance, command, "single-instance");
            }
        }

        internal static bool RequiresMutationLease(CliCommand command)
        {
            if (command.DryRun && command.Action is CliAction.SwitchOutput or CliAction.SwitchInput
                or CliAction.SwitchOutputToDevice or CliAction.SwitchInputToDevice)
            {
                return false;
            }

            return RequiresMutationLease(command.Action);
        }

        internal static bool RequiresMutationLease(CliAction action) => action is not (
            CliAction.Help or CliAction.Version or CliAction.Completion or CliAction.Status or CliAction.Update or
            CliAction.StartupStatus or CliAction.StartupOpen or CliAction.DevicesListOutput or CliAction.DevicesListInput or
            CliAction.DevicesGetOutput or CliAction.DevicesGetInput or CliAction.DevicesFindOutput or CliAction.DevicesFindInput or
            CliAction.CycleShowOutput or CliAction.CycleShowInput or CliAction.CycleValidateOutput or CliAction.CycleValidateInput or
            CliAction.CycleTestOutput or CliAction.CycleTestInput or
            CliAction.MediaStatus or CliAction.NetworkList or CliAction.ConfigGet or CliAction.ConfigList or CliAction.ConfigValidate or
            CliAction.ConfigExport or CliAction.ConfigImportPreview or
            CliAction.RuntimeGet or CliAction.RuntimeList or CliAction.WaitForDevice or CliAction.VolumeGetMaster or CliAction.VolumeGetMic or
            CliAction.DiagnosticsStatus or CliAction.DiagnosticsHistory or CliAction.DiagnosticsHistoryDetail or
            CliAction.DiagnosticsTestProcessMonitor or CliAction.DiagnosticsExportLogs or CliAction.DiagnosticsExportBundle or
            CliAction.RoutineList or CliAction.RoutineGet or CliAction.RoutineCheck or CliAction.RoutineNext or CliAction.RoutineExport or
            CliAction.AppList or CliAction.AppListInput or
            CliAction.AppVolumeGet or CliAction.AppRouteGetOutput or CliAction.AppRouteGetInput or CliAction.WindowStatus);

        private static int DeliverResult(CliExecutionResult result, CliCommand command, CliHostRuntimeDependencies dependencies)
        {
            bool writeToStdErr = !command.JsonOutput && result.ExitCode != 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(result.Output)) (writeToStdErr ? dependencies.StandardError : dependencies.StandardOutput).WriteLine(result.Output);
                return result.ExitCode;
            }
            catch (Exception)
            {
                string message = $"Command finished with exit code {result.ExitCode}, but its output could not be delivered. Check current state before retrying.";
                try
                {
                    dependencies.StandardError.WriteLine(command.JsonOutput
                        ? CliOutputFormatter.SerializeCliErrorJson(
                            new { ExecutionCompleted = true, CommandExitCode = result.ExitCode },
                            8,
                            "output-delivery-failed",
                            message)
                        : CliHostUtilities.FormatTextError("output-delivery-failed", message));
                }
                catch (Exception) { }
                return 8;
            }
        }

        private static void DisposeSafely(IDisposable? resource, CliCommand command, string stage)
        {
            try { resource?.Dispose(); }
            catch (Exception ex)
            {
                try { Logger.Warning("CliHostRuntime", $"cli-cleanup-failed | action={command.Action} stage={stage} hresult=0x{ex.HResult:X8}"); }
                catch (Exception) { }
            }
        }

        private static bool IsProtocolMismatch(ICliHostSingleInstance singleInstance)
        {
            return string.Equals(singleInstance.LastSignalErrorCode, "forwarded-protocol-mismatch", StringComparison.OrdinalIgnoreCase);
        }

        private static HeadlessFailureMapping MapHeadlessFailure(CliCommand command, Exception exception)
        {
            return exception switch
            {
                UnauthorizedAccessException => new HeadlessFailureMapping(
                    "headless-access-denied",
                    "Headless command execution failed due to access restrictions.",
                    "unauthorized-access"),
                IOException => new HeadlessFailureMapping(
                    "headless-io-failed",
                    "Headless command execution failed while reading or writing required files.",
                    "io"),
                JsonException => new HeadlessFailureMapping(
                    "headless-config-parse-failed",
                    "Headless command execution failed because configuration data could not be parsed.",
                    "json"),
                COMException => new HeadlessFailureMapping(
                    "headless-platform-failed",
                    "Headless command execution failed while accessing Windows platform services.",
                    "com"),
                _ => new HeadlessFailureMapping(
                    "headless-runtime-failed",
                    $"Headless command execution failed for '{command.Action}'.",
                    exception.GetType().Name),
            };
        }
    }

    internal sealed class SingleInstanceHelperAdapter(SingleInstanceHelper? singleInstance = null) : ICliHostSingleInstance
    {
        private readonly SingleInstanceHelper _inner = singleInstance ?? new(isHeadless: true);

        public bool LastSignalExistingSucceeded => _inner.LastSignalExistingSucceeded;
        public SingleInstanceSignalFailureKind LastSignalFailureKind => _inner.LastSignalFailureKind;
        public int? LastSignalExitCode => _inner.LastSignalExitCode;
        public string? LastSignalOutput => _inner.LastSignalOutput;
        public string? LastSignalErrorCode => _inner.LastSignalErrorCode;
        public string? LastSignalErrorMessage => _inner.LastSignalErrorMessage;
        public int? LastSignalProtocolVersion => _inner.LastSignalProtocolVersion;

        public bool TryAcquire(string? payloadToExistingInstance = null)
        {
            return _inner.TryAcquire(payloadToExistingInstance);
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }

    internal sealed class LocalHeadlessCommandRunnerAdapter : ICliHeadlessCommandRunner
    {
        private readonly LocalHeadlessCommandRunner _inner = new(ownsCoreAudioExecutor: true);

        public CliExecutionResult Execute(CliCommand command)
        {
            return _inner.ExecuteAsync(command).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }
}

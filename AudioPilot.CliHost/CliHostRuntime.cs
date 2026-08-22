using System.Runtime.InteropServices;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Newtonsoft.Json;

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
        TextWriter StandardError);

    internal static class CliHostRuntime
    {
        private static readonly Logger Logger = Logger.Instance;
        private readonly record struct HeadlessFailureMapping(string ErrorCode, string Message, string LogContext);

        internal static int Execute(string[] args, CliHostRuntimeDependencies dependencies)
        {
            bool prefersJson = CliHostUtilities.PrefersJson(args);
            string? suggestedHelpTopic = CliHostUtilities.InferHelpTopic(args);
            string helpExecutableName = CliHostUtilities.ResolveCliExecutableName();

            if (!CliCommand.TryParse(args, out CliCommand command, out string? cliError))
            {
                CliHostUtilities.WriteCliError(
                    dependencies.StandardError,
                    2,
                    "invalid-usage",
                    cliError ?? "Invalid CLI usage.",
                    prefersJson,
                    includeUsage: !prefersJson && suggestedHelpTopic == null,
                    suggestedHelpTopic: suggestedHelpTopic,
                    helpExecutablePathOrName: helpExecutableName);
                return 2;
            }

            if (command.Action == CliAction.Help)
            {
                dependencies.StandardOutput.WriteLine(CliCommand.GetHelpText(command.Key));
                return 0;
            }

            if (command.Action == CliAction.Completion)
            {
                dependencies.StandardOutput.WriteLine(CliShellCompletionGenerator.GetScript(command.Key ?? "powershell"));
                return 0;
            }

            if (command.Action == CliAction.Version)
            {
                dependencies.StandardOutput.WriteLine($"AudioPilot {typeof(CliCommand).Assembly.GetName().Version}");
                return 0;
            }

            if (command.IsNoOpLaunch)
            {
                CliHostUtilities.WriteCliError(dependencies.StandardError, 2, "invalid-usage", "Missing command.", command.JsonOutput, includeUsage: true, helpExecutablePathOrName: helpExecutableName);
                return 2;
            }

            using var singleInstance = dependencies.CreateSingleInstance();
            bool acquired = singleInstance.TryAcquire(command.ToPipePayload());
            if (!acquired)
            {
                bool forwarded = singleInstance.LastSignalExistingSucceeded;
                if (!forwarded)
                {
                    string errorCode = singleInstance.LastSignalFailureKind switch
                    {
                        SingleInstanceSignalFailureKind.ConnectionFailed => "ui-host-unresponsive",
                        SingleInstanceSignalFailureKind.InvalidResponse => "ui-host-invalid-response",
                        _ => "forwarding-failed",
                    };
                    string message = singleInstance.LastSignalFailureKind switch
                    {
                        SingleInstanceSignalFailureKind.ConnectionFailed => "The running AudioPilot UI host is not responding.",
                        SingleInstanceSignalFailureKind.InvalidResponse => "The running AudioPilot UI host returned an invalid forwarding response.",
                        _ => "Command forwarding failed (existing instance unreachable).",
                    };
                    if (Logger.IsEnabled(LogLevel.Warning))
                    {
                        Logger.Warning("CliHostRuntime", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardConnectFailed} | action={command.Action} failureKind={singleInstance.LastSignalFailureKind}");
                    }
                    CliHostUtilities.WriteCliError(dependencies.StandardError, 4, errorCode, message, command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                    return 4;
                }

                if (IsProtocolMismatch(singleInstance))
                {
                    string mismatchMessage = singleInstance.LastSignalErrorMessage
                        ?? singleInstance.LastSignalOutput
                        ?? "The running AudioPilot instance uses an incompatible CLI forwarding protocol.";
                    if (Logger.IsEnabled(LogLevel.Warning))
                    {
                        Logger.Warning("CliHostRuntime", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardProtocolMismatch} | action={command.Action} responseProtocolVersion={(singleInstance.LastSignalProtocolVersion?.ToString() ?? "unknown")} errorCode={(singleInstance.LastSignalErrorCode ?? "legacy-response")}");
                    }
                    CliHostUtilities.WriteCliError(dependencies.StandardError, 6, "forwarded-protocol-mismatch", mismatchMessage, command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                    return 6;
                }

                if (singleInstance.LastSignalExitCode is int forwardedExitCode
                    && forwardedExitCode != 0
                    && !string.IsNullOrWhiteSpace(singleInstance.LastSignalErrorCode)
                    && string.IsNullOrWhiteSpace(singleInstance.LastSignalOutput))
                {
                    CliHostUtilities.WriteCliError(
                        dependencies.StandardError,
                        forwardedExitCode,
                        singleInstance.LastSignalErrorCode,
                        singleInstance.LastSignalErrorMessage ?? "Forwarded command failed.",
                        command.JsonOutput,
                        includeUsage: false,
                        helpExecutablePathOrName: helpExecutableName);
                    return forwardedExitCode;
                }

                if (!string.IsNullOrWhiteSpace(singleInstance.LastSignalOutput))
                {
                    dependencies.StandardOutput.WriteLine(singleInstance.LastSignalOutput);
                }

                return singleInstance.LastSignalExitCode ?? 0;
            }

            if (CliHostUtilities.IsUiOnlyAction(command.Action))
            {
                CliHostUtilities.WriteCliError(dependencies.StandardError, 3, "ui-host-unavailable", "No running UI host instance is available to execute this command.", command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                return 3;
            }

            try
            {
                if (dependencies.ShouldForceHeadlessFailure())
                {
                    throw new InvalidOperationException("Forced headless runtime failure for test.");
                }

                using var runner = dependencies.CreateHeadlessRunner();
                CliExecutionResult result = runner.Execute(command);

                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    dependencies.StandardOutput.WriteLine(result.Output);
                }

                return result.ExitCode;
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
                    CliHostUtilities.WriteCliError(dependencies.StandardError, 7, "refresh-failed", "Refresh command failed.", command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                    return 7;
                }

                CliHostUtilities.WriteCliError(dependencies.StandardError, 7, failure.ErrorCode, failure.Message, command.JsonOutput, includeUsage: false, helpExecutablePathOrName: helpExecutableName);
                return 7;
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

    internal sealed class SingleInstanceHelperAdapter : ICliHostSingleInstance
    {
        private readonly SingleInstanceHelper _inner = new();

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
        private readonly LocalHeadlessCommandRunner _inner = new();

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

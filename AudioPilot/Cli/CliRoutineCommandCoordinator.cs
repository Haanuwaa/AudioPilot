using AudioPilot.Models;

namespace AudioPilot.Cli
{
    internal sealed class CliRoutineCommandCoordinator(
        IRoutineProcessSnapshotProvider processSnapshots,
        Func<AudioRoutine, int?, Task<RoutineExecutionResult>> execute,
        Action<ExecutionHistoryEntry> recordHistory)
    {
        internal async Task<CliExecutionResult> RunAsync(IReadOnlyList<AudioRoutine> routines, string selector, bool jsonOutput, bool redactOutput = false)
        {
            CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(routines, selector);
            if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            {
                return BuildErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
            }

            AudioRoutine routine = resolution.Routine;
            if (!routine.Enabled)
            {
                recordHistory(new ExecutionHistoryEntry(
                    OpId: $"cli-routine-run:{Guid.NewGuid():N}",
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Kind: ExecutionHistoryKind.Routine,
                    Source: "cli",
                    Action: "routine-run",
                    Success: false,
                    Skipped: true,
                    Summary: $"Routine '{routine.Name}' skipped.",
                    Reason: "Routine is disabled.",
                    RoutineId: routine.Id,
                    RoutineName: routine.Name,
                    Target: routine.TargetSummary,
                    DiagCode: "routine-disabled",
                    Details: new Dictionary<string, string> { ["trigger"] = routine.TriggerKind.ToString(), ["executionSource"] = "cli" }));
                return BuildErrorResult(5, "routine-disabled", $"Routine '{routine.Name}' is disabled.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!routine.HasExecutionTarget)
            {
                recordHistory(new ExecutionHistoryEntry(
                    OpId: $"cli-routine-run:{Guid.NewGuid():N}",
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Kind: ExecutionHistoryKind.Routine,
                    Source: "cli",
                    Action: "routine-run",
                    Success: false,
                    Skipped: true,
                    Summary: $"Routine '{routine.Name}' skipped.",
                    Reason: "Routine has no configured targets.",
                    RoutineId: routine.Id,
                    RoutineName: routine.Name,
                    DiagCode: "routine-has-no-targets",
                    Details: new Dictionary<string, string> { ["trigger"] = routine.TriggerKind.ToString(), ["executionSource"] = "cli" }));
                return BuildErrorResult(5, "routine-has-no-targets", $"Routine '{routine.Name}' has no configured targets.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, processSnapshots, out int? processId, out string? errorCode, out string? errorMessage))
            {
                recordHistory(new ExecutionHistoryEntry(
                    OpId: $"cli-routine-run:{Guid.NewGuid():N}",
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Kind: ExecutionHistoryKind.Routine,
                    Source: "cli",
                    Action: "routine-run",
                    Success: false,
                    Skipped: true,
                    Summary: $"Routine '{routine.Name}' skipped.",
                    Reason: errorMessage,
                    RoutineId: routine.Id,
                    RoutineName: routine.Name,
                    Target: CliRoutineExecutionPolicy.GetTriggerApplicationDisplayName(routine.TriggerAppPath),
                    DiagCode: errorCode ?? "routine-trigger-app-not-running",
                    Details: new Dictionary<string, string> { ["trigger"] = routine.TriggerKind.ToString(), ["executionSource"] = "cli" }));
                return BuildErrorResult(
                    5,
                    errorCode ?? "routine-trigger-app-not-running",
                    errorMessage ?? $"Routine '{routine.Name}' requires the target application to be running.",
                    jsonOutput,
                    routine,
                    CliRoutineExecutionPolicy.GetTriggerApplicationDisplayName(routine.TriggerAppPath),
                    requiresRunningTriggerProcess: true,
                    redactOutput: redactOutput);
            }

            RoutineExecutionResult result = await execute(routine, processId);
            bool hasFailureDetail = !string.IsNullOrWhiteSpace(result.OutputFailureDetail) || !string.IsNullOrWhiteSpace(result.InputFailureDetail);
            bool success = result.Success && !hasFailureDetail;
            string? outputDeviceName = result.OutputDeviceName;
            string? inputDeviceName = result.InputDeviceName;
            if (!success)
            {
                bool? outputSucceeded = result.OutputSucceeded;
                bool? inputSucceeded = result.InputSucceeded;

                if (!string.IsNullOrWhiteSpace(result.OutputFailureDetail) && outputSucceeded == true && !result.AppOutputApplied)
                {
                    outputSucceeded = false;
                }

                if (!string.IsNullOrWhiteSpace(result.InputFailureDetail) && inputSucceeded == true && !result.AppInputApplied)
                {
                    inputSucceeded = false;
                }

                return BuildErrorResult(
                    3,
                    "routine-run-failed",
                    $"Failed to run routine '{routine.Name}'.",
                    jsonOutput,
                    routine,
                    outputSucceeded: outputSucceeded,
                    appliedOutputDeviceName: outputSucceeded == true ? outputDeviceName ?? routine.OutputDeviceName : null,
                    outputFailureDetail: result.OutputFailureDetail,
                    inputSucceeded: inputSucceeded,
                    appliedInputDeviceName: inputSucceeded == true ? inputDeviceName ?? routine.InputDeviceName : null,
                    inputFailureDetail: result.InputFailureDetail,
                    redactOutput: redactOutput);
            }

            return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineRunResult(routine, outputDeviceName, inputDeviceName, jsonOutput, redactOutput));
        }

        internal static CliExecutionResult BuildErrorResult(
            int exitCode,
            string errorCode,
            string message,
            bool jsonOutput,
            AudioRoutine? routine = null,
            string? triggerApplicationName = null,
            bool? requiresRunningTriggerProcess = null,
            bool? outputSucceeded = null,
            string? appliedOutputDeviceName = null,
            string? outputFailureDetail = null,
            bool? inputSucceeded = null,
            string? appliedInputDeviceName = null,
            string? inputFailureDetail = null,
            bool redactOutput = false)
        {
            return new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput, routine, triggerApplicationName, requiresRunningTriggerProcess, outputSucceeded, appliedOutputDeviceName, outputFailureDetail, inputSucceeded, appliedInputDeviceName, inputFailureDetail, redactOutput));
        }
    }
}

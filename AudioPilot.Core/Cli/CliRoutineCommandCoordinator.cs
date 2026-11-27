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
                recordHistory(ExecutionHistoryEntry.RoutineSkipped(routine, "cli", "routine-disabled", "Routine is disabled."));
                return BuildErrorResult(5, "routine-disabled", $"Routine '{routine.Name}' is disabled.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!routine.HasExecutionTarget)
            {
                recordHistory(ExecutionHistoryEntry.RoutineSkipped(routine, "cli", "routine-has-no-targets", "Routine has no configured targets."));
                return BuildErrorResult(5, "routine-has-no-targets", $"Routine '{routine.Name}' has no configured targets.", jsonOutput, routine, redactOutput: redactOutput);
            }

            if (!CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, processSnapshots, out int? processId, out string? errorCode, out string? errorMessage))
            {
                recordHistory(ExecutionHistoryEntry.RoutineSkipped(routine, "cli", errorCode ?? "routine-target-app-not-running",
                    errorMessage ?? "Target application is not running."));
                return BuildErrorResult(
                    errorCode == CliRoutineExecutionPolicy.TargetAppUnavailableCode ? 3 : 5,
                    errorCode ?? "routine-target-app-not-running",
                    errorMessage ?? $"Routine '{routine.Name}' requires the target application to be running.",
                    jsonOutput,
                    routine,
                    CliRoutineExecutionPolicy.GetApplicationDisplayName(routine.TargetAppPath),
                    requiresRunningTargetApplication: true,
                    redactOutput: redactOutput);
            }

            RoutineExecutionResult result = await execute(routine, processId);
            if (result.SkipCode != null)
                return BuildErrorResult(result.SkipCode == CliRoutineExecutionPolicy.TargetAppUnavailableCode ? 3 : 5,
                    result.SkipCode, result.SkipReason ?? "Routine conditions were not met.", jsonOutput, routine, redactOutput: redactOutput);
            bool hasFailureDetail = !string.IsNullOrWhiteSpace(result.OutputFailureDetail) || !string.IsNullOrWhiteSpace(result.InputFailureDetail)
                || !string.IsNullOrWhiteSpace(result.MuteFailureDetail) || !string.IsNullOrWhiteSpace(result.CommunicationsFailureDetail);
            bool success = result.Success && !hasFailureDetail;
            string? outputDeviceName = result.OutputDeviceName;
            string? inputDeviceName = result.InputDeviceName;
            if (!success)
            {
                return BuildErrorResult(
                    3,
                    "routine-run-failed",
                    $"Failed to run routine '{routine.Name}'.",
                    jsonOutput,
                    routine,
                    redactOutput: redactOutput,
                    execution: result);
            }

            return new CliExecutionResult(0, CliOutputFormatter.FormatRoutineRunResult(routine, outputDeviceName, inputDeviceName, jsonOutput, redactOutput));
        }

        internal static CliExecutionResult BuildErrorResult(
            int exitCode,
            string errorCode,
            string message,
            bool jsonOutput,
            AudioRoutine? routine = null,
            string? targetApplicationName = null,
            bool? requiresRunningTargetApplication = null,
            bool redactOutput = false,
            RoutineExecutionResult? execution = null)
        {
            return new CliExecutionResult(exitCode, CliOutputFormatter.FormatRoutineError(exitCode, errorCode, message, jsonOutput, routine, targetApplicationName, requiresRunningTargetApplication, redactOutput, execution));
        }
    }
}

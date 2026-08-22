using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Cli;

internal static class CliRoutineCheckCoordinator
{
    internal static async Task<CliExecutionResult> CheckAsync(IReadOnlyList<AudioRoutine> routines, string selector,
        Func<RoutineReadinessService> createService, bool jsonOutput, bool redactOutput, CancellationToken cancellationToken)
    {
        CliRoutineResolutionResult resolution = CliRoutineResolver.Resolve(routines, selector);
        if (resolution.Status != CliRoutineResolutionStatus.Success || resolution.Routine == null)
            return CliRoutineCommandCoordinator.BuildErrorResult(5, resolution.ErrorCode, resolution.Message, jsonOutput, redactOutput: redactOutput);
        try
        {
            RoutineReadinessResult result = await createService().CheckAsync(resolution.Routine, cancellationToken).ConfigureAwait(false);
            return new(result.Ready ? 0 : result.Unavailable ? 3 : 5, CliOutputFormatter.FormatRoutineCheck(resolution.Routine, result, jsonOutput, redactOutput));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return CliRoutineCommandCoordinator.BuildErrorResult(3, "routine-check-unavailable", "The routine could not be checked.",
                jsonOutput, resolution.Routine, redactOutput: redactOutput);
        }
    }
}

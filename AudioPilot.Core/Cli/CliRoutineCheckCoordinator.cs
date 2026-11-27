using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Cli;

internal static class CliRoutineCheckCoordinator
{
    internal static async Task<CliExecutionResult> CheckAsync(IReadOnlyList<AudioRoutine> routines, string selector,
        Func<RoutineReadinessService> createService, bool jsonOutput, bool redactOutput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selector == "--all")
        {
            try
            {
                IReadOnlyList<(AudioRoutine Routine, RoutineReadinessResult Result)> results = routines.Count == 0 ? []
                    : await createService().CheckAllAsync(routines, cancellationToken).ConfigureAwait(false);
                int exitCode = results.Any(static item => item.Result.Unavailable) ? 3 : results.All(static item => item.Result.Ready) ? 0 : 5;
                return new(exitCode, CliOutputFormatter.FormatRoutineChecks(results, jsonOutput, redactOutput));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return CliRoutineCommandCoordinator.BuildErrorResult(3, "routine-check-all-unavailable", "The routines could not be checked.", jsonOutput, redactOutput: redactOutput);
            }
        }
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

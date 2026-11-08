using AudioPilot.Models;

namespace AudioPilot.Cli
{
    public enum CliRoutineResolutionStatus
    {
        Success = 0,
        NotFound,
        Ambiguous,
    }

    public readonly record struct CliRoutineResolutionResult(
        CliRoutineResolutionStatus Status,
        AudioRoutine? Routine,
        string ErrorCode,
        string Message);

    public static class CliRoutineResolver
    {
        public static CliRoutineResolutionResult Resolve(IReadOnlyList<AudioRoutine>? routines, string selector)
        {
            string normalizedSelector = selector?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedSelector))
            {
                return new CliRoutineResolutionResult(
                    CliRoutineResolutionStatus.NotFound,
                    null,
                    "routine-selector-missing",
                    "Missing routine selector.");
            }

            AudioRoutine? nameMatch = null;
            int nameMatchCount = 0;
            foreach (AudioRoutine routine in routines ?? [])
            {
                if (routine == null)
                {
                    continue;
                }

                if (string.Equals(routine.Id, normalizedSelector, StringComparison.OrdinalIgnoreCase))
                {
                    return new CliRoutineResolutionResult(
                        CliRoutineResolutionStatus.Success,
                        routine,
                        string.Empty,
                        string.Empty);
                }

                if (string.Equals(routine.Name, normalizedSelector, StringComparison.OrdinalIgnoreCase))
                {
                    nameMatch = routine;
                    nameMatchCount++;
                }
            }

            if (nameMatchCount == 1)
            {
                return new CliRoutineResolutionResult(
                    CliRoutineResolutionStatus.Success,
                    nameMatch,
                    string.Empty,
                    string.Empty);
            }

            if (nameMatchCount > 1)
            {
                return new CliRoutineResolutionResult(
                    CliRoutineResolutionStatus.Ambiguous,
                    null,
                    "routine-selector-ambiguous",
                    $"Multiple routines match '{normalizedSelector}'. Use the routine id instead.");
            }

            return new CliRoutineResolutionResult(
                CliRoutineResolutionStatus.NotFound,
                null,
                "routine-not-found",
                $"No routine matches '{normalizedSelector}'.");
        }
    }
}

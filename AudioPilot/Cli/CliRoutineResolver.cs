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

            List<AudioRoutine> candidates = routines?
                .Where(static routine => routine != null)
                .ToList() ?? [];

            AudioRoutine? idMatch = candidates.FirstOrDefault(routine =>
                !string.IsNullOrWhiteSpace(routine.Id) &&
                string.Equals(routine.Id, normalizedSelector, StringComparison.OrdinalIgnoreCase));
            if (idMatch != null)
            {
                return new CliRoutineResolutionResult(
                    CliRoutineResolutionStatus.Success,
                    idMatch,
                    string.Empty,
                    string.Empty);
            }

            List<AudioRoutine> nameMatches =
            [
                .. candidates.Where(routine =>
                    !string.IsNullOrWhiteSpace(routine.Name) &&
                    string.Equals(routine.Name, normalizedSelector, StringComparison.OrdinalIgnoreCase))
            ];

            if (nameMatches.Count == 1)
            {
                return new CliRoutineResolutionResult(
                    CliRoutineResolutionStatus.Success,
                    nameMatches[0],
                    string.Empty,
                    string.Empty);
            }

            if (nameMatches.Count > 1)
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

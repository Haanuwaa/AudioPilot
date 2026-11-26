namespace AudioPilot.Models
{
    public sealed record ExecutionHistoryEntry(
        string OpId,
        DateTimeOffset TimestampUtc,
        ExecutionHistoryKind Kind,
        string Source,
        string Action,
        bool Success,
        bool Skipped,
        string? Summary = null,
        string? Reason = null,
        string? RoutineId = null,
        string? RoutineName = null,
        string? OutputDeviceName = null,
        string? InputDeviceName = null,
        string? Target = null,
        bool? OutputSucceeded = null,
        bool? InputSucceeded = null,
        bool? Enabled = null,
        bool? AwaitingAppCompletion = null,
        bool? AppOutputApplied = null,
        bool? AppInputApplied = null,
        string? DiagCode = null,
        double? ElapsedMs = null,
        IReadOnlyDictionary<string, string>? Details = null,
        string? TraceId = null)
    {
        internal static string FormatRoutineRunSummary(string name, bool success, bool skipped, bool awaitingAppCompletion)
        {
            if (skipped) return $"Routine '{name}' skipped.";
            if (!success) return $"Routine '{name}' failed.";
            return awaitingAppCompletion ? $"Routine '{name}' started; waiting for app audio." : $"Routine '{name}' completed.";
        }

        internal static ExecutionHistoryEntry RoutineSkipped(AudioRoutine routine, string source, string code, string reason) => new(
            OpId: $"routine-run:{Guid.NewGuid():N}", TimestampUtc: DateTimeOffset.UtcNow, Kind: ExecutionHistoryKind.Routine,
            Source: source, Action: "routine-run", Success: false, Skipped: true,
            Summary: FormatRoutineRunSummary(routine.Name, success: false, skipped: true, awaitingAppCompletion: false),
            Reason: reason, RoutineId: routine.Id, RoutineName: routine.Name,
            Target: routine.TargetSummary, DiagCode: code,
            Details: new Dictionary<string, string> { ["trigger"] = routine.TriggerKind.ToString(), ["executionSource"] = source });
    }
}

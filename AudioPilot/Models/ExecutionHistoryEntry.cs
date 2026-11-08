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
        internal static ExecutionHistoryEntry RoutineSkipped(AudioRoutine routine, string source, string code, string reason) => new(
            OpId: $"routine-run:{Guid.NewGuid():N}", TimestampUtc: DateTimeOffset.UtcNow, Kind: ExecutionHistoryKind.Routine,
            Source: source, Action: "routine-run", Success: false, Skipped: true,
            Summary: $"Routine '{routine.Name}' skipped.", Reason: reason, RoutineId: routine.Id, RoutineName: routine.Name,
            Target: routine.TargetSummary, DiagCode: code,
            Details: new Dictionary<string, string> { ["trigger"] = routine.TriggerKind.ToString(), ["executionSource"] = source });
    }
}

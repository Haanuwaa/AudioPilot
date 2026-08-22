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
        IReadOnlyDictionary<string, string>? Details = null);
}

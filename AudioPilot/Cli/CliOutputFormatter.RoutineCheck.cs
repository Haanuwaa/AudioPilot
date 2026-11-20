using AudioPilot.Models;
using AudioPilot.Services.Routines;

namespace AudioPilot.Cli;

public static partial class CliOutputFormatter
{
    internal static string FormatRoutineCheck(AudioRoutine routine, RoutineReadinessResult result, bool jsonOutput, bool redactOutput)
    {
        string name = FormatRoutineName(routine.Name, redactOutput);
        string code = result.Ready ? "routine-check-ready" : result.Unavailable ? "routine-check-unavailable" : "routine-check-blocked";
        RoutineReadinessItem[] checks = [.. result.Checks.Select(check => check with { Target = redactOutput && check.Target != null ? "<redacted>" : check.Target })];
        const string note = "This is a snapshot of current manual-run readiness. No audio actions or reconnects were attempted; execution can still fail if state changes or a device rejects an action.";
        if (jsonOutput) return SerializeCliJson(BuildRoutineCheckSnapshot(routine, result, redactOutput));
        return $"[diag-code:{code}] Routine '{name}': {(result.Ready ? "ready for a manual run" : "not ready for a manual run")}.{Environment.NewLine}" +
            string.Join(Environment.NewLine, checks.Select(static check => $"- {check.Area}: {check.Status}. {check.Message}{(check.Target == null ? string.Empty : $" Target: {check.Target}.")}")) +
            Environment.NewLine + note;
    }
    internal static string FormatRoutineChecks(IReadOnlyList<(AudioRoutine Routine, RoutineReadinessResult Result)> results, bool jsonOutput, bool redactOutput)
    {
        bool ready = results.All(static item => item.Result.Ready);
        string code = ready ? "routine-check-all-ready" : results.Any(static item => item.Result.Unavailable) ? "routine-check-all-unavailable" : "routine-check-all-blocked";
        if (jsonOutput) return SerializeCliJson(new
        {
            DiagCode = code,
            ReadOnly = true,
            Ready = ready,
            results.Count,
            Results = results.Select(item => BuildRoutineCheckSnapshot(item.Routine, item.Result, redactOutput)).ToArray()
        });
        return $"[diag-code:{code}] Checked {results.Count} routines." + Environment.NewLine +
            string.Join(Environment.NewLine + Environment.NewLine, results.Select(item => FormatRoutineCheck(item.Routine, item.Result, false, redactOutput)));
    }

    private static object BuildRoutineCheckSnapshot(AudioRoutine routine, RoutineReadinessResult result, bool redactOutput) => new
    {
        Id = FormatRoutineId(routine.Id, redactOutput),
        Name = FormatRoutineName(routine.Name, redactOutput),
        DiagCode = result.Ready ? "routine-check-ready" : result.Unavailable ? "routine-check-unavailable" : "routine-check-blocked",
        ReadOnly = true,
        result.CheckedUtc,
        result.Ready,
        result.RoutingState,
        Checks = result.Checks.Select(check => check with { Target = redactOutput && check.Target != null ? "<redacted>" : check.Target }).ToArray(),
        Note = "This is a snapshot of current manual-run readiness. No audio actions or reconnects were attempted; execution can still fail if state changes or a device rejects an action.",
    };

}

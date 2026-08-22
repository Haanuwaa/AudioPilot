using AudioPilot.Models;

namespace AudioPilot.Services.Routines;

internal readonly record struct RoutineCycleResult(string Code, AudioRoutine? Routine = null, RoutineExecutionResult Execution = default);

/// <summary>Advances explicit hotkey groups without queuing overlapping presses or changing individual manual runs.</summary>
internal sealed class RoutineHotkeyCycleService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _lastRoutineByGroup = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<RoutineCycleResult> RunAsync(string group, IReadOnlyList<AudioRoutine> configured,
        Func<AudioRoutine, CancellationToken, Task<RoutineExecutionResult>> execute, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return new("routine-cycle-busy");
        try
        {
            var groups = configured.Select(static routine => routine.HotkeyCycleGroup).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string removed in _lastRoutineByGroup.Keys.Where(key => !groups.Contains(key)).ToArray()) _lastRoutineByGroup.Remove(removed);
            AudioRoutine[] members = [.. configured.Where(routine => string.Equals(routine.HotkeyCycleGroup, group, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static routine => routine.DisplayOrder).ThenBy(static routine => routine.Name, StringComparer.OrdinalIgnoreCase).Select(static routine => routine.Clone())];
            int last = _lastRoutineByGroup.TryGetValue(group, out string? lastId)
                ? Array.FindIndex(members, routine => string.Equals(routine.Id, lastId, StringComparison.OrdinalIgnoreCase)) : -1;
            for (int offset = 1; offset <= members.Length; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AudioRoutine routine = members[(last + offset) % members.Length];
                if (!routine.Enabled || !routine.HasExecutionTarget) continue;
                RoutineExecutionResult result = await execute(routine, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Skipped || result.SkipCode != null) continue;
                if (result.Success) _lastRoutineByGroup[group] = routine.Id;
                return new(result.Success ? "routine-cycle-applied" : "routine-cycle-failed", routine, result);
            }
            return new("routine-cycle-no-eligible-routines");
        }
        finally { _gate.Release(); }
    }
}

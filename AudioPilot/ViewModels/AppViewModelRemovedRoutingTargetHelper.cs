using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal static class AppViewModelRemovedRoutingTargetHelper
{
    internal static IReadOnlyList<AppViewModel.RemovedPerAppRoutingTarget> CalculateRemovedPerAppRoutingTargets(
        IEnumerable<AudioRoutine>? previousRoutines,
        IEnumerable<AudioRoutine>? nextRoutines)
    {
        Dictionary<string, AppViewModel.PerAppRoutingSelection> previousSelections = BuildPerAppRoutingSelectionMap(previousRoutines);
        Dictionary<string, AppViewModel.PerAppRoutingSelection> nextSelections = BuildPerAppRoutingSelectionMap(nextRoutines);

        var removedTargets = new List<AppViewModel.RemovedPerAppRoutingTarget>();
        foreach ((string path, AppViewModel.PerAppRoutingSelection previousSelection) in previousSelections.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            nextSelections.TryGetValue(path, out AppViewModel.PerAppRoutingSelection nextSelection);
            bool resetOutput = previousSelection.Output && !nextSelection.Output;
            bool resetInput = previousSelection.Input && !nextSelection.Input;
            if (resetOutput || resetInput)
            {
                removedTargets.Add(new AppViewModel.RemovedPerAppRoutingTarget(path, resetOutput, resetInput));
            }
        }

        return removedTargets;
    }

    private static Dictionary<string, AppViewModel.PerAppRoutingSelection> BuildPerAppRoutingSelectionMap(IEnumerable<AudioRoutine>? routines)
    {
        Dictionary<string, AppViewModel.PerAppRoutingSelection> selections = new(StringComparer.OrdinalIgnoreCase);
        if (routines == null)
        {
            return selections;
        }

        foreach (AudioRoutine routine in routines)
        {
            if (!IsPersistedPerAppRoutingRoutine(routine))
            {
                continue;
            }

            string normalizedPath = RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TriggerAppPath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            selections.TryGetValue(normalizedPath, out AppViewModel.PerAppRoutingSelection currentSelection);
            selections[normalizedPath] = new AppViewModel.PerAppRoutingSelection(
                currentSelection.Output || routine.HasOutputTarget,
                currentSelection.Input || routine.HasInputTarget);
        }

        return selections;
    }

    private static bool IsPersistedPerAppRoutingRoutine(AudioRoutine routine)
    {
        if (routine == null)
        {
            return false;
        }

        return routine.HasApplicationTrigger &&
               routine.SwitchOutputPerApp &&
               RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(routine.TriggerAppPath) &&
               (routine.HasOutputTarget || routine.HasInputTarget);
    }
}

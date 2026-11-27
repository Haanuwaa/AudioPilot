using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private sealed class CliDraftConflictException(string message) : Exception(message) { }

    // Lists have order and membership semantics. Require the user to settle a local list edit before replacing it.
    private void ValidateCliDraftConflicts(Settings before, Settings after)
    {
        if (!AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(before.DeviceSwitching.Output.CycleDevices, after.DeviceSwitching.Output.CycleDevices)
            && !AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(OutputCycleDevices, before.DeviceSwitching.Output.CycleDevices))
            throw new CliDraftConflictException("Output switch order has unsaved GUI edits. Save or discard those edits before changing it through the CLI.");
        if (!AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(before.DeviceSwitching.Input.CycleDevices, after.DeviceSwitching.Input.CycleDevices)
            && !AppSettingsWorkflowCoordinator.AreCycleListsEquivalent(InputCycleDevices, before.DeviceSwitching.Input.CycleDevices))
            throw new CliDraftConflictException("Input switch order has unsaved GUI edits. Save or discard those edits before changing it through the CLI.");
        if (!AreRoutineListsEquivalent(before.Routines.Items, after.Routines.Items)
            && !AreRoutineListsEquivalent(Routines, before.Routines.Items))
            throw new CliDraftConflictException("Routines have unsaved GUI edits. Save or discard those edits before changing them through the CLI.");
    }
}

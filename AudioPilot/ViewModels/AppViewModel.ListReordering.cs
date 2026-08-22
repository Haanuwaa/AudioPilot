using AudioPilot.Helpers;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private void ReorderCycleDevices(object? parameter, bool output)
    {
        if (parameter is not ListReorderRequest request || _isCleaningUp) return;
        var collection = output ? OutputCycleDevices : InputCycleDevices;
        if (!request.Apply(collection)) return;
        AppViewModelDeviceCycleHelper.ReindexCycleDevices(collection);
        int index = collection.IndexOf((Models.CycleDevice)request.Items[0]);
        if (output) SelectedOutputCycleIndex = index;
        else SelectedInputCycleIndex = index;
    }

    private void ReorderRoutines(object? parameter)
    {
        if (parameter is not ListReorderRequest request || _isApplyingRoutines || _isCleaningUp || IsSavingRoutines) return;

        _isApplyingRoutines = true;
        try
        {
            if (!request.Apply(Routines)) return;
            ReindexRoutines();
        }
        finally
        {
            _isApplyingRoutines = false;
        }

        SelectedRoutineIndex = Routines.IndexOf((Models.AudioRoutine)request.Items[0]);
        RefreshAfterRoutineCollectionChanged();
    }
}

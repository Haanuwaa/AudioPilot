using System.Collections.ObjectModel;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    private bool _refreshingCommunications;
    private int _communicationsOutputIndex;
    private int _communicationsInputIndex;
    public ObservableCollection<CycleDevice> CommunicationsOutputDevices { get; } = [];
    public ObservableCollection<CycleDevice> CommunicationsInputDevices { get; } = [];
    public bool IsCommunicationsExpanded { get; set; }
    public int SelectedCommunicationsOutputIndex
    {
        get => _communicationsOutputIndex;
        set { if (_refreshingCommunications || _communicationsOutputIndex == value) return; _communicationsOutputIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSelectedCommunicationsOutputUnavailable)); OnPropertyChanged(nameof(CanRoutePerApp)); }
    }
    public int SelectedCommunicationsInputIndex
    {
        get => _communicationsInputIndex;
        set { if (_refreshingCommunications || _communicationsInputIndex == value) return; _communicationsInputIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSelectedCommunicationsInputUnavailable)); OnPropertyChanged(nameof(CanRoutePerApp)); }
    }

    public bool IsSystemRouting => !SwitchOutputPerApp;
    public bool CanRoutePerApp => SelectedCommunicationsOutputIndex <= 0 && SelectedCommunicationsInputIndex <= 0;
    public bool IsSelectedCommunicationsOutputUnavailable => SelectedCommunicationsOutputIndex > 0 && SelectedCommunicationsOutputIndex < CommunicationsOutputDevices.Count &&
        !_sourceOutputDevices.Any(device => string.Equals(device.Id, CommunicationsOutputDevices[SelectedCommunicationsOutputIndex].Id, StringComparison.OrdinalIgnoreCase));
    public bool IsSelectedCommunicationsInputUnavailable => SelectedCommunicationsInputIndex > 0 && SelectedCommunicationsInputIndex < CommunicationsInputDevices.Count &&
        !_sourceInputDevices.Any(device => string.Equals(device.Id, CommunicationsInputDevices[SelectedCommunicationsInputIndex].Id, StringComparison.OrdinalIgnoreCase));

    public bool IsSystemEventTriggerSelected => SelectedTriggerMode == TriggerModeLabels[8] || SelectedTriggerMode == TriggerModeLabels[9];
    public string SystemEventTriggerDescription => SelectedTriggerMode == TriggerModeLabels[8]
        ? "Runs when you unlock Windows. Unlock and resume in the same wake cycle run this routine once."
        : "Runs after Windows resumes and audio recovery finishes. Unavailable targets are given time to reconnect. This does not wake the computer.";

    private void InitializeCommunicationsTargets(AudioRoutine? routine)
    {
        _ = SyncDeviceCollection(CommunicationsOutputDevices, _sourceOutputDevices, "Leave unchanged", 0);
        _ = SyncDeviceCollection(CommunicationsInputDevices, _sourceInputDevices, "Leave unchanged", 0);
        if (routine?.CommunicationsOutput is { } output)
            SelectedCommunicationsOutputIndex = ResolveSelectedIndex(CommunicationsOutputDevices, output.Id, output.Name, output.StableId);
        if (routine?.CommunicationsInput is { } input)
            SelectedCommunicationsInputIndex = ResolveSelectedIndex(CommunicationsInputDevices, input.Id, input.Name, input.StableId);
        IsCommunicationsExpanded = routine?.HasCommunicationsTarget == true;
    }

    private void RefreshCommunicationsDevices(bool playback)
    {
        _refreshingCommunications = true;
        try
        {
            if (playback) _communicationsOutputIndex = SyncDeviceCollection(CommunicationsOutputDevices, _sourceOutputDevices, "Leave unchanged", _communicationsOutputIndex);
            else _communicationsInputIndex = SyncDeviceCollection(CommunicationsInputDevices, _sourceInputDevices, "Leave unchanged", _communicationsInputIndex);
            OnPropertyChanged(playback ? nameof(SelectedCommunicationsOutputIndex) : nameof(SelectedCommunicationsInputIndex));
            OnPropertyChanged(playback ? nameof(IsSelectedCommunicationsOutputUnavailable) : nameof(IsSelectedCommunicationsInputUnavailable));
            OnPropertyChanged(nameof(CanRoutePerApp));
        }
        finally { _refreshingCommunications = false; }
    }

    private RoutineDeviceReference? BuildCommunicationsTarget(bool playback)
    {
        var devices = playback ? CommunicationsOutputDevices : CommunicationsInputDevices;
        int index = playback ? SelectedCommunicationsOutputIndex : SelectedCommunicationsInputIndex;
        if (index <= 0 || index >= devices.Count) return null;
        CycleDevice device = devices[index];
        return new() { Id = device.Id, Name = device.Name, StableId = device.StableId, Playback = playback };
    }
}

using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    private RoutineDeviceReference? _selectedRequiredDevice;
    private bool _requireDevice;
    private bool _requiredDeviceAvailable = true;
    private bool _requiredDeviceDefault;
    private bool _requiredApplicationRunning = true;
    private bool _requiredNetworkConnected = true;
    private string _requiredApplicationPath = string.Empty;
    private string _requiredNetworkName = string.Empty;
    private bool _updatingDeviceChoices;
    public IReadOnlyList<RoutineDeviceReference> RoutineDeviceChoices { get; private set; } = [];
    public bool IsConditionsExpanded { get; set; }
    public static IReadOnlyList<DeviceAvailabilityTransition> DeviceTransitions => RoutineTriggerDraftViewModel.DeviceTransitions;
    public RoutineDeviceReference? SelectedAvailabilityDevice
    {
        get => CurrentTriggerDraft.SelectedAvailabilityDevice;
        set => CurrentTriggerDraft.SelectedAvailabilityDevice = value;
    }
    public DeviceAvailabilityTransition DeviceTransition
    {
        get => CurrentTriggerDraft.DeviceTransition;
        set => CurrentTriggerDraft.DeviceTransition = value;
    }
    public bool RequireDevice
    {
        get => _requireDevice;
        set { if (_requireDevice == value) return; _requireDevice = value; OnPropertyChanged(); }
    }
    public static IReadOnlyList<string> DeviceRequirementStates { get; } = ["Available", "Unavailable", "Current default"];
    public static IReadOnlyList<string> ApplicationRequirementStates { get; } = ["Running", "Not running"];
    public static IReadOnlyList<string> NetworkRequirementStates { get; } = ["Connected", "Not connected"];
    public int RequiredApplicationStateIndex
    {
        get => _requiredApplicationRunning ? 0 : 1;
        set { bool running = value != 1; if (_requiredApplicationRunning == running) return; _requiredApplicationRunning = running; OnPropertyChanged(); }
    }
    public int RequiredNetworkStateIndex
    {
        get => _requiredNetworkConnected ? 0 : 1;
        set { bool connected = value != 1; if (_requiredNetworkConnected == connected) return; _requiredNetworkConnected = connected; OnPropertyChanged(); }
    }
    public int RequiredDeviceStateIndex
    {
        get => _requiredDeviceDefault ? 2 : _requiredDeviceAvailable ? 0 : 1;
        set
        {
            bool available = value != 1, current = value == 2;
            if (_requiredDeviceAvailable == available && _requiredDeviceDefault == current) return;
            _requiredDeviceAvailable = available; _requiredDeviceDefault = current; OnPropertyChanged();
        }
    }
    public RoutineDeviceReference? SelectedRequiredDevice
    {
        get => _selectedRequiredDevice;
        set { if (_updatingDeviceChoices || _selectedRequiredDevice == value) return; _selectedRequiredDevice = value; OnPropertyChanged(); }
    }
    public string RequiredApplicationPath
    {
        get => _requiredApplicationPath;
        set
        {
            if (_requiredApplicationPath == value) return;
            _requiredApplicationPath = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResolvedRequiredApplicationName));
            OnPropertyChanged(nameof(HasResolvedRequiredApplication));
        }
    }
    private string? _selectedRequiredNetworkName;
    public string? SelectedRequiredNetworkName
    {
        get => _selectedRequiredNetworkName;
        set
        {
            if (_updatingNetworkChoices || _selectedRequiredNetworkName == value) return;
            _selectedRequiredNetworkName = value;
            OnPropertyChanged();
            if (!string.IsNullOrWhiteSpace(value)) RequiredNetworkName = value;
        }
    }
    private void SyncSelectedRequiredNetworkName()
    {
        string? match = AvailableNetworkNames.FirstOrDefault(name => string.Equals(name, RequiredNetworkName, StringComparison.Ordinal));
        if (_selectedRequiredNetworkName == match) return;
        _selectedRequiredNetworkName = match;
        OnPropertyChanged(nameof(SelectedRequiredNetworkName));
    }
    public string RequiredNetworkName
    {
        get => _requiredNetworkName;
        set { if (_updatingNetworkChoices || _requiredNetworkName == value) return; _requiredNetworkName = value ?? string.Empty; OnPropertyChanged(); SyncSelectedRequiredNetworkName(); }
    }

    private RoutineConditions BuildConditions() => new()
    {
        TimeWindow = RequireTimeWindow ? BuildTimeWindow() : null,
        Device = RequireDevice ? SelectedRequiredDevice ?? new RoutineDeviceReference() : null,
        DeviceAvailable = _requiredDeviceAvailable,
        DeviceMustBeDefault = _requiredDeviceDefault,
        RunningAppPath = AudioPilot.Helpers.RoutineTriggerPathHelper.NormalizeTriggerTarget(RequiredApplicationPath),
        ApplicationMustBeRunning = _requiredApplicationRunning,
        ConnectedNetwork = RequiredNetworkName,
        NetworkMustBeConnected = _requiredNetworkConnected,
    };

    private void RefreshRoutineDeviceChoices()
    {
        var devices = _sourceOutputDevices.Select(static device => new RoutineDeviceReference { Id = device.Id, StableId = device.StableId, Name = device.Name })
            .Concat(_sourceInputDevices.Select(static device => new RoutineDeviceReference { Id = device.Id, StableId = device.StableId, Name = device.Name, Playback = false })).ToList();
        foreach (var selected in new[] { _selectedTriggerDraft?.SelectedAvailabilityDevice, _activeTriggerDraft?.SelectedAvailabilityDevice, SelectedRequiredDevice })
            if (selected != null && !devices.Contains(selected)) devices.Add(selected);
        _updatingDeviceChoices = true;
        try
        {
            RoutineDeviceChoices = devices;
            RaiseRawPropertyChanged(nameof(RoutineDeviceChoices));
            RaiseRawPropertyChanged(nameof(SelectedRequiredDevice));
            _loadingTrigger = true;
            try
            {
                _selectedTriggerDraft?.NotifyDeviceChoicesUpdated();
                _activeTriggerDraft?.NotifyDeviceChoicesUpdated();
            }
            finally { _loadingTrigger = false; }
        }
        finally { _updatingDeviceChoices = false; }
    }
}

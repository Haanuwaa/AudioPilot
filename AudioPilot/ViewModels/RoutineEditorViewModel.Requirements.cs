using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    private RoutineDeviceReference? _selectedAvailabilityDevice;
    private RoutineDeviceReference? _selectedRequiredDevice;
    private DeviceAvailabilityTransition _deviceTransition;
    private bool _requireDevice;
    private bool _requiredDeviceAvailable = true;
    private string _requiredApplicationPath = string.Empty;
    private string _requiredNetworkName = string.Empty;
    private bool _updatingDeviceChoices;
    public IReadOnlyList<RoutineDeviceReference> RoutineDeviceChoices { get; private set; } = [];
    public bool IsConditionsExpanded { get; set; }
    public bool IsDeviceAvailabilityTriggerSelected => SelectedTriggerMode == TriggerModeLabels[7];
    public static IReadOnlyList<DeviceAvailabilityTransition> DeviceTransitions { get; } = Enum.GetValues<DeviceAvailabilityTransition>();
    public RoutineDeviceReference? SelectedAvailabilityDevice
    {
        get => _selectedAvailabilityDevice;
        set { if (_updatingDeviceChoices || _selectedAvailabilityDevice == value) return; _selectedAvailabilityDevice = value; OnPropertyChanged(); }
    }
    public DeviceAvailabilityTransition DeviceTransition
    {
        get => _deviceTransition;
        set { if (_deviceTransition == value) return; _deviceTransition = value; OnPropertyChanged(); }
    }
    public bool RequireDevice
    {
        get => _requireDevice;
        set { if (_requireDevice == value) return; _requireDevice = value; OnPropertyChanged(); }
    }
    public static IReadOnlyList<string> DeviceRequirementStates { get; } = ["Available", "Unavailable"];
    public int RequiredDeviceStateIndex
    {
        get => _requiredDeviceAvailable ? 0 : 1;
        set { bool available = value != 1; if (_requiredDeviceAvailable == available) return; _requiredDeviceAvailable = available; OnPropertyChanged(); }
    }
    public RoutineDeviceReference? SelectedRequiredDevice
    {
        get => _selectedRequiredDevice;
        set { if (_updatingDeviceChoices || _selectedRequiredDevice == value) return; _selectedRequiredDevice = value; OnPropertyChanged(); }
    }
    public string RequiredApplicationPath
    {
        get => _requiredApplicationPath;
        set { if (_requiredApplicationPath == value) return; _requiredApplicationPath = value ?? string.Empty; OnPropertyChanged(); }
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
        string? match = AvailableNetworkNames.FirstOrDefault(name => string.Equals(name, RequiredNetworkName, StringComparison.OrdinalIgnoreCase));
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
        RunningAppPath = AudioPilot.Helpers.RoutineTriggerPathHelper.NormalizeTriggerTarget(RequiredApplicationPath),
        ConnectedNetwork = RequiredNetworkName.Trim(),
    };

    private void RefreshRoutineDeviceChoices()
    {
        var devices = _sourceOutputDevices.Select(static device => new RoutineDeviceReference { Id = device.Id, StableId = device.StableId, Name = device.Name })
            .Concat(_sourceInputDevices.Select(static device => new RoutineDeviceReference { Id = device.Id, StableId = device.StableId, Name = device.Name, Playback = false })).ToList();
        foreach (var selected in new[] { SelectedAvailabilityDevice, SelectedRequiredDevice })
            if (selected != null && !devices.Contains(selected)) devices.Add(selected);
        _updatingDeviceChoices = true;
        try
        {
            RoutineDeviceChoices = devices;
            OnPropertyChanged(nameof(RoutineDeviceChoices));
            OnPropertyChanged(nameof(SelectedAvailabilityDevice));
            OnPropertyChanged(nameof(SelectedRequiredDevice));
        }
        finally { _updatingDeviceChoices = false; }
    }
}

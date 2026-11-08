using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioPilot.Localization;

namespace AudioPilot.Models;

public sealed record ApplicationRoutingDevice(string? Id, string Name, bool IsSelectable = true)
{
    internal string? LabelResource { get; init; }
    public object LabelText => LabelResource is { } key ? new LocalizedText(key) : Name;
}

/// <summary>The expanded mixer's current preference and available choices; audio work belongs to the mixer.</summary>
public sealed class ApplicationRoutingDetails : INotifyPropertyChanged
{
    private bool _busy = true;
    private object _statusText = new LocalizedText("Mixer_LoadingDevicePreference");
    private bool _hasError;
    private bool _reading;
    private bool _needsReadRetry;
    private bool _choosingDevice;
    private bool _applyingSnapshot;
    private ApplicationRoutingDevice? _selected;
    public IReadOnlyList<ApplicationRoutingDevice> Devices { get; private set; } = [];
    public bool IsBusy { get => _busy; internal set { if (_busy == value) return; _busy = value; Changed(); Changed(nameof(CanEdit)); Changed(nameof(CanRetry)); } }
    public bool IsReading { get => _reading; internal set { if (_reading == value) return; _reading = value; Changed(nameof(CanRetry)); } }
    public bool NeedsReadRetry { get => _needsReadRetry; internal set { if (_needsReadRetry == value) return; _needsReadRetry = value; Changed(); Changed(nameof(CanRetry)); } }
    public bool IsChoosingDevice
    {
        get => _choosingDevice;
        set { if (_choosingDevice == value) return; _choosingDevice = value; if (!value) ChoiceClosed?.Invoke(); }
    }
    public bool CanEdit => !IsBusy && Devices.Any(static device => device.IsSelectable);
    public bool CanRetry => NeedsReadRetry && !IsBusy && !IsReading;
    public string Status { get => LocalizationService.English.FormatValue(StatusText); internal set => StatusText = value; }
    public object StatusText { get => _statusText; internal set { if (Equals(_statusText, value)) return; _statusText = value; Changed(); Changed(nameof(Status)); } }
    public bool HasError { get => _hasError; internal set { if (_hasError == value) return; _hasError = value; Changed(); } }
    public string DeviceLabel { get; internal init; } = "Output device:";
    public LocalizedText DeviceLabelText => new(DeviceLabel == "Input device:" ? "Mixer_InputDevice" : "Mixer_OutputDevice");
    internal long ProcessStartedTicks { get; set; }
    internal long InteractionRevision { get; set; }
    internal object? WriteFailureStatus { get; set; }
    public ApplicationRoutingDevice? SelectedDevice
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value; Changed();
            if (!_applyingSnapshot && value?.IsSelectable == true && CanEdit) SelectionRequested?.Invoke(value);
        }
    }
    internal event Action<ApplicationRoutingDevice>? SelectionRequested;
    internal event Action? ChoiceClosed;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal bool SetDevices(IReadOnlyList<ApplicationRoutingDevice> devices, ApplicationRoutingDevice? selected)
    {
        bool devicesChanged = !Devices.SequenceEqual(devices);
        if (!devicesChanged && Equals(_selected, selected)) return false;
        _applyingSnapshot = true;
        try
        {
            if (devicesChanged) { Devices = devices; Changed(nameof(Devices)); }
            _selected = Devices.FirstOrDefault(device => Equals(device, selected));
            Changed(nameof(SelectedDevice)); Changed(nameof(CanEdit));
        }
        finally { _applyingSnapshot = false; }
        return true;
    }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

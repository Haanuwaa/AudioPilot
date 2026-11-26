using System.Windows.Input;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    public AudioTestingViewModel AudioTesting { get; }
    public ICommand SetDefaultOutputDeviceCommand { get; private set; } = null!;
    public ICommand SetDefaultInputDeviceCommand { get; private set; } = null!;
    public ICommand OpenWindowsDeviceSettingsCommand { get; private set; } = null!;
    public ICommand SwitchToSelectedDeviceCommand { get; private set; } = null!;
    public LocalizedText? OutputDeviceSwitchFeedback { get; private set; }
    public LocalizedText? InputDeviceSwitchFeedback { get; private set; }
    public LocalizedText? ActiveDeviceSwitchFeedback => SelectedSettingsTabIndex switch
    {
        0 => OutputDeviceSwitchFeedback,
        1 => InputDeviceSwitchFeedback,
        _ => null,
    };
    private long _directOutputRequestRevision;
    private long _directInputRequestRevision;

    private void InitializeAudioEndpointTesting()
    {
        SetDefaultOutputDeviceCommand = TrackCommand(new RelayCommand(parameter => SetDefaultDeviceFromContextAsync(parameter, output: true), CanSetDefaultDevice,
            ex => HandleAsyncCommandException("set-default-output", ex)), observeViewModel: true);
        SetDefaultInputDeviceCommand = TrackCommand(new RelayCommand(parameter => SetDefaultDeviceFromContextAsync(parameter, output: false), CanSetDefaultDevice,
            ex => HandleAsyncCommandException("set-default-input", ex)), observeViewModel: true);
        SwitchToSelectedDeviceCommand = TrackCommand(new RelayCommand(SwitchToSelectedDeviceAsync,
            () => ResolveSelectedSwitchDevice() != null,
            ex => HandleAsyncCommandException("switch-selected-device", ex)), observeViewModel: true);
        OpenWindowsDeviceSettingsCommand = TrackCommand(new RelayCommand(async parameter =>
        {
            if (parameter is CycleDevice device && !WindowsAudioSettings.OpenDevice(device.Id))
                await _dialogs.ShowErrorAsync("Windows audio settings could not be opened.");
        }, IsUsableCycleDevice, ex => HandleAsyncCommandException("open-windows-device-settings", ex)));
    }

    private static bool IsUsableCycleDevice(object? parameter) => parameter is CycleDevice { Id.Length: > 0 };
    private bool CanSetDefaultDevice(object? parameter) => !_isCleaningUp && !IsRuntimeShutdownRequested && !IsResettingSettings && IsUsableCycleDevice(parameter);

    private CycleDevice? ResolveSelectedSwitchDevice()
    {
        if (_isCleaningUp || IsRuntimeShutdownRequested || IsResettingSettings || !_hasDeviceInventorySnapshot || !IsDeviceTabsActive)
            return null;
        bool output = SelectedSettingsTabIndex == 0;
        var selection = output ? SelectedOutputCycleDevices : SelectedInputCycleDevices;
        var cycle = output ? OutputCycleDevices : InputCycleDevices;
        if (selection.Count != 1 || !cycle.Contains(selection[0])) return null;
        CycleDevice selected = selection[0];
        return PersistedAudioDeviceResolver.TryResolveMatch(selected, output ? _outputDevices : _inputDevices) != null ? selected : null;
    }

    private async Task SwitchToSelectedDeviceAsync()
    {
        if (ResolveSelectedSwitchDevice() is not { } selected) return;
        await SetDefaultDeviceFromContextAsync(selected, SelectedSettingsTabIndex == 0);
    }

    private async Task SetDefaultDeviceFromContextAsync(object? parameter, bool output)
    {
        if (!CanSetDefaultDevice(parameter) || parameter is not CycleDevice device) return;
        CycleDevice target = device.Clone();
        long revision = output ? Interlocked.Increment(ref _directOutputRequestRevision) : Interlocked.Increment(ref _directInputRequestRevision);
        bool IsCurrent() => revision == (output ? Volatile.Read(ref _directOutputRequestRevision) : Volatile.Read(ref _directInputRequestRevision));
        SetDeviceSwitchFeedback(output, null);
        DirectDeviceSwitchOutcome outcome = DirectDeviceSwitchOutcome.Failed;
        try
        {
            outcome = await TrackDirectDeviceSwitchAsync(async () =>
            {
                await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced);
                if (!IsCurrent() || _isCleaningUp || IsRuntimeShutdownRequested) return DirectDeviceSwitchOutcome.Canceled;
                Settings settings = CurrentSettings ?? new Settings();
                return output
                    ? await _switchCoordinator.SwitchOutputToDeviceAsync(target, false, false, Deafen, PreserveAudioLevels, BluetoothReconnectOptions.FromSettings(settings), ScheduleOutputPostSwitchRefresh)
                    : await _switchCoordinator.SwitchInputToDeviceAsync(target, PreserveAudioLevels, BluetoothReconnectOptions.FromSettings(settings));
            }, output, target.Name);
        }
        catch (Exception ex)
        {
            _logger.Error("AppViewModel", "manual-device-switch-failed", nameof(SetDefaultDeviceFromContextAsync), ex);
            if (IsCurrent() && !_isCleaningUp && !IsRuntimeShutdownRequested && _overlay.IsOverlayEnabled())
                _overlay.Show(OverlayDeviceKind.Error, output ? "Failed to switch output device" : "Failed to switch input device", target.Name);
        }

        if (IsCurrent() && !_isCleaningUp && !IsRuntimeShutdownRequested && !_overlay.IsOverlayEnabled()
            && outcome is DirectDeviceSwitchOutcome.Unavailable or DirectDeviceSwitchOutcome.Failed)
            SetDeviceSwitchFeedback(output, new(outcome == DirectDeviceSwitchOutcome.Unavailable ? "Device_SwitchUnavailable" : "Device_SwitchFailed", target.Name));
    }

    private void SetDeviceSwitchFeedback(bool output, LocalizedText? feedback)
    {
        if ((output ? OutputDeviceSwitchFeedback : InputDeviceSwitchFeedback) == feedback) return;
        if (output) OutputDeviceSwitchFeedback = feedback;
        else InputDeviceSwitchFeedback = feedback;
        OnPropertyChanged(output ? nameof(OutputDeviceSwitchFeedback) : nameof(InputDeviceSwitchFeedback));
        OnPropertyChanged(nameof(ActiveDeviceSwitchFeedback));
    }

    internal Task StopAudioEndpointTestAsync(AudioEndpointTestStopReason reason) => AudioTesting.StopAudioEndpointTestAsync(reason);
    internal Task ReconcileAudioEndpointTestDevicesAsync(CancellationToken cancellationToken) => AudioTesting.ReconcileAudioEndpointTestDevicesAsync(cancellationToken);
    private void RequestStopAudioEndpointTest(AudioEndpointTestStopReason reason) => AudioTesting.RequestStopAudioEndpointTest(reason);
}

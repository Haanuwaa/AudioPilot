using System.Windows.Input;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    public AudioTestingViewModel AudioTesting { get; }
    public ICommand SetDefaultOutputDeviceCommand { get; private set; } = null!;
    public ICommand SetDefaultInputDeviceCommand { get; private set; } = null!;

    private void InitializeAudioEndpointTesting()
    {
        SetDefaultOutputDeviceCommand = TrackCommand(new RelayCommand(SetDefaultOutputDeviceFromContextAsync, IsUsableCycleDevice, ex => HandleAsyncCommandException("set-default-output", ex)));
        SetDefaultInputDeviceCommand = TrackCommand(new RelayCommand(SetDefaultInputDeviceFromContextAsync, IsUsableCycleDevice, ex => HandleAsyncCommandException("set-default-input", ex)));
    }

    private static bool IsUsableCycleDevice(object? parameter) => parameter is CycleDevice { Id.Length: > 0 };

    private async Task SetDefaultOutputDeviceFromContextAsync(object? parameter)
    {
        if (parameter is not CycleDevice device) return;
        await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced);
        Settings settings = CurrentSettings ?? new Settings();
        await _switchCoordinator.SwitchOutputToDeviceAsync(device.Clone(), MuteMic, MuteSound, Deafen, PreserveAudioLevels, BluetoothReconnectOptions.FromSettings(settings), ScheduleOutputPostSwitchRefresh);
    }

    private async Task SetDefaultInputDeviceFromContextAsync(object? parameter)
    {
        if (parameter is not CycleDevice device) return;
        await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced);
        Settings settings = CurrentSettings ?? new Settings();
        await _switchCoordinator.SwitchInputToDeviceAsync(device.Clone(), PreserveAudioLevels, BluetoothReconnectOptions.FromSettings(settings));
    }

    internal Task StopAudioEndpointTestAsync(AudioEndpointTestStopReason reason) => AudioTesting.StopAudioEndpointTestAsync(reason);
    internal Task ReconcileAudioEndpointTestDevicesAsync(CancellationToken cancellationToken) => AudioTesting.ReconcileAudioEndpointTestDevicesAsync(cancellationToken);
    private void RequestStopAudioEndpointTest(AudioEndpointTestStopReason reason) => AudioTesting.RequestStopAudioEndpointTest(reason);
}

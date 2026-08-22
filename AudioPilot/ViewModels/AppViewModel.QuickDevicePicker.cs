using System.Windows.Interop;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private QuickDevicePickerWindow? _quickDevicePicker;
    private nint _quickDevicePickerHandle;

    internal void ToggleQuickDevicePicker()
    {
        _dispatcher.VerifyAccess();
        if (_isCleaningUp) return;
        if (_quickDevicePicker != null) { _quickDevicePicker.Dismiss(); return; }
        nint previousWindow = QuickDevicePickerWindow.GetForegroundWindow();
        var formFactors = new Dictionary<string, (bool Available, AudioEndpointFormFactor? Value)>(StringComparer.OrdinalIgnoreCase);
        var picker = new QuickDevicePickerWindow(token => ReadQuickDevicePickerAsync(formFactors, token), SwitchFromQuickDevicePickerAsync, previousWindow);
        _quickDevicePicker = picker;
        picker.SourceInitialized += (_, _) => Volatile.Write(ref _quickDevicePickerHandle, new WindowInteropHelper(picker).Handle);
        picker.Closed += (_, _) => { _quickDevicePicker = null; Volatile.Write(ref _quickDevicePickerHandle, 0); };
        try { picker.Show(); }
        catch { picker.Close(); _quickDevicePicker = null; Volatile.Write(ref _quickDevicePickerHandle, 0); throw; }
    }

    private Task<IReadOnlyList<QuickDevicePickerItem>> ReadQuickDevicePickerAsync(
        Dictionary<string, (bool Available, AudioEndpointFormFactor? Value)> formFactors, CancellationToken cancellationToken)
    {
        DeviceSwitchingSettings settings = DeviceSwitchingSettings.Clone(CurrentSettings?.DeviceSwitching);
        return ComThreadingHelper.RunOnCoreAudioThreadAsync<IReadOnlyList<QuickDevicePickerItem>>(() =>
        {
            var result = new List<QuickDevicePickerItem>();
            using var enumerator = new MMDeviceEnumerator();
            for (int flowIndex = 0; flowIndex < 2; flowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool output = flowIndex == 0;
                var configured = output ? settings.Output.CycleDevices : settings.Input.CycleDevices;
                if (configured.Count == 0) continue;
                var active = output ? _audio.GetActivePlaybackCycleEntries() : _audio.GetActiveCaptureCycleEntries();
                var roles = AudioDeviceService.NormalizeConfiguredRoles(output ? settings.Output.SwitchRoles : settings.Input.SwitchRoles,
                    [Role.Console, Role.Multimedia, Role.Communications]);
                var defaults = roles.Select(role => DeviceRoleSwitchEngine.ReadDefaultDeviceId(r =>
                {
                    using var device = enumerator.GetDefaultAudioEndpoint(output ? DataFlow.Render : DataFlow.Capture, r);
                    return device.ID;
                }, role)).ToArray();
                foreach (CycleDevice target in configured)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CycleDevice? resolved = PersistedAudioDeviceResolver.TryResolveMatch(target, active);
                    bool reconnect = settings.BluetoothReconnectEnabled && (!BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints || BluetoothReconnectCoordinator.IsLikelyBluetoothEndpoint(target));
                    string endpointId = resolved?.Id ?? target.Id;
                    bool available = resolved != null;
                    if (!formFactors.TryGetValue(endpointId, out var metadata) || metadata.Available != available)
                    {
                        AudioEndpointFormFactor? formFactor = null;
                        try
                        {
                            using var device = enumerator.GetDevice(endpointId);
                            var key = PropertyKeys.PKEY_AudioEndpoint_FormFactor;
                            if (device.Properties.Contains(key))
                                formFactor = ParsePickerFormFactor(device.Properties[key].Value);
                        }
                        catch (Exception)
                        {
                            // Optional driver metadata must not prevent switching a usable endpoint.
                        }
                        metadata = (available, formFactor);
                        formFactors[endpointId] = metadata;
                    }
                    result.Add(new(target.Id, resolved?.Name ?? target.Name, target.StableId, output,
                        available, resolved != null && defaults.All(id => string.Equals(id, resolved.Id, StringComparison.OrdinalIgnoreCase)), reconnect, metadata.Value));
                }
            }
            return result;
        }, cancellationToken);
    }

    internal static AudioEndpointFormFactor? ParsePickerFormFactor(object? value)
    {
        uint? number = value switch { uint unsigned => unsigned, int signed when signed >= 0 => (uint)signed, _ => null };
        return number <= (uint)AudioEndpointFormFactor.UnknownFormFactor ? (AudioEndpointFormFactor)number.Value : null;
    }

    private async Task<bool> SwitchFromQuickDevicePickerAsync(QuickDevicePickerItem item)
    {
        if (_isCleaningUp) return false;
        await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced);
        if (_isCleaningUp) return false;
        Settings settings = (CurrentSettings ?? new Settings()).Clone();
        bool deafen = Deafen;
        return await Task.Run(async () => item.Output
            ? await _switchCoordinator.SwitchOutputToDeviceAsync(item.ToDevice(), false, false, deafen,
                settings.DeviceSwitching.PreserveAudioLevels, BluetoothReconnectOptions.FromSettings(settings), ScheduleOutputPostSwitchRefresh)
            : await _switchCoordinator.SwitchInputToDeviceAsync(item.ToDevice(), settings.DeviceSwitching.PreserveAudioLevels,
                BluetoothReconnectOptions.FromSettings(settings)));
    }
}

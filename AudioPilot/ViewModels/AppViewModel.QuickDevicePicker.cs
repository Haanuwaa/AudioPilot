using System.Windows;
using System.Windows.Interop;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private QuickDevicePickerWindow? _quickDevicePicker;
    private nint _quickDevicePickerHandle;
    private bool _lastQuickDevicePickerOutput = true;
    private long _lastQuickDevicePickerClosedTicks;
    private bool? _pendingPickerShowAllDevices;
    private long _pickerPreferenceRevision;
    private TaskCompletionSource<bool>? _pickerPreferenceCompletion;

    internal void ShowQuickDevicePicker()
    {
        _dispatcher.VerifyAccess();
        if (IsRuntimeShutdownRequested || _isCleaningUp) return;
        if (_quickDevicePicker is { } picker)
        {
            if (picker.WindowState == WindowState.Minimized) picker.WindowState = WindowState.Normal;
            _ = picker.Activate(); return;
        }
        ToggleQuickDevicePicker();
    }

    internal void ToggleQuickDevicePickerFromTray()
    {
        _dispatcher.VerifyAccess();
        if (IsRuntimeShutdownRequested || _isCleaningUp) return;
        if (_quickDevicePicker == null && _lastQuickDevicePickerClosedTicks > 0
            && Environment.TickCount64 - _lastQuickDevicePickerClosedTicks < 250)
        {
            return;
        }
        ToggleQuickDevicePicker();
    }

    internal void ToggleQuickDevicePicker(Func<bool>? canPresent = null)
    {
        _dispatcher.VerifyAccess();
        if (IsRuntimeShutdownRequested || _isCleaningUp) return;
        if (_quickDevicePicker != null)
        {
            if (_quickDevicePicker.IsPinned) ShowQuickDevicePicker(); else _quickDevicePicker.Dismiss();
            return;
        }
        nint previousWindow = QuickDevicePickerWindow.GetForegroundWindow();
        bool mainWindowIsActive = Application.Current?.MainWindow is MainWindow
        { IsActive: true, IsVisible: true, WindowState: not WindowState.Minimized } mainWindow
            && ReferenceEquals(mainWindow.AppViewModel, this);
        bool output = ResolveQuickDevicePickerOutput(mainWindowIsActive, SelectedSettingsTabIndex, _lastQuickDevicePickerOutput);
        var formFactors = new Dictionary<string, (bool Available, AudioEndpointFormFactor? Value)>(StringComparer.OrdinalIgnoreCase);
        bool showAllDevices = _pendingPickerShowAllDevices ?? CurrentSettings?.DeviceSwitching.PickerShowAllAvailableDevices ?? false;
        var picker = new QuickDevicePickerWindow(token => ReadQuickDevicePickerAsync(formFactors, token), SwitchFromQuickDevicePickerAsync,
            previousWindow, output, showAllDevices, SavePickerViewPreferenceAsync, layouts: QuickPanelLayouts, deviceNotifications: _audio, canPresent: canPresent);
        _quickDevicePicker = picker;
        picker.DeviceSwitched += switchedOutput => _lastQuickDevicePickerOutput = switchedOutput;
        picker.SourceInitialized += (_, _) => Volatile.Write(ref _quickDevicePickerHandle, new WindowInteropHelper(picker).Handle);
        picker.Closed += (_, _) => { _lastQuickDevicePickerClosedTicks = Environment.TickCount64; _quickDevicePicker = null; Volatile.Write(ref _quickDevicePickerHandle, 0); };
        try { picker.Show(); }
        catch { picker.Close(); _quickDevicePicker = null; Volatile.Write(ref _quickDevicePickerHandle, 0); throw; }
    }

    internal static bool ResolveQuickDevicePickerOutput(bool mainWindowIsActive, int selectedTabIndex, bool lastOutput)
        => mainWindowIsActive && selectedTabIndex is 0 or 1 ? selectedTabIndex == 0 : lastOutput;

    private Task<bool> SavePickerViewPreferenceAsync(bool showAllDevices)
    {
        long revision = Interlocked.Increment(ref _pickerPreferenceRevision);
        _pendingPickerShowAllDevices = showAllDevices;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pickerPreferenceCompletion, completion)?.TrySetResult(true);
        if (!TryRunBackgroundWork(async token =>
        {
            try
            {
                await ExecuteSettingsWriteAsync(async () =>
                {
                    if (_isCleaningUp || token.IsCancellationRequested || revision != Volatile.Read(ref _pickerPreferenceRevision)) return;
                    Settings settings = GetCachedSettingsSnapshot() ?? await Task.Run(_settings.LoadSettings, token);
                    settings = settings.Clone();
                    if (settings.DeviceSwitching.PickerShowAllAvailableDevices == showAllDevices) return;
                    settings.DeviceSwitching.PickerShowAllAvailableDevices = showAllDevices;
                    await Task.Run(() => _settings.SaveSettings(settings), token);
                    lock (_settingsLock) _cachedSettings = settings;
                    UpdateLastSettingsWriteTime();
                });
                completion.TrySetResult(!token.IsCancellationRequested && !_isCleaningUp);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.TrySetResult(false); }
            catch (Exception ex)
            {
                _logger.Warning("QuickDevicePicker", "device-picker-preference-save-failed", nameof(SavePickerViewPreferenceAsync), ex);
                completion.TrySetResult(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _pickerPreferenceCompletion, null, completion);
                await InvokeOnDispatcherAsync(() =>
                {
                    if (revision == Volatile.Read(ref _pickerPreferenceRevision)) _pendingPickerShowAllDevices = null;
                });
            }
        }, nameof(SavePickerViewPreferenceAsync), onDiscarded: () => completion.TrySetResult(false)))
        {
            _pendingPickerShowAllDevices = null;
            Interlocked.CompareExchange(ref _pickerPreferenceCompletion, null, completion);
            completion.TrySetResult(false);
        }
        return completion.Task.WaitAsync(ShutdownToken);
    }

    private Task<QuickDevicePickerSnapshot> ReadQuickDevicePickerAsync(
        Dictionary<string, (bool Available, AudioEndpointFormFactor? Value)> formFactors, CancellationToken cancellationToken)
    {
        DeviceSwitchingSettings settings = DeviceSwitchingSettings.Clone(CurrentSettings?.DeviceSwitching);
        return ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            QuickDevicePickerSnapshot.Capture(output =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var enumerator = new MMDeviceEnumerator();
                var configured = output ? settings.Output.CycleDevices : settings.Input.CycleDevices;
                AudioDeviceInventory active = _audio.ReadActiveDeviceInventory(output);
                var roles = AudioDeviceService.NormalizeConfiguredRoles(output ? settings.Output.SwitchRoles : settings.Input.SwitchRoles,
                    [Role.Console, Role.Multimedia, Role.Communications]);
                var defaults = roles.Select(role =>
                {
                    string? id = DeviceRoleSwitchEngine.ReadDefaultDeviceId(r =>
                    {
                        using var device = enumerator.GetDefaultAudioEndpoint(output ? DataFlow.Render : DataFlow.Capture, r);
                        return device.ID;
                    }, role);
                    return (Role: role, DeviceId: id);
                }).ToArray();
                var items = QuickDevicePickerItem.BuildInventory(configured, active, output, defaults, target =>
                    settings.BluetoothReconnectEnabled && (!BluetoothReconnectRuntimeConfig.OnlyLikelyBluetoothEndpoints || BluetoothReconnectCoordinator.IsLikelyBluetoothEndpoint(target)));
                var result = new List<QuickDevicePickerItem>(items.Count);
                foreach (QuickDevicePickerItem item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string endpointId = item.Id;
                    if (item.AvailabilityUnknown)
                    {
                        result.Add(item with { FormFactor = formFactors.GetValueOrDefault(endpointId).Value });
                        continue;
                    }
                    bool available = item.Available;
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
                    result.Add(item with { FormFactor = metadata.Value });
                }
                return (result, !active.IsComplete);
            }, cancellationToken), cancellationToken);
    }

    internal static AudioEndpointFormFactor? ParsePickerFormFactor(object? value)
    {
        uint? number = value switch { uint unsigned => unsigned, int signed when signed >= 0 => (uint)signed, _ => null };
        return number <= (uint)AudioEndpointFormFactor.UnknownFormFactor ? (AudioEndpointFormFactor)number.Value : null;
    }

    private async Task<bool> SwitchFromQuickDevicePickerAsync(QuickDevicePickerItem item)
    {
        if (IsRuntimeShutdownRequested || _isCleaningUp) return false;
        await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced);
        if (IsRuntimeShutdownRequested || _isCleaningUp) return false;
        Settings settings = (CurrentSettings ?? new Settings()).Clone();
        bool deafen = Deafen;
        return await TrackDeviceSwitchAsync(async () => await Task.Run(async () => (item.Output
            ? await _switchCoordinator.SwitchOutputToDeviceAsync(item.ToDevice(), false, false, deafen,
                settings.DeviceSwitching.PreserveAudioLevels, BluetoothReconnectOptions.FromSettings(settings), ScheduleOutputPostSwitchRefresh)
            : await _switchCoordinator.SwitchInputToDeviceAsync(item.ToDevice(), settings.DeviceSwitching.PreserveAudioLevels,
                BluetoothReconnectOptions.FromSettings(settings))) == DirectDeviceSwitchOutcome.Succeeded), item.Output, reverse: false, source: "picker", item.Name);
    }
}

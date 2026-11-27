using System.ComponentModel;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels;

public partial class MixerViewModel
{
    private RoutingPanel? _mainRouting, _quickRouting;
    // A process can appear in both presentations or be re-added during a device change.
    private HashSet<uint>? _routingWrites;
    private CancellationTokenSource? _routingLifetimeCts;
    private ApplicationRoutingOperations? _routingOperations;
    private event Action<uint, RoutingPanel?, bool>? RoutingWriteStateChanged;
    internal Func<uint, bool, Func<Task<ApplicationRoutingChange>>, CancellationToken, Task<ApplicationRoutingChange>>? ManualRoutingScope { get; set; }
    private ApplicationRoutingOperations RoutingOperations => _routingOperations ??= new(_audio.ReadApplicationRoutingAsync, _audio.ChangeApplicationRoutingAsync);

    internal MixerViewModel(AudioDeviceService audio, Dispatcher dispatcher, AudioMixerMode mode, Logger logger, DeviceCacheHelper cache, ApplicationRoutingOperations routing)
        : this(audio, dispatcher, mode, logger, cache) => _routingOperations = routing;

    private void ToggleApplicationDetails(AudioSessionItem item) =>
        (_mainRouting ??= new(this, static (row, details) => row.RoutingDetails = details)).Toggle(item);

    internal Task RefreshApplicationRoutingAsync() => _mainRouting?.RefreshAsync() ?? Task.CompletedTask;
    internal void RefreshApplicationRoutingOnActivation() => _mainRouting?.RefreshOnActivation();

    internal RoutingPanel CreateQuickRoutingPanel(Action<AudioSessionItem, ApplicationRoutingDetails?> updateDetails)
    {
        _quickRouting?.Detach();
        return _quickRouting = new(this, updateDetails);
    }

    internal void ReleaseQuickRoutingPanel(RoutingPanel panel)
    {
        if (ReferenceEquals(_quickRouting, panel)) _quickRouting = null;
        panel.Detach();
    }

    internal sealed class RoutingPanel(MixerViewModel mixer, Action<AudioSessionItem, ApplicationRoutingDetails?> updateDetails)
    {
        private Action<AudioSessionItem, ApplicationRoutingDetails?>? _updateDetails = updateDetails;
        private AudioSessionItem? _item;
        private ApplicationRoutingDetails? _details;
        private CancellationTokenSource? _routingReadCts;
        private Task? _routingReadTask;
        private int _routingRefreshPosted;
        private int _routingInventoryStale;

        internal bool CanRetry => _details?.CanRetry == true;
        internal bool IsChoosingDevice => _details?.IsChoosingDevice == true;

        internal bool CloseChoice()
        {
            if (_details is not { IsChoosingDevice: true } details) return false;
            details.IsChoosingDevice = false;
            return true;
        }

        internal void Toggle(AudioSessionItem item)
        {
            if (_updateDetails == null || !item.IsApplication || !mixer.IsCurrentInteractionRow(item)) return;
            bool closing = ReferenceEquals(item, _item);
            Close();
            if (closing) return;
            _item = item;
            var details = _details = new()
            {
                DeviceLabel = mixer.CanGroupApplications ? "Output device:" : "Input device:",
                RetryCommand = new(() => { if (CanRetry) _ = RefreshAsync(); }),
            };
            UpdateActiveDeviceText(item, details);
            item.PropertyChanged += ItemPropertyChanged;
            details.SelectionRequested += SelectDevice;
            details.ChoiceClosed += ChoiceClosed;
            if (mixer._audio is { } audio)
            {
                audio.DeviceStateChanged += RoutingDevicesChanged;
                audio.ApplicationRoutingChanged += RoutingPreferenceChanged;
            }
            mixer.RoutingWriteStateChanged += OnWriteStateChanged;
            _updateDetails(item, details);
            if (mixer._routingWrites?.Contains(item.ProcessId!.Value) == true) details.StatusText = new LocalizedText("Mixer_ApplyingDevicePreference");
            else _routingReadTask = LoadApplicationRoutingAsync(item);
        }

        private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AudioSessionItem.ActiveDeviceUse) or nameof(AudioSessionItem.EndpointLabel)
                && ReferenceEquals(sender, _item) && _item is { } item && _details is { } details)
                UpdateActiveDeviceText(item, details);
        }

        private static void UpdateActiveDeviceText(AudioSessionItem item, ApplicationRoutingDetails details) =>
            details.ActiveDeviceText = item.ActiveDeviceUse switch
            {
                AudioSessionDeviceUse.Multiple => new("Mixer_ActiveOnMultipleDevices"),
                AudioSessionDeviceUse.One when !string.IsNullOrWhiteSpace(item.EndpointName) => new("Mixer_ActiveOnDevice", item.EndpointName),
                _ => null,
            };

        private void SelectDevice(ApplicationRoutingDevice selected)
        {
            if (_item is { } item && _details is { } details) _ = ChangeApplicationRoutingAsync(item, details, selected);
        }

        private void ChoiceClosed() => _ = RefreshRoutingIfStale();

        internal void Remove(AudioSessionItem item)
        {
            if (ReferenceEquals(_item, item)) Close();
        }

        internal void Detach()
        {
            Close();
            _updateDetails = null;
        }

        internal void Close()
        {
            if (_item is not { } item || _details is not { } details) return;
            _item = null;
            _details = null;
            details.InteractionRevision++;
            item.PropertyChanged -= ItemPropertyChanged;
            details.SelectionRequested -= SelectDevice;
            details.ChoiceClosed -= ChoiceClosed;
            details.RetryCommand?.Dispose();
            details.IsChoosingDevice = false;
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingReadCts);
            _routingReadTask = null;
            if (mixer._audio is { } audio)
            {
                audio.DeviceStateChanged -= RoutingDevicesChanged;
                audio.ApplicationRoutingChanged -= RoutingPreferenceChanged;
            }
            mixer.RoutingWriteStateChanged -= OnWriteStateChanged;
            Interlocked.Exchange(ref _routingInventoryStale, 0);
            _updateDetails?.Invoke(item, null);
        }

        private async Task LoadApplicationRoutingAsync(AudioSessionItem item, bool blockEditing = false)
        {
            Interlocked.Exchange(ref _routingInventoryStale, 0);
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingReadCts);
            _routingReadCts = new();
            CancellationToken token = _routingReadCts.Token;
            var details = _details;
            if (details == null) return;
            bool quiet = !blockEditing && details.Devices.Count > 0;
            bool recovering = details.HasError || details.NeedsReadRetry;
            Task<ApplicationRoutingInventory>? read = null;
            details.IsReading = true;
            if (!quiet) { details.IsBusy = true; details.HasError = false; details.StatusText = new LocalizedText("Mixer_LoadingDevicePreference"); }
            try
            {
                read = mixer.RoutingOperations.Read(item.ProcessId!.Value, mixer.CanGroupApplications, item.ProcessStartedTicks, token);
                var inventory = await read.WaitAsync(token);
                if (token.IsCancellationRequested || !ReferenceEquals(_details, details) || !mixer.IsCurrentInteractionRow(item)) return;
                if (details.IsChoosingDevice) { Interlocked.Exchange(ref _routingInventoryStale, 1); return; }
                item.ProcessStartedTicks = inventory.ProcessStartedTicks;
                details.ProcessStartedTicks = inventory.ProcessStartedTicks;
                bool changed = ApplyRoutingInventory(details, inventory.Preference, inventory.Devices);
                details.NeedsReadRetry = !inventory.Preference.Available || !inventory.IsComplete;
                if (details.WriteFailureStatus is { } failure)
                {
                    details.HasError = true; details.StatusText = failure;
                }
                else if (!inventory.IsComplete)
                {
                    details.HasError = false;
                    details.StatusText = new LocalizedText("Mixer_DeviceInventoryIncomplete");
                }
                else if (!quiet || changed || recovering)
                {
                    details.HasError = false;
                    details.StatusText = inventory.Preference.Available
                        ? inventory.Preference.Mixed ? new LocalizedText("Mixer_DifferentPreferencesAreSetForWindowsAudio") : new LocalizedText("Mixer_ChangesApplyImmediatelySomeAppsNeedTo")
                        : new LocalizedText("Mixer_TheCurrentPreferenceIsUnavailableChooseA");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (read != null)
                    _ = read.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                if (!mixer._disposed && !token.IsCancellationRequested && ReferenceEquals(_details, details))
                {
                    details.HasError = true; details.NeedsReadRetry = true; details.StatusText = new LocalizedText("Mixer_CouldNotReadThisAppSDevice");
                    details.SetDevices([], null);
                    mixer._logger.Warning("MixerViewModel", $"mixer-routing-read-failed | error={ex.GetType().Name}");
                }
            }
            finally
            {
                if (!mixer._disposed && ReferenceEquals(_details, details) && !token.IsCancellationRequested)
                {
                    details.IsReading = false;
                    if (!quiet) details.IsBusy = false;
                    if (Volatile.Read(ref _routingInventoryStale) != 0) PostRoutingRefresh();
                }
            }
        }

        private static bool ApplyRoutingInventory(ApplicationRoutingDetails details, ApplicationDevicePreference preference, IReadOnlyList<CycleDevice> devices)
        {
            details.DeviceInventory = devices;
            if (preference.Available && !preference.Mixed && details.SelectedDevice is { IsSelectable: true } current
                && string.Equals(current.Id, preference.DeviceId, StringComparison.OrdinalIgnoreCase)
                && details.Devices.Count == devices.Count + 1
                && details.Devices[0] is { Id: null, Name: "Windows default", IsSelectable: true })
            {
                bool unchanged = true;
                bool selectedFromChoices = ReferenceEquals(current, details.Devices[0]);
                for (int i = 0; i < devices.Count; i++)
                {
                    var choice = details.Devices[i + 1];
                    selectedFromChoices |= ReferenceEquals(current, choice);
                    if (string.Equals(choice.Id, devices[i].Id, StringComparison.Ordinal)
                        && string.Equals(choice.Name, devices[i].Name, StringComparison.Ordinal) && choice.IsSelectable) continue;
                    unchanged = false;
                    break;
                }
                if (unchanged && selectedFromChoices) return false;
            }
            bool complete = devices is not AudioDeviceInventory { IsComplete: false };
            var choices = new List<ApplicationRoutingDevice> { new(null, "Windows default") { LabelResource = "Mixer_WindowsDefault" } };
            choices.AddRange(devices.Select(device => new ApplicationRoutingDevice(device.Id, device.Name)));
            ApplicationRoutingDevice? selected = null;
            if (preference.Available && !preference.Mixed)
                foreach (var choice in choices)
                    if (string.Equals(choice.Id, preference.DeviceId, StringComparison.OrdinalIgnoreCase)) { selected = choice; break; }
            if (selected == null)
            {
                selected = new(preference.DeviceId, !preference.Available ? "Current preference unavailable"
                    : preference.Mixed ? "Different devices by audio role" : complete ? "Unavailable device" : "Device status unavailable", false)
                {
                    LabelResource = !preference.Available ? "Mixer_PreferenceUnavailable" : preference.Mixed ? "Mixer_MixedDevices"
                        : complete ? "Mixer_UnavailableDevice" : "Mixer_RoutingDeviceStatusUnavailable",
                };
                choices.Insert(0, selected);
            }
            return details.SetDevices(choices, selected);
        }

        private async Task ChangeApplicationRoutingAsync(AudioSessionItem item, ApplicationRoutingDetails details, ApplicationRoutingDevice selected)
        {
            if (mixer._disposed || details.IsBusy || !ReferenceEquals(_details, details) || !mixer.IsCurrentInteractionRow(item)) return;
            uint processId = item.ProcessId!.Value;
            mixer._routingWrites ??= [];
            if (!mixer._routingWrites.Add(processId)) return;
            details.InteractionRevision++;
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingReadCts);
            details.IsReading = false;
            details.IsBusy = true; details.HasError = false; details.NeedsReadRetry = false; details.StatusText = new LocalizedText("Mixer_ApplyingDevicePreference");
            details.WriteFailureStatus = null;
            var devices = details.DeviceInventory;
            bool refreshAfterWrite = false;
            mixer._routingLifetimeCts ??= new();
            CancellationToken token = mixer._routingLifetimeCts.Token;
            try
            {
                mixer.RoutingWriteStateChanged?.Invoke(processId, this, true);
                Task<ApplicationRoutingChange> Apply()
                {
                    token.ThrowIfCancellationRequested();
                    if (!mixer.IsCurrentInteractionRow(item)) throw new OperationCanceledException(token);
                    return mixer.RoutingOperations.Change(processId, mixer.CanGroupApplications, details.ProcessStartedTicks, selected.Id, token);
                }
                var result = mixer.ManualRoutingScope is { } scope ? await scope(processId, mixer.CanGroupApplications, Apply, token) : await Apply();
                if (mixer._disposed) return;
                if (result.OverridesRoutine) _ = mixer.RefreshAsync(interactive: true);
                if (!ReferenceEquals(_details, details)) return;
                bool confirmed = result.Preference.Available && !result.Preference.Mixed && string.Equals(result.Preference.DeviceId, selected.Id, StringComparison.OrdinalIgnoreCase);
                details.HasError = result.Result == ProcessAudioRoutingResult.Failed;
                details.NeedsReadRetry = !result.Preference.Available;
                refreshAfterWrite = details.HasError && result.MayHaveWritten == false;
                details.StatusText = result.TargetUnavailable ? new LocalizedText("Mixer_RoutingDeviceDisconnected")
                    : details.HasError ? new LocalizedText(result.MayHaveWritten == false ? "Mixer_RoutingRequestRejected" : "Mixer_CouldNotApplyAllAudioRolesCheck")
                    : confirmed ? result.Result == ProcessAudioRoutingResult.DeferredNoAudio ? new LocalizedText("Mixer_PreferenceSavedWaitingForAppAudio") : new LocalizedText("Mixer_DevicePreferenceSavedSomeAppsNeedTo")
                    : new LocalizedText("Mixer_TheChangeWasSentButItsPreference");
                details.WriteFailureStatus = details.HasError ? details.StatusText : null;
                ApplyRoutingInventory(details, result.Preference, devices);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || !mixer.IsCurrentInteractionRow(item)) { }
            catch (Exception ex)
            {
                if (!mixer._disposed && ReferenceEquals(_details, details))
                {
                    details.HasError = true; details.NeedsReadRetry = true; details.StatusText = new LocalizedText("Mixer_TheDeviceChangeCouldNotBeConfirmed");
                    ApplyRoutingInventory(details, default, devices);
                    mixer._logger.Warning("MixerViewModel", $"mixer-routing-change-failed | error={ex.GetType().Name}");
                }
            }
            finally
            {
                mixer._routingWrites.Remove(processId);
                bool ownsDetails = !mixer._disposed && ReferenceEquals(_details, details);
                if (ownsDetails && refreshAfterWrite)
                {
                    details.IsChoosingDevice = false;
                    _routingReadTask = LoadApplicationRoutingAsync(item, blockEditing: true);
                }
                else if (ownsDetails)
                {
                    details.IsBusy = false;
                    _ = RefreshRoutingIfStale();
                }
                mixer.RoutingWriteStateChanged?.Invoke(processId, ownsDetails ? this : null, false);
            }
        }

        private void OnWriteStateChanged(uint processId, RoutingPanel? source, bool writing)
        {
            if (mixer._disposed || ReferenceEquals(source, this) || _item is not { } item || item.ProcessId != processId || _details is not { } details) return;
            if (!writing)
            {
                _routingReadTask = LoadApplicationRoutingAsync(item, blockEditing: true);
                return;
            }

            details.InteractionRevision++;
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingReadCts);
            _routingReadTask = null;
            details.IsReading = false;
            details.IsBusy = true;
            details.IsChoosingDevice = false;
            details.HasError = false;
            details.WriteFailureStatus = null;
            details.StatusText = new LocalizedText("Mixer_ApplyingDevicePreference");
        }

        internal async Task RefreshAsync()
        {
            if (mixer._disposed || AppDispatcherHelper.IsDispatcherUnavailable(mixer._dispatcher)) return;
            if (!mixer._dispatcher.CheckAccess())
            {
                try { await mixer._dispatcher.InvokeAsync(RefreshAsync, DispatcherPriority.Background).Task.Unwrap(); }
                catch (InvalidOperationException) { }
                return;
            }
            if (_item == null || _details is not { } details) return;
            long interactionRevision = details.InteractionRevision;
            Interlocked.Exchange(ref _routingInventoryStale, 1);
            do { await RefreshRoutingIfStale(); }
            while (!mixer._disposed && ReferenceEquals(_details, details) && interactionRevision == details.InteractionRevision && !details.IsBusy && !details.IsChoosingDevice
                && (details.IsReading || Volatile.Read(ref _routingInventoryStale) != 0));
        }

        internal void RefreshOnActivation()
        {
            if (!mixer._disposed && _item != null) RoutingDevicesChanged();
        }

        private void RoutingPreferenceChanged(DataFlow flow)
        {
            if (flow == DataFlow.All || flow == (mixer.CanGroupApplications ? DataFlow.Render : DataFlow.Capture)) RoutingDevicesChanged();
        }

        private void RoutingDevicesChanged()
        {
            Interlocked.Exchange(ref _routingInventoryStale, 1);
            PostRoutingRefresh();
        }

        private void PostRoutingRefresh()
        {
            if (mixer._disposed || Interlocked.Exchange(ref _routingRefreshPosted, 1) != 0 || AppDispatcherHelper.IsDispatcherUnavailable(mixer._dispatcher)) return;
            try
            {
                mixer._dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _routingRefreshPosted, 0);
                    _ = RefreshRoutingIfStale();
                }, DispatcherPriority.Background);
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _routingRefreshPosted, 0); }
        }

        private Task RefreshRoutingIfStale()
        {
            if (!mixer._disposed && _item is { } item && _details is { IsBusy: false, IsReading: false, IsChoosingDevice: false } && Interlocked.Exchange(ref _routingInventoryStale, 0) != 0)
                _routingReadTask = LoadApplicationRoutingAsync(item);
            return _routingReadTask ?? Task.CompletedTask;
        }
    }
}

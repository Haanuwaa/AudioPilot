using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels;

public partial class MixerViewModel
{
    private HashSet<AudioSessionItem>? _selectedApplications;
    private Dictionary<AudioSessionItem, float>? _groupDrag;
    private AudioSessionItem? _groupAnchor;
    private bool _propagatingGroup;
    private bool _selectingApplications;
    private ListCollectionView? _groupedSessions;
    private HashSet<AudioSessionItem>? _routingWrites;
    private AudioSessionItem? _expandedApplication;
    private CancellationTokenSource? _routingReadCts;
    private Task? _routingReadTask;
    private CancellationTokenSource? _routingLifetimeCts;
    private ApplicationRoutingOperations? _routingOperations;
    private int _routingRefreshPosted;
    private int _routingInventoryStale;
    private RelayCommand? _applicationCommand, _expandApplicationCommand, _clearGroupCommand, _selectionModeCommand, _toggleAppSelectionCommand, _refreshRoutingCommand;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ICollectionView GroupedSessions
    {
        get
        {
            if (_groupedSessions == null)
            {
                _groupedSessions = new(Sessions);
                _groupedSessions.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AudioSessionItem.IsApplication)));
            }
            return _groupedSessions;
        }
    }
    public bool CanGroupApplications => _mixerMode == AudioMixerMode.Output;
    public int SelectedApplicationCount => _selectedApplications?.Count ?? 0;
    public bool HasSelectedApplications => SelectedApplicationCount > 0;
    public bool HasMultipleSelectedApplications => SelectedApplicationCount > 1;
    public string GroupSelectionText => $"· {SelectedApplicationCount} selected";
    public string SelectionModeText => IsSelectingApplications ? "Done" : "Select apps";
    public bool IsSelectingApplications { get => _selectingApplications; private set { _selectingApplications = value; InteractionChanged(); } }
    internal Func<uint, bool, Func<Task>, CancellationToken, Task>? ManualRoutingScope { get; set; }
    private ApplicationRoutingOperations RoutingOperations => _routingOperations ??= new(_audio.ReadApplicationRoutingAsync, _audio.ChangeApplicationRoutingAsync);

    internal MixerViewModel(AudioDeviceService audio, Dispatcher dispatcher, AudioMixerMode mode, Logger logger, DeviceCacheHelper cache, ApplicationRoutingOperations routing)
        : this(audio, dispatcher, mode, logger, cache) => _routingOperations = routing;

    public RelayCommand ApplicationCommand => _applicationCommand ??= new(parameter =>
    {
        if (parameter is not AudioSessionItem item) return;
        if (IsSelectingApplications) ToggleApplicationSelection(item);
        else if (item.IsApplication && IsCurrentInteractionRow(item))
        {
            ClearApplicationSelection();
            ToggleApplicationSelection(item);
        }
    });
    public RelayCommand ExpandApplicationCommand => _expandApplicationCommand ??= new(parameter => { if (parameter is AudioSessionItem item) ToggleApplicationDetails(item); });
    public RelayCommand ClearApplicationSelectionCommand => _clearGroupCommand ??= new(_ => ClearApplicationSelection());
    public RelayCommand ToggleApplicationSelectionCommand => _toggleAppSelectionCommand ??= new(parameter => { if (parameter is AudioSessionItem item) ToggleApplicationSelection(item); });
    public RelayCommand ToggleApplicationSelectionModeCommand => _selectionModeCommand ??= new(_ =>
    {
        if (CanGroupApplications && !_disposed)
        {
            IsSelectingApplications = !IsSelectingApplications;
            InteractionChanged(nameof(SelectionModeText));
        }
    });
    public RelayCommand RetryApplicationRoutingCommand => _refreshRoutingCommand ??= new(_ => { if (_expandedApplication?.RoutingDetails?.CanRetry == true) _ = RefreshApplicationRoutingAsync(); });

    public void ToggleApplicationSelection(AudioSessionItem item)
    {
        if (_disposed || !CanGroupApplications || !item.IsApplication || !IsCurrentInteractionRow(item)) return;
        EndGroupVolumeEdit();
        _selectedApplications ??= [];
        if (!_selectedApplications.Add(item)) _selectedApplications.Remove(item);
        item.IsGroupSelected = _selectedApplications.Contains(item);
        NotifySelectionChanged();
    }

    public void ClearApplicationSelection()
    {
        EndGroupVolumeEdit();
        if (_selectedApplications != null)
        {
            foreach (var item in _selectedApplications) item.IsGroupSelected = false;
            _selectedApplications.Clear();
        }
        IsSelectingApplications = false;
        NotifySelectionChanged();
    }

    public void BeginGroupVolumeEdit(AudioSessionItem anchor)
    {
        EndGroupVolumeEdit();
        if (!CanGroupApplications || !IsCurrentInteractionRow(anchor)) return;
        if (!anchor.IsGroupSelected) { ClearApplicationSelection(); return; }
        if (SelectedApplicationCount < 2) return;
        _groupAnchor = anchor;
        _groupDrag = _selectedApplications!.Where(IsCurrentInteractionRow).ToDictionary(item => item, item => item.Volume);
    }

    public void EndGroupVolumeEdit(AudioSessionItem? anchor = null)
    {
        if (anchor != null && !ReferenceEquals(anchor, _groupAnchor)) return;
        _groupDrag = null; _groupAnchor = null;
    }

    internal void PropagateGroupVolume(AudioSessionItem anchor)
    {
        if (_propagatingGroup || !ReferenceEquals(_groupAnchor, anchor) || _groupDrag == null || !_groupDrag.TryGetValue(anchor, out float baseline)) return;
        float delta = anchor.Volume - baseline;
        _propagatingGroup = true;
        try
        {
            foreach (var pair in _groupDrag)
                if (!ReferenceEquals(pair.Key, anchor) && IsCurrentInteractionRow(pair.Key)) pair.Key.Volume = Math.Clamp(pair.Value + delta, 0f, 100f);
        }
        finally { _propagatingGroup = false; }
    }

    private bool IsCurrentInteractionRow(AudioSessionItem item) => !_disposed &&
        _sessionsById.TryGetValue(GetSessionIdForItem(item), out var current) && ReferenceEquals(current, item);

    private static bool IsApplicationProcessCurrent(AudioSessionItem item)
    {
        if (!item.IsApplication || !item.ProcessStartedTicks.HasValue) return true;
        try
        {
            using var process = Process.GetProcessById(checked((int)item.ProcessId!.Value));
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == item.ProcessStartedTicks.Value;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException) { return false; }
    }

    private void NotifySelectionChanged()
    {
        InteractionChanged(nameof(SelectedApplicationCount)); InteractionChanged(nameof(HasSelectedApplications)); InteractionChanged(nameof(GroupSelectionText));
        InteractionChanged(nameof(SelectionModeText));
        InteractionChanged(nameof(HasMultipleSelectedApplications));
    }

    private void InteractionChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    private void ToggleApplicationDetails(AudioSessionItem item)
    {
        if (!item.IsApplication || !IsCurrentInteractionRow(item)) return;
        bool closing = ReferenceEquals(item, _expandedApplication);
        CloseApplicationDetails();
        if (closing) return;
        _expandedApplication = item;
        item.RoutingDetails = new() { DeviceLabel = CanGroupApplications ? "Output device:" : "Input device:" };
        var details = item.RoutingDetails;
        details.SelectionRequested += selected => _ = ChangeApplicationRoutingAsync(item, details, selected);
        details.ChoiceClosed += () => _ = RefreshRoutingIfStale();
        _audio.DeviceStateChanged += RoutingDevicesChanged;
        _audio.ApplicationRoutingChanged += RoutingPreferenceChanged;
        if (_routingWrites?.Contains(item) == true) details.Status = "Applying device preference...";
        else _routingReadTask = LoadApplicationRoutingAsync(item);
    }

    private async Task LoadApplicationRoutingAsync(AudioSessionItem item)
    {
        Interlocked.Exchange(ref _routingInventoryStale, 0);
        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingReadCts);
        _routingReadCts = new();
        CancellationToken token = _routingReadCts.Token;
        var details = item.RoutingDetails;
        if (details == null) return;
        bool quiet = details.Devices.Count > 0;
        bool recovering = details.HasError || details.NeedsReadRetry;
        Task<ApplicationRoutingInventory>? read = null;
        details.IsReading = true;
        if (!quiet) { details.IsBusy = true; details.HasError = false; details.Status = "Loading device preference..."; }
        try
        {
            read = RoutingOperations.Read(item.ProcessId!.Value, CanGroupApplications, item.ProcessStartedTicks, token);
            var inventory = await read.WaitAsync(token);
            if (token.IsCancellationRequested || !ReferenceEquals(item.RoutingDetails, details) || !IsCurrentInteractionRow(item)) return;
            if (details.IsChoosingDevice) { Interlocked.Exchange(ref _routingInventoryStale, 1); return; }
            item.ProcessStartedTicks = inventory.ProcessStartedTicks;
            details.ProcessStartedTicks = inventory.ProcessStartedTicks;
            bool changed = ApplyRoutingInventory(details, inventory.Preference, inventory.Devices);
            details.NeedsReadRetry = !inventory.Preference.Available;
            if (details.WriteFailureStatus is { } failure)
            {
                details.HasError = true; details.Status = failure;
            }
            else if (!quiet || changed || recovering)
            {
                details.HasError = false;
                details.Status = inventory.Preference.Available
                    ? inventory.Preference.Mixed ? "Different preferences are set for Windows audio roles." : "Changes apply immediately. Some apps need to restart audio."
                    : "The current preference is unavailable. Choose a device to set it explicitly.";
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
            if (!_disposed && !token.IsCancellationRequested && ReferenceEquals(item.RoutingDetails, details))
            {
                details.HasError = true; details.NeedsReadRetry = true; details.Status = "Could not read this app's device preference. Retry or use Refresh.";
                if (details.ProcessStartedTicks != 0)
                    ApplyRoutingInventory(details, default, [.. details.Devices.Where(device => device.IsSelectable && device.Id != null).Select(device => new CycleDevice { Id = device.Id!, Name = device.Name })]);
                _logger.Warning("MixerViewModel", $"mixer-routing-read-failed | error={ex.GetType().Name}");
            }
        }
        finally
        {
            if (!_disposed && ReferenceEquals(item.RoutingDetails, details) && !token.IsCancellationRequested)
            {
                details.IsReading = false;
                if (!quiet) details.IsBusy = false;
                if (Volatile.Read(ref _routingInventoryStale) != 0) PostRoutingRefresh();
            }
        }
    }

    private static bool ApplyRoutingInventory(ApplicationRoutingDetails details, ApplicationDevicePreference preference, IReadOnlyList<CycleDevice> devices)
    {
        if (preference.Available && !preference.Mixed && details.SelectedDevice is { IsSelectable: true } current
            && string.Equals(current.Id, preference.DeviceId, StringComparison.Ordinal)
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
        var choices = new List<ApplicationRoutingDevice> { new(null, "Windows default") };
        choices.AddRange(devices.Select(device => new ApplicationRoutingDevice(device.Id, device.Name)));
        ApplicationRoutingDevice? selected = null;
        if (preference.Available && !preference.Mixed)
            foreach (var choice in choices)
                if (string.Equals(choice.Id, preference.DeviceId, StringComparison.Ordinal)) { selected = choice; break; }
        if (selected == null)
        {
            selected = new(preference.DeviceId, !preference.Available ? "Current preference unavailable" : preference.Mixed ? "Different devices by audio role" : "Unavailable device", false);
            choices.Insert(0, selected);
        }
        return details.SetDevices(choices, selected);
    }

    private async Task ChangeApplicationRoutingAsync(AudioSessionItem item, ApplicationRoutingDetails details, ApplicationRoutingDevice selected)
    {
        if (_disposed || details.IsBusy || !IsCurrentInteractionRow(item)) return;
        details.InteractionRevision++;
        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingReadCts);
        details.IsReading = false;
        _routingWrites ??= [];
        if (!_routingWrites.Add(item)) return;
        details.IsBusy = true; details.HasError = false; details.NeedsReadRetry = false; details.Status = "Applying device preference...";
        details.WriteFailureStatus = null;
        var devices = details.Devices.Where(device => device.IsSelectable && device.Id != null).Select(device => new CycleDevice { Id = device.Id!, Name = device.Name }).ToArray();
        _routingLifetimeCts ??= new();
        CancellationToken token = _routingLifetimeCts.Token;
        try
        {
            ApplicationRoutingChange result = default;
            async Task Apply()
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentInteractionRow(item)) throw new OperationCanceledException(token);
                result = await RoutingOperations.Change(item.ProcessId!.Value, CanGroupApplications, details.ProcessStartedTicks, selected.Id, token);
            }
            if (ManualRoutingScope is { } scope) await scope(item.ProcessId!.Value, CanGroupApplications, Apply, token);
            else await Apply();
            if (_disposed) return;
            _ = RefreshAsync(interactive: true);
            if (!ReferenceEquals(item.RoutingDetails, details)) return;
            bool confirmed = result.Preference.Available && !result.Preference.Mixed && string.Equals(result.Preference.DeviceId, selected.Id, StringComparison.Ordinal);
            details.HasError = result.Result == ProcessAudioRoutingResult.Failed;
            details.NeedsReadRetry = !result.Preference.Available;
            details.Status = details.HasError ? "Could not apply all audio roles. Check the preference before retrying."
                : confirmed ? result.Result == ProcessAudioRoutingResult.DeferredNoAudio ? "Preference saved; waiting for app audio." : "Device preference saved. Some apps need to restart audio."
                : "The change was sent, but its preference could not be confirmed.";
            details.WriteFailureStatus = details.HasError ? details.Status : null;
            ApplyRoutingInventory(details, result.Preference, devices);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || !IsCurrentInteractionRow(item)) { }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(item.RoutingDetails, details))
            {
                details.HasError = true; details.NeedsReadRetry = true; details.Status = "The device change could not be confirmed. Retry the read or use Refresh.";
                ApplyRoutingInventory(details, default, devices);
                _logger.Warning("MixerViewModel", $"mixer-routing-change-failed | error={ex.GetType().Name}");
            }
        }
        finally
        {
            _routingWrites.Remove(item);
            if (!_disposed && ReferenceEquals(item.RoutingDetails, details))
            {
                details.IsBusy = false;
                _ = RefreshRoutingIfStale();
            }
            else if (!_disposed && ReferenceEquals(_expandedApplication, item)) _routingReadTask = LoadApplicationRoutingAsync(item);
        }
    }

    internal async Task RefreshApplicationRoutingAsync()
    {
        if (_disposed || AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher)) return;
        if (!_dispatcher.CheckAccess())
        {
            try { await _dispatcher.InvokeAsync(RefreshApplicationRoutingAsync, DispatcherPriority.Background).Task.Unwrap(); }
            catch (InvalidOperationException) { }
            return;
        }
        if (_expandedApplication is not { RoutingDetails: { } details } item) return;
        long interactionRevision = details.InteractionRevision;
        Interlocked.Exchange(ref _routingInventoryStale, 1);
        do { await RefreshRoutingIfStale(); }
        while (!_disposed && ReferenceEquals(item.RoutingDetails, details) && interactionRevision == details.InteractionRevision && !details.IsBusy && !details.IsChoosingDevice
            && (details.IsReading || Volatile.Read(ref _routingInventoryStale) != 0));
    }

    internal void RefreshApplicationRoutingOnActivation()
    {
        if (!_disposed && _expandedApplication != null) RoutingDevicesChanged();
    }

    private void RoutingPreferenceChanged(DataFlow flow)
    {
        if (flow == DataFlow.All || flow == (CanGroupApplications ? DataFlow.Render : DataFlow.Capture)) RoutingDevicesChanged();
    }

    private void RoutingDevicesChanged()
    {
        Interlocked.Exchange(ref _routingInventoryStale, 1);
        PostRoutingRefresh();
    }

    private void PostRoutingRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _routingRefreshPosted, 1) != 0 || AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher)) return;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _routingRefreshPosted, 0);
                _ = RefreshRoutingIfStale();
            }, DispatcherPriority.Background);
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _routingRefreshPosted, 0); }
    }

    private Task RefreshRoutingIfStale()
    {
        if (!_disposed && _expandedApplication is { RoutingDetails: { IsBusy: false, IsReading: false, IsChoosingDevice: false } } item && Interlocked.Exchange(ref _routingInventoryStale, 0) != 0)
            _routingReadTask = LoadApplicationRoutingAsync(item);
        return _routingReadTask ?? Task.CompletedTask;
    }

    private void CloseApplicationDetails()
    {
        if (_expandedApplication?.RoutingDetails is { } details) details.InteractionRevision++;
        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingReadCts);
        _routingReadTask = null;
        if (_expandedApplication is not { } item) return;
        _audio.DeviceStateChanged -= RoutingDevicesChanged;
        _audio.ApplicationRoutingChanged -= RoutingPreferenceChanged;
        item.RoutingDetails = null;
        _expandedApplication = null;
        Interlocked.Exchange(ref _routingInventoryStale, 0);
    }

    private void RemoveInteractionRow(AudioSessionItem item)
    {
        if (_selectedApplications?.Remove(item) == true)
        {
            item.IsGroupSelected = false;
            if (ReferenceEquals(item, _groupAnchor) || _selectedApplications.Count < 2) EndGroupVolumeEdit();
            else _groupDrag?.Remove(item);
            NotifySelectionChanged();
        }
        if (ReferenceEquals(item, _expandedApplication)) CloseApplicationDetails();
    }

    public void ResetMixerInteractions(bool shutdown = false)
    {
        ClearApplicationSelection();
        CloseApplicationDetails();
        if (shutdown) AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingLifetimeCts);
    }
}

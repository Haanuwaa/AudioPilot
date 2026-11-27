using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioPilot.Localization;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels;

internal enum AudioEndpointFormFactor
{
    RemoteNetworkDevice, Speakers, LineLevel, Headphones, Microphone, Headset, Handset,
    UnknownDigitalPassthrough, Spdif, DigitalAudioDisplayDevice, UnknownFormFactor
}

internal sealed record QuickDevicePickerItem(string Id, string Name, string? StableId, bool Output, bool Available, bool Current, bool CanReconnect,
    AudioEndpointFormFactor? FormFactor = null, bool Configured = true, bool AvailabilityUnknown = false,
    (Role First, Role? Second)? PartialDefaultRoles = null)
{
    public LocalizedText StatusText => (AvailabilityUnknown, Current, PartialDefaultRoles) switch
    {
        (true, _, _) => new("Manage_StatusUnavailable"),
        (_, true, _) => new("Picker_Current"),
        (_, _, (var first, Role second)) => new("Picker_CurrentForTwoRoles", RoleLabel(first), RoleLabel(second)),
        (_, _, (var first, null)) => new("Picker_CurrentForRole", RoleLabel(first)),
        _ => new(Available ? "Picker_Available" : CanReconnect ? "Picker_Reconnect" : "Picker_Disconnected"),
    };

    private static LocalizedText RoleLabel(Role role) => new(role switch
    {
        Role.Multimedia => "Picker_MultimediaRole",
        Role.Communications => "Ui_Communications",
        Role.Console => "Ui_Console",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    });

    public string Status => LocalizationService.Default.Format(StatusText);
    public string Glyph => FormFactor switch
    {
        AudioEndpointFormFactor.Speakers => "\uE7F5",
        AudioEndpointFormFactor.Headphones => "\uE7F6",
        AudioEndpointFormFactor.Headset => "\uE95B",
        AudioEndpointFormFactor.Microphone => "\uE720",
        AudioEndpointFormFactor.DigitalAudioDisplayDevice => "\uE7F4",
        AudioEndpointFormFactor.Handset => "\uE717",
        AudioEndpointFormFactor.RemoteNetworkDevice => "\uE968",
        AudioEndpointFormFactor.LineLevel or AudioEndpointFormFactor.Spdif or AudioEndpointFormFactor.UnknownDigitalPassthrough => "\uE703",
        _ => Output ? "\uE767" : "\uE720",
    };
    public LocalizedText DeviceTypeText => new(FormFactor switch
    {
        AudioEndpointFormFactor.Speakers => "DeviceType_Speakers",
        AudioEndpointFormFactor.Headphones => "DeviceType_Headphones",
        AudioEndpointFormFactor.Headset => "DeviceType_Headset",
        AudioEndpointFormFactor.Microphone => "DeviceType_Microphone",
        AudioEndpointFormFactor.DigitalAudioDisplayDevice => "DeviceType_Display",
        AudioEndpointFormFactor.Handset => "DeviceType_Handset",
        AudioEndpointFormFactor.RemoteNetworkDevice => "DeviceType_Network",
        AudioEndpointFormFactor.LineLevel => "DeviceType_Line",
        AudioEndpointFormFactor.Spdif => "DeviceType_Spdif",
        AudioEndpointFormFactor.UnknownDigitalPassthrough => "DeviceType_Digital",
        _ => Output ? "DeviceType_Output" : "DeviceType_Input",
    });
    public LocalizedText HelpMessage => new("Picker_Help", DeviceTypeText, StatusText);
    public LocalizedText HoverMessage => new("Picker_Hover", Name, HelpMessage);
    public string DeviceType => LocalizationService.Default.Format(DeviceTypeText);
    public string HelpText => LocalizationService.Default.Format(HelpMessage);
    public string HoverText => LocalizationService.Default.Format(HoverMessage);
    public bool CanSelect => !AvailabilityUnknown && (Available || CanReconnect);
    public CycleDevice ToDevice() => new() { Id = Id, Name = Name, StableId = StableId };

    internal static List<QuickDevicePickerItem> BuildInventory(IReadOnlyList<CycleDevice> configured,
        IReadOnlyList<CycleDevice> available, bool output, IReadOnlyList<(Role Role, string? DeviceId)> defaults, Func<CycleDevice, bool> canReconnect)
    {
        List<QuickDevicePickerItem> items = [];
        Dictionary<string, CycleDevice> activeById = IndexAvailableDevices(available);
        bool incomplete = available is AudioDeviceInventory { IsComplete: false };
        CycleDevice?[] matches = MatchConfiguredDevices(configured, activeById, exactOnly: incomplete);
        HashSet<string> included = [with(StringComparer.OrdinalIgnoreCase)];
        for (int index = 0; index < configured.Count; index++)
        {
            CycleDevice target = configured[index];
            if (string.IsNullOrWhiteSpace(target.Id)) continue;
            CycleDevice? resolved = matches[index];
            CycleDevice device = resolved ?? target;
            if (!included.Add(device.Id)) continue;
            items.Add(CreateItem(device, resolved != null, configured: true,
                reconnect: resolved == null && !incomplete && canReconnect(target), availabilityUnknown: resolved == null && incomplete));
        }
        foreach (CycleDevice device in activeById.Values.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!included.Add(device.Id)) continue;
            items.Add(CreateItem(device, available: true, configured: false));
        }
        return items;

        QuickDevicePickerItem CreateItem(CycleDevice device, bool available, bool configured, bool reconnect = false, bool availabilityUnknown = false)
        {
            Role? first = null;
            Role? second = null;
            int matched = 0;
            if (available)
            {
                foreach (var (role, deviceId) in defaults)
                {
                    if (!string.Equals(deviceId, device.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    // A partial match can contain at most two of the three Windows roles.
                    if (first == null) first = role;
                    else second ??= role;
                    matched++;
                }
            }
            bool current = matched > 0 && matched == defaults.Count;
            return new(device.Id, device.Name, device.StableId, output, available, current, reconnect,
                Configured: configured, AvailabilityUnknown: availabilityUnknown,
                PartialDefaultRoles: !current && first is Role firstRole ? (firstRole, second) : null);
        }
    }

    internal static Dictionary<string, CycleDevice> IndexAvailableDevices(IReadOnlyList<CycleDevice> available)
    {
        Dictionary<string, CycleDevice> activeById = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (CycleDevice device in available)
            if (!string.IsNullOrWhiteSpace(device.Id)) activeById.TryAdd(device.Id, device);
        return activeById;
    }

    internal static CycleDevice?[] MatchConfiguredDevices(IReadOnlyList<CycleDevice> configured, Dictionary<string, CycleDevice> activeById, bool exactOnly = false)
    {
        CycleDevice?[] matches = new CycleDevice?[configured.Count];
        HashSet<string> reserved = [with(StringComparer.OrdinalIgnoreCase)];
        for (int index = 0; index < configured.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(configured[index].Id) && activeById.TryGetValue(configured[index].Id, out CycleDevice? exact))
            {
                matches[index] = exact;
                reserved.Add(exact.Id);
            }
        }
        if (exactOnly) return matches;

        CycleDevice[] active = [.. activeById.Values];
        for (int index = 0; index < configured.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(configured[index].Id) && matches[index] == null
                && PersistedAudioDeviceResolver.TryFindByStableId(configured[index].StableId, active) is { } stable)
            {
                matches[index] = stable;
                reserved.Add(stable.Id);
            }
        }

        string[]? normalizedActive = null;
        List<(CycleDevice Candidate, string MatchReason)>? candidates = null;
        for (int index = 0; index < configured.Count; index++)
        {
            CycleDevice target = configured[index];
            if (string.IsNullOrWhiteSpace(target.Id) || matches[index] != null) continue;
            bool ambiguousStableId = !string.IsNullOrEmpty(target.StableId)
                && active.Any(device => string.Equals(device.StableId, target.StableId, StringComparison.Ordinal));
            if (ambiguousStableId || string.IsNullOrWhiteSpace(target.Name)) continue;

            normalizedActive ??= [.. active.Select(static device => BluetoothReconnectService.NormalizeForMatch(device.Name))];
            string normalizedTarget = BluetoothReconnectService.NormalizeForMatch(target.Name);
            candidates ??= [];
            candidates.Clear();
            for (int candidateIndex = 0; candidateIndex < active.Length; candidateIndex++)
            {
                CycleDevice device = active[candidateIndex];
                if (reserved.Contains(device.Id)) continue;
                string reason = BluetoothReconnectService.ResolveMatchReason(device.Name, target.Name, normalizedTarget, normalizedActive[candidateIndex]);
                if (reason.Length != 0) candidates.Add((device, reason));
            }
            if (!BluetoothReconnectService.TrySelectBestUniqueMatch(candidates, out var best) || best is not { } match) continue;
            matches[index] = match.Candidate;
            reserved.Add(match.Candidate.Id);
        }
        return matches;
    }
}

internal sealed record QuickDevicePickerSnapshot(IReadOnlyList<QuickDevicePickerItem> Items,
    Exception? OutputError = null, Exception? InputError = null, bool OutputIncomplete = false, bool InputIncomplete = false)
{
    /// <summary>Reads each direction independently, without treating failed discovery as an empty device list.</summary>
    internal static QuickDevicePickerSnapshot Capture(Func<bool, (IReadOnlyList<QuickDevicePickerItem> Items, bool Incomplete)> read, CancellationToken token)
    {
        List<QuickDevicePickerItem> items = [];
        Exception? outputError = null;
        Exception? inputError = null;
        bool outputIncomplete = false, inputIncomplete = false;
        for (int index = 0; index < 2; index++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var (direction, incomplete) = read(index == 0);
                items.AddRange(direction);
                if (index == 0) outputIncomplete = incomplete;
                else inputIncomplete = incomplete;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                if (index == 0) outputError = ex;
                else inputError = ex;
            }
        }
        token.ThrowIfCancellationRequested();
        return new(items, outputError, inputError, outputIncomplete, inputIncomplete);
    }
}

internal sealed class QuickDevicePickerViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<QuickDevicePickerItem> _snapshot = [];
    private string _search = string.Empty;
    private bool _output = true;
    private bool _busy;
    private LocalizedText _message = new("Picker_Loading");
    private LocalizedText? _switchMessage;
    private QuickDevicePickerItem? _selected;
    private bool _hasSnapshot;
    private bool _showAllDevices;
    private bool _outputFailed;
    private bool _inputFailed;
    private bool _outputIncomplete;
    private bool _inputIncomplete;

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<QuickDevicePickerItem> Items { get; private set; } = [];
    public QuickDevicePickerItem? Selected { get => _selected; set { _selected = value; Changed(); Changed(nameof(CanSwitch)); } }
    public string Search { get => _search; set { if (_search == value) return; _search = value; Changed(); Filter(); } }
    public bool Output
    {
        get => _output;
        set
        {
            if (_output == value) return;
            _output = value;
            _selected = null;
            Changed();
            Filter();
            SetSwitchMessage(null);
        }
    }
    public bool Busy { get => _busy; set { _busy = value; Changed(); Changed(nameof(CanSwitch)); Changed(nameof(CanBrowse)); } }
    public bool CanBrowse => !Busy;
    public bool ShowAllDevices { get => _showAllDevices; set { if (_showAllDevices == value) return; _showAllDevices = value; Changed(); Filter(); } }
    public int DeviceCount => _snapshot.Count(IsInScope);
    public bool CanSwitch => !Busy && !DiscoveryFailed && Selected?.CanSelect == true;
    public LocalizedText MessageText => _switchMessage ?? _message;
    public string Message => LocalizationService.Default.Format(MessageText);

    internal void SetMessage(string key)
    {
        _message = new(key);
        if (_switchMessage == null) NotifyMessageChanged();
    }

    internal void SetSwitchMessage(string? key)
    {
        if (_switchMessage?.Key == key) return;
        _switchMessage = key == null ? null : new(key);
        NotifyMessageChanged();
    }

    private void NotifyMessageChanged()
    {
        Changed(nameof(MessageText));
        Changed(nameof(Message));
    }

    internal void Update(IReadOnlyList<QuickDevicePickerItem> snapshot, bool force = false, bool outputFailed = false, bool inputFailed = false,
        bool outputIncomplete = false, bool inputIncomplete = false)
    {
        if (!force && _hasSnapshot && _outputFailed == outputFailed && _inputFailed == inputFailed
            && _outputIncomplete == outputIncomplete && _inputIncomplete == inputIncomplete && _snapshot.SequenceEqual(snapshot)) return;
        _hasSnapshot = true;
        _outputFailed = outputFailed;
        _inputFailed = inputFailed;
        _outputIncomplete = outputIncomplete;
        _inputIncomplete = inputIncomplete;
        _snapshot = snapshot;
        Filter();
    }

    internal void Move(int direction)
    {
        if (Items.Count == 0 || Busy) return;
        int index = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i] == Selected) { index = i; break; }
        }
        Selected = Items[Math.Clamp(index + direction, 0, Items.Count - 1)];
    }

    private void Filter()
    {
        string? selectedId = Selected?.Id;
        string search = Search.Trim();
        QuickDevicePickerItem[] nextItems = [.. _snapshot.Where(item => IsInScope(item) && item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))];
        if (!Items.SequenceEqual(nextItems))
        {
            Items = nextItems;
            Changed(nameof(Items));
        }
        QuickDevicePickerItem? selection = Items.FirstOrDefault(item => item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        if (selection == null && Items.Count > 0)
        {
            int currentIndex = -1;
            if (search.Length == 0)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i].Current) { currentIndex = i; break; }
                }
            }
            for (int offset = 1; offset <= Items.Count; offset++)
            {
                QuickDevicePickerItem candidate = Items[(currentIndex + offset) % Items.Count];
                if (candidate.CanSelect) { selection = candidate; break; }
            }
            selection ??= Items[0];
        }
        Changed(nameof(DeviceCount));
        Selected = selection;
        if (_hasSnapshot && !Busy) SetMessage(DiscoveryFailed ? (Output ? "Picker_ReadOutputFailed" : "Picker_ReadInputFailed")
            : (Output ? _outputIncomplete : _inputIncomplete) ? "Picker_PartialRead"
            : Items.Count > 0 ? "Common_Empty" : DeviceCount > 0
            ? "Picker_NoMatch" : ShowAllDevices ? (Output ? "Picker_NoOutput" : "Picker_NoInput")
            : Output ? "Picker_ConfigureOutput" : "Picker_ConfigureInput");
    }

    private bool DiscoveryFailed => Output ? _outputFailed : _inputFailed;

    private bool IsInScope(QuickDevicePickerItem item) => !DiscoveryFailed && item.Output == Output && (ShowAllDevices ? item.Available : item.Configured);

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

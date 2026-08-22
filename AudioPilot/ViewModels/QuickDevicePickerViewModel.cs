using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal enum AudioEndpointFormFactor
{
    RemoteNetworkDevice, Speakers, LineLevel, Headphones, Microphone, Headset, Handset,
    UnknownDigitalPassthrough, Spdif, DigitalAudioDisplayDevice, UnknownFormFactor
}

internal sealed record QuickDevicePickerItem(string Id, string Name, string? StableId, bool Output, bool Available, bool Current, bool CanReconnect,
    AudioEndpointFormFactor? FormFactor = null, bool Configured = true)
{
    public string Status => Current ? "✓ Current" : Available ? "Available" : CanReconnect ? "Connect and switch" : "Disconnected";
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
    public string DeviceType => FormFactor switch
    {
        AudioEndpointFormFactor.Speakers => "Speakers",
        AudioEndpointFormFactor.Headphones => "Headphones",
        AudioEndpointFormFactor.Headset => "Headset",
        AudioEndpointFormFactor.Microphone => "Microphone",
        AudioEndpointFormFactor.DigitalAudioDisplayDevice => "Display audio",
        AudioEndpointFormFactor.Handset => "Handset",
        AudioEndpointFormFactor.RemoteNetworkDevice => "Network audio",
        AudioEndpointFormFactor.LineLevel => "Line audio",
        AudioEndpointFormFactor.Spdif => "S/PDIF audio",
        AudioEndpointFormFactor.UnknownDigitalPassthrough => "Digital audio",
        _ => Output ? "Audio output" : "Audio input",
    };
    public string HelpText => $"{DeviceType} · {Status}";
    public string HoverText => $"{Name}\n{HelpText}";
    public bool CanSelect => Available || CanReconnect;
    public CycleDevice ToDevice() => new() { Id = Id, Name = Name, StableId = StableId };

    internal static List<QuickDevicePickerItem> BuildInventory(IReadOnlyList<CycleDevice> configured,
        IReadOnlyList<CycleDevice> available, bool output, IReadOnlyList<string?> defaults, Func<CycleDevice, bool> canReconnect)
    {
        List<QuickDevicePickerItem> items = [];
        Dictionary<string, CycleDevice> activeById = new(StringComparer.OrdinalIgnoreCase);
        foreach (CycleDevice device in available)
            if (!string.IsNullOrWhiteSpace(device.Id)) activeById.TryAdd(device.Id, device);
        CycleDevice[] active = [.. activeById.Values];
        CycleDevice?[] matches = new CycleDevice?[configured.Count];
        HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < configured.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(configured[index].Id) && activeById.TryGetValue(configured[index].Id, out CycleDevice? exact))
            {
                matches[index] = exact;
                reserved.Add(exact.Id);
            }
        }
        for (int index = 0; index < configured.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(configured[index].Id) && matches[index] == null
                && PersistedAudioDeviceResolver.TryFindByStableId(configured[index].StableId, active) is { } stable)
            {
                matches[index] = stable;
                reserved.Add(stable.Id);
            }
        }
        HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < configured.Count; index++)
        {
            CycleDevice target = configured[index];
            if (string.IsNullOrWhiteSpace(target.Id)) continue;
            CycleDevice? resolved = matches[index];
            bool ambiguousStableId = resolved == null && !string.IsNullOrEmpty(target.StableId)
                && active.Any(device => string.Equals(device.StableId, target.StableId, StringComparison.Ordinal));
            if (resolved == null && !ambiguousStableId)
            {
                resolved = PersistedAudioDeviceResolver.TryResolveUniqueBestNameMatch(target.Name,
                    [.. active.Where(device => !reserved.Contains(device.Id))]);
                if (resolved != null) reserved.Add(resolved.Id);
            }
            CycleDevice device = resolved ?? target;
            if (string.IsNullOrWhiteSpace(device.Id) || !included.Add(device.Id)) continue;
            items.Add(new(device.Id, device.Name, device.StableId, output, resolved != null,
                resolved != null && defaults.Count > 0 && defaults.All(id => string.Equals(id, device.Id, StringComparison.OrdinalIgnoreCase)),
                resolved == null && canReconnect(target)));
        }
        foreach (CycleDevice device in active.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(device.Id) || !included.Add(device.Id)) continue;
            items.Add(new(device.Id, device.Name, device.StableId, output, true,
                defaults.Count > 0 && defaults.All(id => string.Equals(id, device.Id, StringComparison.OrdinalIgnoreCase)), false, Configured: false));
        }
        return items;
    }
}

internal sealed record QuickDevicePickerSnapshot(IReadOnlyList<QuickDevicePickerItem> Items,
    Exception? OutputError = null, Exception? InputError = null)
{
    /// <summary>Reads each direction independently, without treating failed discovery as an empty device list.</summary>
    internal static QuickDevicePickerSnapshot Capture(Func<bool, IReadOnlyList<QuickDevicePickerItem>> read, CancellationToken token)
    {
        List<QuickDevicePickerItem> items = [];
        Exception? outputError = null;
        Exception? inputError = null;
        for (int index = 0; index < 2; index++)
        {
            token.ThrowIfCancellationRequested();
            try { items.AddRange(read(index == 0)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                if (index == 0) outputError = ex;
                else inputError = ex;
            }
        }
        token.ThrowIfCancellationRequested();
        return new(items, outputError, inputError);
    }
}

internal sealed class QuickDevicePickerViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<QuickDevicePickerItem> _snapshot = [];
    private string _search = string.Empty;
    private bool _output = true;
    private bool _busy;
    private string _message = "Loading devices…";
    private QuickDevicePickerItem? _selected;
    private bool _hasSnapshot;
    private bool _showAllDevices;
    private bool _outputFailed;
    private bool _inputFailed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<QuickDevicePickerItem> Items { get; private set; } = [];
    public QuickDevicePickerItem? Selected { get => _selected; set { _selected = value; Changed(); Changed(nameof(CanSwitch)); } }
    public string Search { get => _search; set { if (_search == value) return; _search = value; Changed(); Filter(); } }
    public bool Output { get => _output; set { if (_output == value) return; _output = value; _selected = null; Changed(); Filter(); } }
    public bool Busy { get => _busy; set { _busy = value; Changed(); Changed(nameof(CanSwitch)); Changed(nameof(CanBrowse)); } }
    public bool CanBrowse => !Busy;
    public bool ShowAllDevices { get => _showAllDevices; set { if (_showAllDevices == value) return; _showAllDevices = value; Changed(); Filter(); } }
    public int DeviceCount => _snapshot.Count(IsInScope);
    public bool CanSwitch => !Busy && !DiscoveryFailed && Selected?.CanSelect == true;
    public string Message { get => _message; set { _message = value; Changed(); } }

    internal void Update(IReadOnlyList<QuickDevicePickerItem> snapshot, bool force = false, bool outputFailed = false, bool inputFailed = false)
    {
        if (!force && _hasSnapshot && _outputFailed == outputFailed && _inputFailed == inputFailed && _snapshot.SequenceEqual(snapshot)) return;
        _hasSnapshot = true;
        _outputFailed = outputFailed;
        _inputFailed = inputFailed;
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
        Items = [.. _snapshot.Where(item => IsInScope(item) && item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))];
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
        Changed(nameof(Items));
        Changed(nameof(DeviceCount));
        Selected = selection;
        if (_hasSnapshot && !Busy) Message = DiscoveryFailed ? $"Could not read {(Output ? "output" : "input")} devices. Retrying…"
            : Items.Count > 0 ? string.Empty : DeviceCount > 0
            ? "No matching devices." : ShowAllDevices ? $"No available {(Output ? "output" : "input")} devices."
            : $"Add devices to your {(Output ? "Output" : "Input")} switch order or enable Show all available devices.";
    }

    private bool DiscoveryFailed => Output ? _outputFailed : _inputFailed;

    private bool IsInScope(QuickDevicePickerItem item) => !DiscoveryFailed && item.Output == Output && (ShowAllDevices ? item.Available : item.Configured);

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

internal enum AudioEndpointFormFactor
{
    RemoteNetworkDevice, Speakers, LineLevel, Headphones, Microphone, Headset, Handset,
    UnknownDigitalPassthrough, Spdif, DigitalAudioDisplayDevice, UnknownFormFactor
}

internal sealed record QuickDevicePickerItem(string Id, string Name, string? StableId, bool Output, bool Available, bool Current, bool CanReconnect,
    AudioEndpointFormFactor? FormFactor = null)
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

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<QuickDevicePickerItem> Items { get; private set; } = [];
    public QuickDevicePickerItem? Selected { get => _selected; set { _selected = value; Changed(); Changed(nameof(CanSwitch)); } }
    public string Search { get => _search; set { if (_search == value) return; _search = value; Changed(); Filter(); } }
    public bool Output { get => _output; set { if (_output == value) return; _output = value; _selected = null; Changed(); Filter(); } }
    public bool Busy { get => _busy; set { _busy = value; Changed(); Changed(nameof(CanSwitch)); Changed(nameof(CanBrowse)); } }
    public bool CanBrowse => !Busy;
    public bool CanSwitch => !Busy && Selected?.CanSelect == true;
    public string Message { get => _message; set { _message = value; Changed(); } }

    internal void Update(IReadOnlyList<QuickDevicePickerItem> snapshot, bool force = false)
    {
        if (!force && _hasSnapshot && _snapshot.SequenceEqual(snapshot)) return;
        _hasSnapshot = true;
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
        Items = [.. _snapshot.Where(item => item.Output == Output && item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))];
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
        Selected = selection;
        if (!Busy) Message = Items.Count > 0 ? string.Empty : _snapshot.Any(item => item.Output == Output)
            ? "No matching devices." : $"Add devices to your {(Output ? "Output" : "Input")} switch order first.";
    }

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

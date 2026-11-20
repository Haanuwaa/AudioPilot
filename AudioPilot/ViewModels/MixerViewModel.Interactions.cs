using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class MixerViewModel
{
    private HashSet<AudioSessionItem>? _selectedApplications;
    private Dictionary<AudioSessionItem, float>? _groupDrag;
    private AudioSessionItem? _groupAnchor;
    private bool _propagatingGroup;
    private bool _selectingApplications;
    private ListCollectionView? _groupedSessions;
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
    public LocalizedText SelectionModeLabel => new(IsSelectingApplications ? "Mixer_Done" : "Mixer_SelectApps");
    public bool IsSelectingApplications { get => _selectingApplications; private set { _selectingApplications = value; InteractionChanged(); } }

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
            InteractionChanged(nameof(SelectionModeLabel));
        }
    });
    public RelayCommand RetryApplicationRoutingCommand => _refreshRoutingCommand ??= new(_ => { if (_mainRouting?.CanRetry == true) _ = RefreshApplicationRoutingAsync(); });

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
        _groupDrag = CaptureGroupVolumes(_selectedApplications!);
    }

    internal Dictionary<AudioSessionItem, float> CaptureGroupVolumes(HashSet<AudioSessionItem> selection) =>
        selection.Where(IsCurrentInteractionRow).ToDictionary(item => item, item => item.Volume);

    public void EndGroupVolumeEdit(AudioSessionItem? anchor = null)
    {
        if (anchor != null && !ReferenceEquals(anchor, _groupAnchor)) return;
        _groupDrag = null; _groupAnchor = null;
    }

    internal void PropagateGroupVolume(AudioSessionItem anchor)
    {
        if (_propagatingGroup || !ReferenceEquals(_groupAnchor, anchor) || _groupDrag == null) return;
        _propagatingGroup = true;
        try { ApplyGroupVolumeOffsets(anchor, _groupDrag); }
        finally { _propagatingGroup = false; }
    }

    private void ApplyGroupVolumeOffsets(AudioSessionItem anchor, Dictionary<AudioSessionItem, float> originalVolumes)
    {
        if (!originalVolumes.TryGetValue(anchor, out float baseline)) return;
        float delta = anchor.Volume - baseline;
        foreach (var (item, volume) in originalVolumes)
            if (!ReferenceEquals(item, anchor) && IsCurrentInteractionRow(item)) item.Volume = Math.Clamp(volume + delta, 0f, 100f);
    }

    internal bool IsCurrentInteractionRow(AudioSessionItem item) => !_disposed &&
        _sessionsById.TryGetValue(GetSessionIdForItem(item), out var current) && ReferenceEquals(current, item);

    private bool CanUnmuteMixerRow(AudioSessionItem item) =>
        !item.IsEndpoint || _audio?.ResolveSwitchMuteOverride is not { } resolve || resolve(item.IsMaster, false) == false;

    internal void SetPresentationVolume(AudioSessionItem item, float volume, Dictionary<AudioSessionItem, float>? originalVolumes = null)
    {
        if (!float.IsFinite(volume) || !IsCurrentInteractionRow(item)) return;
        volume = Math.Clamp(volume, 0, 100);
        if (Math.Abs(item.Volume - volume) < 0.01f) return;
        if (CanGroupApplications && item.IsApplication && originalVolumes is { Count: > 1 } && originalVolumes.ContainsKey(item))
        {
            ApplyGroupVolumeEdit(item, originalVolumes, volume);
            return;
        }
        bool wasPropagating = _propagatingGroup;
        _propagatingGroup = true;
        try { item.Volume = volume; }
        finally { _propagatingGroup = wasPropagating; }
    }

    internal void TogglePresentationMute(AudioSessionItem item, HashSet<AudioSessionItem>? selection = null)
    {
        if (!IsCurrentInteractionRow(item)) return;
        if (CanGroupApplications && item.IsApplication && selection is { Count: > 1 } && selection.Contains(item))
            SetGroupMute(!item.IsMuted, selection);
        else if (!item.IsMuted || CanUnmuteMixerRow(item)) item.IsMuted = !item.IsMuted;
    }

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
        InteractionChanged(nameof(SelectionModeLabel));
        InteractionChanged(nameof(HasMultipleSelectedApplications));
    }

    private void InteractionChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    private void RemoveInteractionRow(AudioSessionItem item)
    {
        if (_selectedApplications?.Remove(item) == true)
        {
            item.IsGroupSelected = false;
            if (ReferenceEquals(item, _groupAnchor) || _selectedApplications.Count < 2) EndGroupVolumeEdit();
            else _groupDrag?.Remove(item);
            NotifySelectionChanged();
        }
        _mainRouting?.Remove(item);
        _quickRouting?.Remove(item);
    }

    public void ResetMixerInteractions(bool shutdown = false)
    {
        ClearApplicationSelection();
        _mainRouting?.Close();
        if (!shutdown) return;
        _quickRouting?.Close();
        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _routingLifetimeCts);
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Controls;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Metering;

namespace AudioPilot.ViewModels;

internal sealed class QuickMixerPage : INotifyPropertyChanged, IDisposable
{
    private readonly MixerViewModel _mixer;
    private readonly Dispatcher _dispatcher;
    private HashSet<AudioSessionItem>? _selected;
    private AudioSessionItem? _groupAnchor;
    private Dictionary<AudioSessionItem, float>? _groupVolumes;
    private MixerViewModel.RoutingPanel? _routing;
    private bool _queued;
    private bool _disposed;
    public ObservableCollection<QuickMixerRow> Rows { get; } = [];
    public bool CanGroupApplications => _mixer.CanGroupApplications;
    public bool IsSelectingApplications { get; private set; }
    public bool HasSelectedApplications => SelectedApplicationCount > 0;
    public int SelectedApplicationCount => _selected?.Count ?? 0;
    public LocalizedText SelectionModeLabel => new(IsSelectingApplications ? "Mixer_Done" : "Mixer_SelectApps");
    public event PropertyChangedEventHandler? PropertyChanged;
    internal AudioMixerMode Mode => _mixer.MixerMode;
    internal double ScrollOffset { get; set; }
    internal bool WasAtBottom { get; set; }
    internal string? SelectedIdentity { get; set; }

    internal QuickMixerPage(MixerViewModel mixer, Dispatcher dispatcher)
    {
        _mixer = mixer;
        _dispatcher = dispatcher;
        mixer.Sessions.CollectionChanged += SessionsChanged;
        Synchronize();
    }

    private void SessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed || _queued) return;
        _queued = true;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => { if (!_disposed && _queued) Synchronize(); }));
    }

    internal void Synchronize()
    {
        _queued = false;
        var desired = _mixer.Sessions.Where(row => !row.IsEndpoint || (Mode == AudioMixerMode.Output ? row.IsMaster : row.IsMic))
            .OrderByDescending(row => row.IsEndpoint).ToArray();
        var retained = desired.ToHashSet();
        for (int index = Rows.Count - 1; index >= 0; index--)
        {
            var row = Rows[index];
            if (retained.Contains(row.Source)) continue;
            _routing?.Remove(row.Source);
            if (_selected?.Remove(row.Source) == true)
            {
                if (ReferenceEquals(_groupAnchor, row.Source) || _selected.Count < 2) EndVolumeEdit();
                else _groupVolumes?.Remove(row.Source);
                NotifySelection();
            }
            row.Dispose();
            Rows.RemoveAt(index);
        }
        var bySource = Rows.ToDictionary(row => row.Source);
        for (int index = 0; index < desired.Length; index++)
        {
            if (!bySource.TryGetValue(desired[index], out var row)) Rows.Insert(index, new(_mixer, desired[index], page: this));
            else if (!ReferenceEquals(Rows[index], row)) Rows.Move(Rows.IndexOf(row), index);
        }
    }

    internal void ToggleSelectionMode()
    {
        if (!_disposed && CanGroupApplications) SetSelectionMode(!IsSelectingApplications);
    }

    private void SetSelectionMode(bool value)
    {
        if (IsSelectingApplications == value) return;
        IsSelectingApplications = value;
        PropertyChanged?.Invoke(this, new(nameof(IsSelectingApplications)));
        PropertyChanged?.Invoke(this, new(nameof(SelectionModeLabel)));
    }

    private void NotifySelection()
    {
        PropertyChanged?.Invoke(this, new(nameof(SelectedApplicationCount)));
        PropertyChanged?.Invoke(this, new(nameof(HasSelectedApplications)));
    }

    internal void SelectApplication(QuickMixerRow row, bool extend)
    {
        if (_disposed || !CanGroupApplications || !row.Source.IsApplication || !Rows.Contains(row)
            || !_mixer.IsCurrentInteractionRow(row.Source)) return;
        EndVolumeEdit();
        _selected ??= [];
        if (!extend && !IsSelectingApplications) ClearSelection();
        if (!_selected.Add(row.Source)) _selected.Remove(row.Source);
        row.IsGroupSelected = _selected.Contains(row.Source);
        NotifySelection();
    }

    internal bool ClearSelection()
    {
        EndVolumeEdit();
        bool changed = HasSelectedApplications || IsSelectingApplications;
        if (_selected is { Count: > 0 })
        {
            foreach (var row in Rows)
                if (row.IsGroupSelected) row.IsGroupSelected = false;
            _selected.Clear();
            NotifySelection();
        }
        SetSelectionMode(false);
        return changed;
    }

    internal void BeginVolumeEdit(AudioSessionItem anchor)
    {
        EndVolumeEdit();
        if (!CanGroupApplications || _disposed) return;
        if (_selected?.Contains(anchor) != true) { ClearSelection(); return; }
        if (_selected.Count < 2) return;
        _groupAnchor = anchor;
        _groupVolumes = _mixer.CaptureGroupVolumes(_selected);
    }

    internal void EndVolumeEdit(AudioSessionItem? anchor = null)
    {
        if (anchor != null && !ReferenceEquals(anchor, _groupAnchor)) return;
        _groupAnchor = null;
        _groupVolumes = null;
    }

    internal void SetVolume(AudioSessionItem item, float volume) =>
        _mixer.SetPresentationVolume(item, volume, ReferenceEquals(_groupAnchor, item) ? _groupVolumes : null);

    internal void ToggleMute(AudioSessionItem item) => _mixer.TogglePresentationMute(item, _selected);

    internal void ToggleRouting(QuickMixerRow row)
    {
        if (_disposed || !row.Source.IsApplication || !Rows.Contains(row)) return;
        (_routing ??= _mixer.CreateQuickRoutingPanel(SetRoutingDetails)).Toggle(row.Source);
    }

    private void SetRoutingDetails(AudioSessionItem item, ApplicationRoutingDetails? details)
    {
        foreach (var row in Rows)
        {
            if (!ReferenceEquals(row.Source, item)) continue;
            row.RoutingDetails = details;
            break;
        }
    }

    internal bool IsChoosingDevice => _routing?.IsChoosingDevice == true;
    internal bool CloseDeviceChoice() => _routing?.CloseChoice() == true;
    internal void RefreshRoutingOnActivation() => _routing?.RefreshOnActivation();
    internal Task RefreshRoutingAsync() => _routing?.RefreshAsync() ?? Task.CompletedTask;

    internal void SuspendInteractions()
    {
        ClearSelection();
        _routing?.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mixer.Sessions.CollectionChanged -= SessionsChanged;
        ClearSelection();
        if (_routing is { } routing) _mixer.ReleaseQuickRoutingPanel(routing);
        _routing = null;
        foreach (var row in Rows) row.Dispose();
        Rows.Clear();
        SelectedIdentity = null;
    }
}

internal sealed class QuickMixerRow : INotifyPropertyChanged, IDisposable
{
    private readonly MixerViewModel _mixer;
    private readonly QuickMixerPage? _page;
    private bool _disposed;
    private bool _selected;
    private ApplicationRoutingDetails? _routingDetails;
    private RelayCommand? _selectApplication, _toggleSelection, _toggleRouting;
    private bool _meterAvailable;
    private double _level, _peak;
    private float _rawPeak;
    private float _postVolumePeak;
    private float? _pendingVolume;
    private long _pendingVolumeUntil, _volumeEditRevision;
    private bool? _pendingMute;
    private long _pendingMuteUntil, _muteEditRevision;
    private bool _hasSessionControls;
    private bool _sessionUnmuted, _sessionAudible;
    private int _sessionControlHash;
    private ImageSource? _icon;
    private CancellationTokenSource? _iconWait;
    private long _iconRetryAt;
    private readonly TimeProvider _timeProvider;
    private readonly AudioPeakEnvelope _peakEnvelope;
    public AudioSessionItem Source { get; }
    public bool IsGroupSelected { get => _selected; internal set { if (_selected == value) return; _selected = value; Changed(nameof(IsGroupSelected)); } }
    public bool CanGroupApplications => _page?.CanGroupApplications == true;
    public ApplicationRoutingDetails? RoutingDetails
    {
        get => _routingDetails;
        internal set { if (ReferenceEquals(_routingDetails, value)) return; _routingDetails = value; Changed(nameof(RoutingDetails)); Changed(nameof(IsRoutingExpanded)); }
    }
    public bool IsRoutingExpanded => _routingDetails != null;
    public RelayCommand SelectApplicationCommand => _selectApplication ??= new(() => SelectApplication(extend: false));
    public RelayCommand ToggleApplicationSelectionCommand => _toggleSelection ??= new(() => SelectApplication(extend: true));
    public RelayCommand ToggleRoutingCommand => _toggleRouting ??= new(() => { if (!_disposed) _page?.ToggleRouting(this); });
    internal string Identity => Source.IsEndpoint ? Source.IsMic ? "input" : "output" : Source.IsSystemSounds ? "system"
        : $"{Source.ProcessId}:{Source.ProcessStartedTicks?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Source.SessionInstanceId}";
    public float Volume
    {
        get => Source.Volume;
        set
        {
            if (_disposed) return;
            if (_page != null) _page.SetVolume(Source, value);
            else _mixer.SetPresentationVolume(Source, value);
        }
    }
    public RelayCommand ToggleMuteCommand { get; }
    public Geometry MuteGlyph => Source.IsMic
        ? Source.IsMuted ? AppMenuGlyphs.InputMuted : AppMenuGlyphs.Input
        : Source.IsMuted ? AppMenuGlyphs.OutputMuted : AppMenuGlyphs.Output;
    public Geometry FallbackGlyph => Source.IsApplication ? AppMenuGlyphs.Window : Source.IsMic ? AppMenuGlyphs.Input : AppMenuGlyphs.Output;
    public LocalizedText MuteLabel => new(Source.IsMuted ? "QuickMixer_Unmute" : "QuickMixer_Mute", Source.DisplayLabel);
    public bool MeterAvailable => _meterAvailable;
    public double Level => _level;
    public double Peak => _peak;
    public ImageSource? Icon { get => _icon; internal set { if (_disposed || ReferenceEquals(_icon, value)) return; _icon = value; Changed(nameof(Icon)); Changed(nameof(HasIcon)); } }
    public bool HasIcon => Icon != null;
    internal bool IconRequested { get; set; }
    internal bool CanRequestIcon => !_disposed && !IconRequested && _timeProvider.GetTimestamp() >= _iconRetryAt;
    internal void DeferIconRequest()
    {
        IconRequested = false;
        _iconRetryAt = _timeProvider.GetTimestamp() + _timeProvider.TimestampFrequency / 4;
    }

    internal async Task WaitForIconAsync(Task<ImageSource?> pending)
    {
        if (pending.IsCompletedSuccessfully)
        {
            Icon = pending.Result;
            return;
        }

        // The provider can finish and cache this icon even when the row stops waiting.
        using var wait = new CancellationTokenSource();
        _iconWait = wait;
        try
        {
            var icon = await pending.WaitAsync(wait.Token);
            if (ReferenceEquals(_iconWait, wait)) Icon = icon;
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_iconWait, wait)) _iconWait = null; }
    }

    internal void ReleaseIcon()
    {
        var wait = _iconWait;
        _iconWait = null;
        wait?.Cancel();
        IconRequested = false;
        Icon = null;
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal QuickMixerRow(MixerViewModel mixer, AudioSessionItem source, TimeProvider? timeProvider = null, QuickMixerPage? page = null)
    {
        _mixer = mixer;
        _page = page;
        Source = source;
        _muteEditRevision = source.MuteEditRevision;
        _volumeEditRevision = source.VolumeEditRevision;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _peakEnvelope = new(_timeProvider.TimestampFrequency);
        ToggleMuteCommand = new(() =>
        {
            if (_disposed) return;
            if (_page != null) _page.ToggleMute(Source);
            else _mixer.TogglePresentationMute(Source);
        });
        source.PropertyChanged += SourceChanged;
    }

    internal void BeginVolumeEdit() { if (!_disposed) _page?.BeginVolumeEdit(Source); }
    internal void EndVolumeEdit() => _page?.EndVolumeEdit(Source);
    internal void SelectApplication(bool extend) { if (!_disposed) _page?.SelectApplication(this, extend); }

    private void SourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioSessionItem.SessionInstanceId))
        { ReleaseIcon(); _iconRetryAt = 0; _pendingVolume = null; _pendingMute = null; UpdateMeter(null, _mixer.MixerMode); }
        if (e.PropertyName == nameof(AudioSessionItem.IsMuted))
        {
            long revision = Source.MuteEditRevision;
            if (revision != _muteEditRevision)
            {
                _muteEditRevision = revision;
                _pendingMute = Source.IsMuted;
                _pendingMuteUntil = _timeProvider.GetTimestamp() + _timeProvider.TimestampFrequency / 4;
            }
            else if (_pendingMute != Source.IsMuted) _pendingMute = null;
        }
        if (e.PropertyName == nameof(AudioSessionItem.Volume))
        {
            long revision = Source.VolumeEditRevision;
            if (revision != _volumeEditRevision)
            {
                _volumeEditRevision = revision;
                _pendingVolume = Source.Volume;
                _pendingVolumeUntil = _timeProvider.GetTimestamp() + _timeProvider.TimestampFrequency / 4;
            }
            else if (_pendingVolume != Source.Volume) _pendingVolume = null;
            Changed(nameof(Volume));
        }
        if (e.PropertyName is nameof(AudioSessionItem.Volume) or nameof(AudioSessionItem.IsMuted))
        {
            _peakEnvelope.Reset();
            UpdateSignalPresentation();
        }
        if (e.PropertyName is nameof(AudioSessionItem.IsMuted) or nameof(AudioSessionItem.DisplayName))
        { Changed(nameof(MuteGlyph)); Changed(nameof(MuteLabel)); }
    }

    internal void UpdateMeter(AudioMeterSnapshot? snapshot, AudioMixerMode mode)
    {
        if (_disposed) return;
        bool available = false, incomplete = false, exact = false;
        bool hasControls = false, allHaveControls = true, controlsMatchVolume = true, controlsMatchMute = true;
        bool sessionUnmuted = false, sessionAudible = false;
        var controlHash = new HashCode();
        float maximum = 0;
        float postVolumeMaximum = 0;
        int contributors = 0;
        if (snapshot is { ErrorCode: null })
        {
            foreach (var reading in snapshot.Batch.Readings)
            {
                if (reading.Source.Mode != mode) continue;
                bool matches = Source.IsEndpoint
                    ? reading.Source.IsEndpoint && string.Equals(reading.Source.EndpointId, Source.EndpointId, StringComparison.OrdinalIgnoreCase)
                    : Source.IsApplication
                        ? !reading.Source.IsEndpoint && reading.Source.ProcessId == Source.ProcessId
                        : !reading.Source.IsEndpoint && string.Equals(reading.Source.SessionInstanceId, Source.SessionInstanceId, StringComparison.Ordinal)
                            && string.Equals(reading.Source.EndpointId, Source.EndpointId, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
                contributors++;
                exact |= Source.IsEndpoint || (string.Equals(reading.Source.SessionInstanceId, Source.SessionInstanceId, StringComparison.Ordinal)
                    && string.Equals(reading.Source.EndpointId, Source.EndpointId, StringComparison.OrdinalIgnoreCase));
                if (reading.Status == AudioMeterStatus.Available && reading.Peak is float value) { maximum = Math.Max(maximum, value); available = true; }
                else incomplete = true;
                if (mode == AudioMixerMode.Output && !Source.IsEndpoint)
                {
                    if (reading.VolumeScalar is float scalar && float.IsFinite(scalar) && reading.IsMuted is bool muted && reading.Peak is float raw)
                    {
                        hasControls = true;
                        sessionUnmuted |= !muted;
                        sessionAudible |= !muted && scalar > 0;
                        postVolumeMaximum = Math.Max(postVolumeMaximum, muted ? 0 : raw * scalar);
                        controlHash.Add(reading.Source); controlHash.Add(scalar); controlHash.Add(muted);
                        controlsMatchVolume &= !_pendingVolume.HasValue || Math.Abs(scalar - _pendingVolume.Value / 100f) < .0005f;
                        controlsMatchMute &= !_pendingMute.HasValue || muted == _pendingMute.Value;
                    }
                    else
                    {
                        allHaveControls = false;
                        if (reading.VolumeScalar.HasValue || reading.IsMuted.HasValue) incomplete = true;
                    }
                }
            }
            if (Source.IsApplication && (snapshot.Batch.Truncated ||
                (snapshot.Batch.CompleteModes & (mode == AudioMixerMode.Output ? AudioMeterModes.Output : AudioMeterModes.Input)) == 0
                || contributors < Source.SessionCount)) incomplete = true;
        }
        available &= !incomplete && exact && (!hasControls || allHaveControls);
        int nextHash = controlHash.ToHashCode();
        if (hasControls && (!_hasSessionControls || nextHash != _sessionControlHash)) _peakEnvelope.Reset();
        _hasSessionControls = hasControls;
        _sessionUnmuted = sessionUnmuted;
        _sessionAudible = sessionAudible;
        _sessionControlHash = nextHash;
        _postVolumePeak = available ? postVolumeMaximum : 0;
        if (available && hasControls && controlsMatchVolume) _pendingVolume = null;
        if (available && hasControls && controlsMatchMute) _pendingMute = null;
        if (_meterAvailable != available) { _meterAvailable = available; Changed(nameof(MeterAvailable)); }
        _rawPeak = available ? maximum : 0;
        UpdateSignalPresentation();
    }

    private void UpdateSignalPresentation()
    {
        bool output = _mixer.MixerMode == AudioMixerMode.Output;
        long now = _timeProvider.GetTimestamp();
        if (now >= _pendingVolumeUntil) _pendingVolume = null;
        if (now >= _pendingMuteUntil) _pendingMute = null;
        bool useSessionControls = _hasSessionControls && !_pendingVolume.HasValue && !_pendingMute.HasValue;
        bool muted = _pendingMute ?? (_hasSessionControls ? !_sessionUnmuted : Source.IsMuted);
        bool audible = useSessionControls ? _sessionAudible : !muted && Source.Volume > 0;
        double signal = !output ? _rawPeak : useSessionControls ? _postVolumePeak
            : muted ? 0 : _rawPeak * Math.Clamp(Source.Volume / 100d, 0, 1);
        bool showPeak = _meterAvailable && (!output || audible);
        double level = !_meterAvailable ? 0 : output ? signal * 100 : AudioMeterPresentation.ToPercent(signal);
        double heldDb = showPeak ? _peakEnvelope.Update(AudioMeterPresentation.ToDb(signal), now) : AudioMeterPresentation.FloorDb;
        double peak = !showPeak || heldDb <= AudioMeterPresentation.FloorDb ? 0
            : output ? Math.Pow(10, heldDb / 20) * 100 : AudioMeterPresentation.DbToPercent(heldDb);
        if (!showPeak) _peakEnvelope.Reset();
        if (_level != level) { _level = level; Changed(nameof(Level)); }
        if (_peak != peak) { _peak = peak; Changed(nameof(Peak)); }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Source.PropertyChanged -= SourceChanged;
        ToggleMuteCommand.Dispose();
        _selectApplication?.Dispose();
        _toggleSelection?.Dispose();
        _toggleRouting?.Dispose();
        ReleaseIcon();
        _icon = null;
    }
}

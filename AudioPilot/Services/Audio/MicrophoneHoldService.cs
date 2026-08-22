using System.Runtime.InteropServices;
using System.Windows.Input;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio;

internal interface IMicrophoneMuteLease : IDisposable
{
    bool Apply(bool muted, bool enforce = false);
    bool Restore();
    void Refresh();
    bool? ReadMuteState();
}

internal readonly record struct MicrophoneHoldFeedback(string Title, string Message, bool? Muted = null);

/// <summary>Serializes microphone holds and an optional persistent push-to-talk mode on captured endpoints.</summary>
internal sealed class MicrophoneHoldService(Logger logger, Action<MicrophoneHoldFeedback> feedback,
    Func<IMicrophoneMuteLease>? capture = null) : IAsyncDisposable
{
    private readonly Lock _sync = new();
    private readonly Dictionary<bool, Func<bool>> _holds = [];
    private readonly Dictionary<bool, Func<bool>> _cancelledHolds = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private Task? _operation;
    private bool _enabled;
    private int _leaseActive;
    private bool _disposed;
    private int _signalDisposed;
    private bool _abandonRestore;
    private long _revision;
    private long _requestVersion;
    private bool _faulted;
    private bool? _mutePreference;

    internal bool IsControlling
    {
        get { lock (_sync) return (!_disposed && (_enabled || _holds.Count > 0)) || Volatile.Read(ref _leaseActive) != 0; }
    }

    public void SetPushToTalkEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed || (_enabled == enabled && !_faulted)) return;
            _enabled = enabled;
            _revision++;
            _requestVersion++;
            CancelHolds();
            WakeUnderLock();
        }
    }

    public void Begin(bool muted, Func<bool> held)
    {
        lock (_sync)
        {
            if (_disposed || (!muted && !_enabled) || !held()) return;
            if (_cancelledHolds.TryGetValue(muted, out var cancelled) && cancelled()) return;
            _cancelledHolds.Remove(muted);
            _holds[muted] = held;
            _requestVersion++;
            WakeUnderLock();
        }
    }

    public void Cancel(bool abandonRestoration = false, bool? mutePreference = null)
    {
        lock (_sync)
        {
            if (_disposed) return;
            CancelHolds();
            _requestVersion++;
            if (_enabled && mutePreference.HasValue) _mutePreference = mutePreference;
            _abandonRestore |= abandonRestoration && !_enabled;
            if (_operation != null) WakeUnderLock();
        }
    }

    public void RefreshEndpoints()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _revision++;
            _requestVersion++;
            CancelHolds();
            if (_operation != null || _enabled) WakeUnderLock();
        }
    }

    private void CancelHolds()
    {
        foreach (var hold in _holds) _cancelledHolds[hold.Key] = hold.Value;
        _holds.Clear();
    }

    private void WakeUnderLock()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
        _operation ??= Task.Run(RunAsync);
    }

    private void OnEndpointChanged()
    {
        lock (_sync)
        {
            if (!_disposed && _enabled && !_faulted) WakeUnderLock();
        }
    }

    private async Task RunAsync()
    {
        IMicrophoneMuteLease? lease = null;
        bool leaseMode = false;
        bool? previous = null;
        long leaseRevision = -1;
        bool signalled = true;
        int consecutiveFailures = 0;
        long lastRequestVersion = -1;
        while (true)
        {
            bool enabled;
            bool disposed;
            bool held;
            bool muted;
            long revision;
            long requestVersion;
            lock (_sync)
            {
                if (_holds.TryGetValue(false, out var talkHeld) && !talkHeld()) _holds.Remove(false);
                if (_holds.TryGetValue(true, out var muteHeld) && !muteHeld()) _holds.Remove(true);
                enabled = _enabled;
                disposed = _disposed;
                held = _holds.Count > 0;
                muted = _holds.ContainsKey(true) || !_holds.ContainsKey(false);
                revision = _revision;
                requestVersion = _requestVersion;
            }
            if (requestVersion != lastRequestVersion) consecutiveFailures = 0;
            lastRequestVersion = requestVersion;
            int waitMilliseconds = held ? 16 : Timeout.Infinite;
            try
            {
                if (lease != null && (disposed || leaseMode != enabled || (leaseRevision != revision && !enabled) || (!enabled && !held)))
                {
                    bool restore = false;
                    var closing = lease;
                    lease = null;
                    bool wasApplied = previous.HasValue;
                    bool? restoredMute = ComThreadingHelper.RunOnCoreAudioThread(() =>
                    {
                        try
                        {
                            bool? preference;
                            lock (_sync)
                            {
                                restore = !_abandonRestore;
                                _abandonRestore = false;
                                preference = leaseMode ? _mutePreference : null;
                                _mutePreference = null;
                            }
                            if (!restore) return null;
                            bool restored = preference.HasValue ? closing.Apply(preference.Value, enforce: true) : closing.Restore();
                            if (!restored) throw new InvalidOperationException("Microphone state restoration failed.");
                            return closing.ReadMuteState();
                        }
                        finally { try { closing.Dispose(); } finally { Volatile.Write(ref _leaseActive, 0); } }
                    });
                    previous = null;
                    if (!disposed && wasApplied && restore)
                        Report(leaseMode ? "Push-to-talk off" : "Hold-to-mute released",
                            restoredMute switch { true => "Microphone muted", false => "Microphone live", _ => "Previous microphone state restored" }, restoredMute);
                }
                if (disposed) return;
                if (!enabled && lease == null)
                {
                    lock (_sync) { if (_requestVersion == requestVersion) _mutePreference = null; }
                }
                if (enabled || held)
                {
                    if (lease == null)
                    {
                        lock (_sync) { if (_requestVersion == requestVersion) _abandonRestore = false; }
                        lease = ComThreadingHelper.RunOnCoreAudioThread(() => capture != null ? capture() : EndpointMicrophoneMuteLease.Capture(logger, OnEndpointChanged));
                        Volatile.Write(ref _leaseActive, 1);
                        leaseMode = enabled;
                        leaseRevision = revision;
                        previous = null;
                    }
                    else if (leaseRevision != revision)
                    {
                        ComThreadingHelper.RunOnCoreAudioThread(lease.Refresh);
                        leaseRevision = revision;
                        previous = null;
                    }
                    if (previous != muted || (enabled && signalled))
                    {
                        bool? appliedMute = ComThreadingHelper.RunOnCoreAudioThread<bool?>(() =>
                        {
                            lock (_sync)
                            {
                                if (_disposed || _requestVersion != requestVersion || _enabled != enabled) return null;
                                bool talk = _holds.TryGetValue(false, out var talkInput) && talkInput();
                                bool mute = _holds.TryGetValue(true, out var muteInput) && muteInput();
                                if (!enabled && !mute) return null;
                                muted = mute || !talk;
                            }
                            if (!lease.Apply(muted, enforce: enabled)) throw new InvalidOperationException("Microphone hold was interrupted or no endpoint is available.");
                            return muted;
                        });
                        if (appliedMute.HasValue)
                        {
                            lock (_sync) _faulted = false;
                            consecutiveFailures = 0;
                            if (previous != appliedMute)
                                Report(enabled ? "Push-to-talk" : "Hold-to-mute", appliedMute.Value ? "Microphone muted" : "Microphone live", appliedMute);
                            previous = appliedMute;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_sync) _faulted = true;
                logger.Warning("MicrophoneHold", "microphone-hold-failed", nameof(RunAsync), ex);
                if (consecutiveFailures == 0) Report("Microphone control unavailable", "Check microphone mute state");
                if (lease != null && !leaseMode)
                {
                    var failedLease = lease;
                    lease = null;
                    try
                    {
                        ComThreadingHelper.RunOnCoreAudioThread(() =>
                        {
                            try
                            {
                                bool restore;
                                lock (_sync) { restore = !_abandonRestore; _abandonRestore = false; }
                                if (restore) _ = failedLease.Restore();
                            }
                            finally { try { failedLease.Dispose(); } finally { Volatile.Write(ref _leaseActive, 0); } }
                        });
                    }
                    catch (Exception cleanupError) { logger.Warning("MicrophoneHold", "microphone-hold-cleanup-failed", nameof(RunAsync), cleanupError); }
                }
                previous = null;
                leaseRevision = -1;
                consecutiveFailures++;
                lock (_sync)
                {
                    if (_requestVersion == requestVersion)
                    {
                        CancelHolds();
                        while (_signal.Wait(0)) { }
                    }
                    if (_disposed && lease == null) return;
                    if (_disposed || _requestVersion != requestVersion) continue;
                    waitMilliseconds = _enabled && consecutiveFailures <= 3 ? 100 << (consecutiveFailures - 1) : Timeout.Infinite;
                }
            }
            signalled = await _signal.WaitAsync(waitMilliseconds, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? operation;
        lock (_sync)
        {
            bool firstRequest = !_disposed;
            _disposed = true;
            _requestVersion++;
            CancelHolds();
            operation = _operation;
            if (firstRequest && operation != null && _signal.CurrentCount == 0) _signal.Release();
        }
        if (operation != null) await operation.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _signalDisposed, 1) == 0) _signal.Dispose();
    }

    private void Report(string title, string message, bool? muted = null)
    {
        try { feedback(new(title, message, muted)); }
        catch (Exception ex) { logger.Warning("MicrophoneHold", "microphone-hold-feedback-failed", nameof(Report), ex); }
    }

    internal static bool IsHoldable(HotkeyMainInput input) => input.Kind == HotkeyMainInputKind.MouseButton ||
        (input.Kind == HotkeyMainInputKind.Keyboard && input.Key is not (Key.Pause or Key.PrintScreen));
}
internal sealed class EndpointMicrophoneMuteLease : IMicrophoneMuteLease
{
    private sealed class Endpoint : IDisposable
    {
        public required MMDevice Device { get; init; }
        public required string Id { get; init; }
        public required AudioEndpointVolume Volume { get; init; }
        public required MicrophoneMuteOwnership Ownership { get; init; }
        public bool Faulted;
        public AudioEndpointVolumeNotificationDelegate? Notification;
        public void Dispose()
        {
            try { if (Notification != null) Volume.OnVolumeNotification -= Notification; Volume.Dispose(); }
            finally { Device.Dispose(); }
        }
    }
    private readonly List<Endpoint> _endpoints = [];
    private readonly Logger _logger;
    private readonly Action? _changed;
    private EndpointMicrophoneMuteLease(Logger logger, Action? changed) { _logger = logger; _changed = changed; }

    public static EndpointMicrophoneMuteLease Capture(Logger logger, Action? changed = null)
    {
        var lease = new EndpointMicrophoneMuteLease(logger, changed);
        try
        {
            lease.Refresh();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Refresh()
    {
        using var enumerator = new MMDeviceEnumerator();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            MMDevice? device = null;
            AudioEndpointVolume? volume = null;
            try
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                string id = device.ID;
                if (!seen.Add(id)) continue;
                int existingIndex = _endpoints.FindIndex(endpoint => string.Equals(endpoint.Id, id, StringComparison.OrdinalIgnoreCase));
                Endpoint? existing = existingIndex >= 0 ? _endpoints[existingIndex] : null;
                if (existing is { Faulted: false }) continue;
                volume = device.AudioEndpointVolume;
                bool muted = volume.Mute;
                _logger.Trace("MicrophoneHold", $"microphone-hold-capture | endpoint={LogPrivacy.Id(device.ID)} original={muted}");
                var endpoint = new Endpoint
                {
                    Device = device,
                    Id = id,
                    Volume = volume,
                    Ownership = existing?.Ownership ?? new MicrophoneMuteOwnership(muted),
                };
                volume.NotificationGuid = endpoint.Ownership.Context;
                endpoint.Notification = data =>
                {
                    endpoint.Ownership.Observe(data.EventContext, data.Muted);
                    _changed?.Invoke();
                };
                volume.OnVolumeNotification += endpoint.Notification;
                if (existing != null)
                {
                    _endpoints[existingIndex] = endpoint;
                    try { existing.Dispose(); }
                    catch (Exception ex) { _logger.Debug("MicrophoneHold", $"microphone-hold-old-endpoint-dispose-failed | hresult=0x{ex.HResult:X8}"); }
                }
                else _endpoints.Add(endpoint);
                volume = null;
                device = null;
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { }
            finally
            {
                try { volume?.Dispose(); }
                finally { device?.Dispose(); }
            }
        }
        for (int index = _endpoints.Count - 1; index >= 0; index--)
        {
            var endpoint = _endpoints[index];
            if (seen.Contains(endpoint.Id)) continue;
            try
            {
                if (endpoint.Ownership.ShouldRestore(endpoint.Volume.Mute)) endpoint.Volume.Mute = endpoint.Ownership.Original;
            }
            catch (Exception ex) { _logger.Debug("MicrophoneHold", $"microphone-hold-endpoint-removed | hresult=0x{ex.HResult:X8}"); }
            finally { _endpoints.RemoveAt(index); endpoint.Dispose(); }
        }
    }

    public bool Apply(bool muted, bool enforce = false)
    {
        if (_endpoints.Count == 0) return false;
        bool success = true;
        foreach (var endpoint in _endpoints)
        {
            try
            {
                bool current = endpoint.Volume.Mute;
                if (!endpoint.Ownership.TryApply(current, muted, enforce)) { success = false; continue; }
                if (current != muted) endpoint.Volume.Mute = muted;
                endpoint.Faulted = false;
            }
            catch (Exception ex)
            {
                success = false;
                endpoint.Faulted = true;
                _logger.Warning("MicrophoneHold", $"microphone-endpoint-mute-failed | endpoint={LogPrivacy.Id(endpoint.Id)} muted={muted}", nameof(Apply), ex);
            }
        }
        return success;
    }

    public bool Restore()
    {
        bool success = true;
        if (_endpoints.Any(static endpoint => endpoint.Faulted))
        {
            try { Refresh(); }
            catch (Exception ex)
            {
                success = false;
                _logger.Warning("MicrophoneHold", "microphone-restore-refresh-failed", nameof(Restore), ex);
            }
        }
        foreach (var endpoint in _endpoints)
        {
            if (!endpoint.Ownership.Changed) continue;
            try
            {
                bool current = endpoint.Volume.Mute;
                _logger.Trace("MicrophoneHold", $"microphone-hold-restore | endpoint={LogPrivacy.Id(endpoint.Id)} original={endpoint.Ownership.Original} expected={endpoint.Ownership.Expected} current={current}");
                if (endpoint.Ownership.ShouldRestore(current)) endpoint.Volume.Mute = endpoint.Ownership.Original;
            }
            catch (Exception ex)
            {
                success = false;
                _logger.Warning("MicrophoneHold", "microphone-endpoint-restore-failed", nameof(Restore), ex);
            }
        }
        return success;
    }

    public bool? ReadMuteState()
    {
        bool? muted = null;
        foreach (var endpoint in _endpoints)
        {
            bool current = endpoint.Volume.Mute;
            if (muted.HasValue && muted.Value != current) return null;
            muted = current;
        }
        return muted;
    }

    public void Dispose()
    {
        foreach (var endpoint in _endpoints)
        {
            try { endpoint.Dispose(); }
            catch (Exception ex) { _logger.Warning("MicrophoneHold", "microphone-endpoint-dispose-failed", nameof(Dispose), ex); }
        }
        _endpoints.Clear();
    }
}

/// <summary>Tracks temporary mute ownership without confusing unrelated volume notifications with a new mute choice.</summary>
internal sealed class MicrophoneMuteOwnership(bool original)
{
    private int _invalidated;
    private volatile bool _enforcing;
    private volatile bool _expected = original;
    private int _observedMute = original ? 1 : 0;
    internal Guid Context { get; } = Guid.NewGuid();
    internal bool Original { get; } = original;
    internal bool Expected => _expected;
    internal bool Changed { get; private set; }

    internal void Observe(Guid context, bool muted)
    {
        bool previouslyObserved = Interlocked.Exchange(ref _observedMute, muted ? 1 : 0) != 0;
        // A queued notification for the captured baseline is not a new external mute transition.
        if (!_enforcing && context != Context && muted != previouslyObserved && muted != _expected)
            Interlocked.Exchange(ref _invalidated, 1);
    }

    internal bool TryApply(bool current, bool muted, bool enforce)
    {
        if (!enforce && (Volatile.Read(ref _invalidated) != 0 || current != _expected)) return false;
        _enforcing = enforce;
        _expected = muted;
        Changed = true;
        return true;
    }

    internal bool ShouldRestore(bool current) => Changed && Volatile.Read(ref _invalidated) == 0 && current == _expected;
}

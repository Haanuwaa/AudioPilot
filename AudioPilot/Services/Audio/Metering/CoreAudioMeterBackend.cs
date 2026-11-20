using System.Collections.Immutable;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Services.Audio.Metering;

// All creation, peak reads and releases run on AudioPilot.MeterCOM. This activates
// only meters/session discovery, never an AudioClient, capture stream or volume setter.
// Peak-only mode is for opt-in cost comparisons; production uses session controls.
internal sealed class CoreAudioMeterBackend(bool includeOutputSessionControls = true) : IAudioMeterBackend
{
    internal const int MaximumReaders = 256;
    private const int MaximumEndpoints = 32;
    private const int MaximumSessionsScanned = 1024;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly List<MMDevice> _devices = [];
    private readonly List<AudioSessionControl> _sessions = [];
    private readonly List<Reader> _readers = [];
    private AudioMeterModes _completeModes;
    private bool _truncated;

    public AudioMeterBatch Read(AudioMeterModes modes, bool refresh, CancellationToken cancellationToken)
    {
        if (refresh) Discover(modes, cancellationToken);
        var readings = ImmutableArray.CreateBuilder<AudioMeterReading>(_readers.Count);
        foreach (Reader reader in _readers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readings.Add(reader.Read());
        }
        return new(readings.MoveToImmutable(), _completeModes, _truncated);
    }

    private void Discover(AudioMeterModes modes, CancellationToken cancellationToken)
    {
        ReleaseReaders();
        _completeModes = AudioMeterModes.None;
        _truncated = false;
        int share = modes == AudioMeterModes.Both ? 2 : 1;
        if ((modes & AudioMeterModes.Output) != 0) DiscoverMode(AudioMixerMode.Output, share, cancellationToken);
        if ((modes & AudioMeterModes.Input) != 0) DiscoverMode(AudioMixerMode.Input, share, cancellationToken);
    }

    private void DiscoverMode(AudioMixerMode mode, int share, CancellationToken cancellationToken)
    {
        bool complete = true;
        int readerLimit = _readers.Count + MaximumReaders / share;
        int deviceLimit = _devices.Count + MaximumEndpoints / share;
        try
        {
            using var devices = _enumerator.EnumerateAudioEndPoints(mode == AudioMixerMode.Output ? DataFlow.Render : DataFlow.Capture, DeviceState.Active);
            int count = devices.Count;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_devices.Count >= deviceLimit || _readers.Count >= readerLimit) { _truncated = true; complete = false; break; }
                try
                {
                    MMDevice device = devices[index];
                    _devices.Add(device); // Establish ownership before any fallible property read.
                    var source = new AudioMeterSource(mode, device.ID);
                    _readers.Add(Reader.Create(source, () => device.AudioMeterInformation));
                    using SessionCollection sessions = device.AudioSessionManager.Sessions;
                    int sessionCount = sessions.Count;
                    for (int sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (sessionIndex >= MaximumSessionsScanned || _readers.Count >= readerLimit)
                        { _truncated = true; complete = false; break; }
                        AudioSessionControl? session = null;
                        try
                        {
                            session = sessions[sessionIndex];
                            if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                            string sessionId = session.GetSessionInstanceIdentifier;
                            if (string.IsNullOrEmpty(sessionId)) { complete = false; continue; }
                            var sessionSource = new AudioMeterSource(mode, source.EndpointId, sessionId, session.GetProcessID);
                            Reader reader = Reader.Create(sessionSource, () => session.AudioMeterInformation,
                                mode == AudioMixerMode.Output && includeOutputSessionControls ? () => session.SimpleAudioVolume : null);
                            _readers.Add(reader);
                            if (reader.HasNativeMeter)
                            {
                                _sessions.Add(session);
                                session = null; // The backend now owns this session until refresh/hide.
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch { complete = false; } // Unknown absence, not an empty inventory.
                        finally
                        {
                            try { session?.Dispose(); } catch { complete = false; }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { complete = false; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { complete = false; }
        if (complete) _completeModes |= mode == AudioMixerMode.Output ? AudioMeterModes.Output : AudioMeterModes.Input;
    }

    private void ReleaseReaders()
    {
        _readers.Clear();
        List<Exception>? failures = null;
        // Borrowed session meters are owned by their session; endpoint meters and
        // session managers are owned by MMDevice. Release children before parents.
        foreach (AudioSessionControl session in _sessions)
        {
            try { session.Dispose(); } catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        _sessions.Clear();
        foreach (MMDevice device in _devices)
        {
            try { device.Dispose(); } catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        _devices.Clear();
        if (failures != null) throw new AggregateException("Meter resource cleanup failed.", failures);
    }

    public void Dispose()
    {
        try { ReleaseReaders(); }
        finally { _enumerator.Dispose(); }
    }

    private sealed class Reader(AudioMeterSource source, AudioMeterInformation? meter, AudioMeterStatus status, int? error, SimpleAudioVolume? volume = null)
    {
        internal bool HasNativeMeter => meter != null;
        internal static Reader Create(AudioMeterSource source, Func<AudioMeterInformation> activate, Func<SimpleAudioVolume>? activateVolume = null)
        {
            try { return new(source, activate(), AudioMeterStatus.Available, null, activateVolume?.Invoke()); }
            catch (NotSupportedException ex) { return new(source, null, AudioMeterStatus.Unsupported, ex.HResult); }
            catch (Exception ex) { return new(source, null, AudioMeterStatus.Unavailable, ex.HResult); }
        }

        internal AudioMeterReading Read()
        {
            if (meter == null) return new(source, status, null, error);
            try
            {
                float peak = meter.MasterPeakValue;
                return volume == null ? AudioMeterReading.FromPeak(source, peak)
                    : AudioMeterReading.FromPeak(source, peak, volume.Volume, volume.Mute);
            }
            catch (Exception ex)
            {
                // A disconnected reader stays unknown until rediscovery. Do not
                // allocate/throw the same native failure twenty times per second.
                meter = null;
                status = AudioMeterStatus.Unavailable;
                error = ex.HResult;
                return new(source, status, null, error);
            }
        }
    }
}

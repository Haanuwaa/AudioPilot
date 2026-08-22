using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceSessionMonitoringCoordinatorFacade(
        Logger logger,
        AudioSessionService sessionService,
        SessionMonitorCoordinator playbackCoordinator,
        SessionMonitorCoordinator recordingCoordinator,
        Func<bool> isDisposed,
        Action<AudioSessionLifecycleSignal> notifyLifecycleChanged)
    {
        private readonly Logger _logger = logger;
        private readonly AudioSessionService _sessionService = sessionService;
        private readonly SessionMonitorCoordinator _playbackCoordinator = playbackCoordinator;
        private readonly SessionMonitorCoordinator _recordingCoordinator = recordingCoordinator;
        private readonly Func<bool> _isDisposed = isDisposed;
        private readonly Action<AudioSessionLifecycleSignal> _notifyLifecycleChanged = notifyLifecycleChanged;
        private int _playbackConsumers;
        private int _recordingConsumers;
        private int _playbackMonitoringActive;
        private int _recordingMonitoringActive;

        internal int GetConsumerCountForTests(AudioMixerMode mixerMode)
        {
            return mixerMode == AudioMixerMode.Input
                ? Volatile.Read(ref _recordingConsumers)
                : Volatile.Read(ref _playbackConsumers);
        }

        internal bool IsMonitoringActiveForTests(AudioMixerMode mixerMode)
        {
            return mixerMode == AudioMixerMode.Input
                ? Volatile.Read(ref _recordingMonitoringActive) != 0
                : Volatile.Read(ref _playbackMonitoringActive) != 0;
        }

        internal int GetEndpointMonitorCountForTests(AudioMixerMode mixerMode)
        {
            return mixerMode == AudioMixerMode.Input
                ? _recordingCoordinator.GetEndpointMonitorCountForTests()
                : _playbackCoordinator.GetEndpointMonitorCountForTests();
        }

        internal void Acquire(AudioMixerMode mixerMode)
        {
            if (_isDisposed())
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"session-monitoring-acquire-skip | mode={mixerMode} reason=disposed");
                }
                return;
            }

            int consumerCount = mixerMode == AudioMixerMode.Input
                ? Interlocked.Increment(ref _recordingConsumers)
                : Interlocked.Increment(ref _playbackConsumers);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "AudioDeviceService",
                    () => $"session-monitoring-acquire | mode={mixerMode} consumers={consumerCount}");
            }

            Update();
        }

        internal void Release(AudioMixerMode mixerMode)
        {
            ref int consumerCountRef = ref (mixerMode == AudioMixerMode.Input
                ? ref _recordingConsumers
                : ref _playbackConsumers);

            int currentCount;
            while (true)
            {
                currentCount = Volatile.Read(ref consumerCountRef);
                if (currentCount == 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref consumerCountRef, currentCount - 1, currentCount) == currentCount)
                {
                    break;
                }
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "AudioDeviceService",
                    () => $"session-monitoring-release | mode={mixerMode} consumers={currentCount - 1}");
            }

            Update();
        }

        internal void Update()
        {
            bool outputActive = HasConsumers(AudioMixerMode.Output);
            bool inputActive = HasConsumers(AudioMixerMode.Input);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace(
                    "AudioDeviceService",
                    () => $"session-monitoring-update | outputActive={outputActive} outputConsumers={Volatile.Read(ref _playbackConsumers)} inputActive={inputActive} inputConsumers={Volatile.Read(ref _recordingConsumers)}");
            }

            if (outputActive)
            {
                Volatile.Write(ref _playbackMonitoringActive, 1);
                _playbackCoordinator.Update();
            }
            else if (Interlocked.Exchange(ref _playbackMonitoringActive, 0) != 0)
            {
                _playbackCoordinator.Stop();
            }

            if (inputActive)
            {
                Volatile.Write(ref _recordingMonitoringActive, 1);
                _recordingCoordinator.Update();
            }
            else if (Interlocked.Exchange(ref _recordingMonitoringActive, 0) != 0)
            {
                _recordingCoordinator.Stop();
            }
        }

        internal void Stop()
        {
            Volatile.Write(ref _playbackMonitoringActive, 0);
            Volatile.Write(ref _recordingMonitoringActive, 0);
            _playbackCoordinator.Stop();
            _recordingCoordinator.Stop();
        }

        internal Task StopAndDrainAsync(Func<Task>? drainOverride)
        {
            Volatile.Write(ref _playbackMonitoringActive, 0);
            Volatile.Write(ref _recordingMonitoringActive, 0);
            if (drainOverride != null)
            {
                return drainOverride();
            }

            return Task.WhenAll(
                _playbackCoordinator.StopAndDrainAsync(),
                _recordingCoordinator.StopAndDrainAsync());
        }

        internal void OnEndpointVolumeChanged(AudioMixerMode mixerMode, string endpointId, float volumePercent, bool isMuted)
        {
            if (_isDisposed())
            {
                return;
            }

            bool isPrimaryEndpoint = _sessionService.RecordEndpointVolumeNotification(mixerMode, endpointId, volumePercent, isMuted);
            _notifyLifecycleChanged(new AudioSessionLifecycleSignal(
                mixerMode,
                AudioSessionLifecycleSignalKind.EndpointVolumeChanged,
                $"endpoint:{endpointId}",
                EndpointId: endpointId,
                VolumePercent: isPrimaryEndpoint ? volumePercent : null,
                IsMuted: isPrimaryEndpoint ? isMuted : null));
        }

        private bool HasConsumers(AudioMixerMode mixerMode)
        {
            return mixerMode == AudioMixerMode.Input
                ? Volatile.Read(ref _recordingConsumers) > 0
                : Volatile.Read(ref _playbackConsumers) > 0;
        }
    }
}

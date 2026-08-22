using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceResumeRecoveryHelper(
        Logger logger,
        Func<bool> isDisposed,
        Func<bool> isRegistered,
        Action registerNotificationClient,
        Action updateSessionMonitoring)
    {
        private const string QueueBestEffortMethodName = "QueueBestEffortResumeRecoveryWork";

        private readonly Logger _logger = logger;
        private readonly Func<bool> _isDisposed = isDisposed;
        private readonly Func<bool> _isRegistered = isRegistered;
        private readonly Action _registerNotificationClient = registerNotificationClient;
        private readonly Action _updateSessionMonitoring = updateSessionMonitoring;

        public bool TryQueueBestEffortRecovery(
            ConcurrentDictionary<int, Task> backgroundTasks,
            ref int backgroundTaskId,
            CancellationTokenSource backgroundWorkCts)
        {
            if (_isDisposed())
            {
                return false;
            }

            if (backgroundTasks.Count >= AppConstants.Limits.MaxConcurrentBackgroundTasks)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.BestEffortQueueSkip} | reason=queue-saturated");
                }

                return false;
            }

            return BackgroundTaskHelper.TryQueue(
                backgroundTasks,
                ref backgroundTaskId,
                backgroundWorkCts,
                _isDisposed,
                RunBestEffortRecoveryAsync,
                ex =>
                {
                    if (!_isDisposed())
                    {
                        _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.BestEffortFailed, QueueBestEffortMethodName, ex);
                    }
                });
        }

        internal async Task RunBestEffortRecoveryAsync(CancellationToken shutdownToken)
        {
            if (shutdownToken.IsCancellationRequested || _isDisposed())
            {
                return;
            }

            try
            {
                if (!_isRegistered())
                {
                    _registerNotificationClient();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.BestEffortRegisterFailed, QueueBestEffortMethodName, ex);
            }

            try
            {
                _updateSessionMonitoring();
            }
            catch (Exception ex)
            {
                _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.BestEffortMonitorFailed, QueueBestEffortMethodName, ex);
            }

            await Task.CompletedTask;
        }
    }
}

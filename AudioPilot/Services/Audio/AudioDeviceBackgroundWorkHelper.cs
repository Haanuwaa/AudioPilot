using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceBackgroundWorkHelper(Logger logger, Func<bool> isDisposed)
    {
        private readonly Logger _logger = logger;
        private readonly Func<bool> _isDisposed = isDisposed;

        public bool TryQueue(
            ConcurrentDictionary<int, Task> backgroundTasks,
            ref int backgroundTaskId,
            CancellationTokenSource backgroundWorkCts,
            Func<CancellationToken, Task> operation,
            string operationName)
        {
            return BackgroundTaskHelper.TryQueueWithPolicy(
                backgroundTasks,
                ref backgroundTaskId,
                backgroundWorkCts,
                _isDisposed,
                operation,
                operationName,
                (name, ex) =>
                {
                    if (!_isDisposed())
                    {
                        _logger.Warning(
                            "AudioDeviceService",
                            () => $"background-operation-failed | operation={name} reason=exception disposed={_isDisposed()}",
                            name,
                            ex);
                    }
                },
                AppConstants.Limits.MaxConcurrentBackgroundTasks,
                name =>
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.Warning("AudioDeviceService", () => $"background-operation-drop | operation={name} reason=queue-saturated disposed={_isDisposed()}");
                    }
                });
        }
    }
}

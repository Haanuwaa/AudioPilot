using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceResumeRecoveryCoordinator(
        Logger logger,
        Func<bool> isDisposed)
        : IDisposable
    {
        private readonly Logger _logger = logger;
        private readonly Func<bool> _isDisposed = isDisposed;
        private readonly SemaphoreSlim _resumeRecoverySemaphore = new(1, 1);
        private readonly Lock _resumeRecoveryStateLock = new();
        private DateTime _lastResumeRecoveryUtc;
        private TaskCompletionSource<bool> _resumeRecoveryCompletionSource = CreateCompletedResumeRecoveryCompletionSource();
        private int _activeResumeRecoveryCount;
        private int _resumeRecoverySemaphoreWaiterCount;

        internal SemaphoreSlim SemaphoreForTests => _resumeRecoverySemaphore;
        internal bool IsWaitingOnSemaphoreForTests => Volatile.Read(ref _resumeRecoverySemaphoreWaiterCount) > 0;
        internal int ActiveRecoveryCountForTests => Volatile.Read(ref _activeResumeRecoveryCount);

        internal async Task RecoverAfterSystemResumeAsync(
            Func<DeviceCacheHelper?> deviceCacheAccessor,
            Action invalidateRecentMixerSnapshotState,
            Action resetSwitchTimes,
            Func<bool> queueBestEffortResumeRecoveryWork,
            CancellationToken shutdownToken)
        {
            if (_isDisposed())
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastResumeRecoveryUtc).TotalSeconds < 2)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Skip} | reason=cooldown");
                }

                return;
            }

            if (!TryBeginResumeRecovery())
            {
                return;
            }

            bool semaphoreEntered = false;
            Stopwatch? stopwatch = null;
            bool resumeRecoverySucceeded = false;
            bool cacheInvalidated = false;
            bool bestEffortQueued = false;
            try
            {
                Interlocked.Increment(ref _resumeRecoverySemaphoreWaiterCount);
                try
                {
                    await _resumeRecoverySemaphore.WaitAsync(shutdownToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _resumeRecoverySemaphoreWaiterCount);
                }

                semaphoreEntered = true;
                stopwatch = Stopwatch.StartNew();

                if (_isDisposed())
                {
                    return;
                }

                nowUtc = DateTime.UtcNow;
                if ((nowUtc - _lastResumeRecoveryUtc).TotalSeconds < 2)
                {
                    return;
                }

                _lastResumeRecoveryUtc = nowUtc;
                _logger.Info("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.Start);

                DeviceCacheHelper? deviceCache = deviceCacheAccessor();
                if (deviceCache != null)
                {
                    deviceCache.InvalidateCache();
                    cacheInvalidated = true;
                }

                invalidateRecentMixerSnapshotState();
                resetSwitchTimes();

                bestEffortQueued = queueBestEffortResumeRecoveryWork();
                resumeRecoverySucceeded = true;
                _logger.Info("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.Success);
            }
            catch (OperationCanceledException) when (_isDisposed() || shutdownToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.Failed, nameof(RecoverAfterSystemResumeAsync), ex);
            }
            finally
            {
                if (stopwatch != null)
                {
                    stopwatch.Stop();
                    _logger.Info(
                        "AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Summary} | durationMs={stopwatch.Elapsed.TotalMilliseconds:F1} success={resumeRecoverySucceeded} cacheInvalidated={cacheInvalidated} bestEffortQueued={bestEffortQueued}");
                }

                if (semaphoreEntered)
                {
                    try
                    {
                        _resumeRecoverySemaphore.Release();
                    }
                    catch (ObjectDisposedException) when (_isDisposed())
                    {
                    }
                }

                EndResumeRecovery();
            }
        }

        internal void SetStateForTests(TaskCompletionSource<bool> completionSource, int activeCount)
        {
            lock (_resumeRecoveryStateLock)
            {
                _resumeRecoveryCompletionSource = completionSource;
                _activeResumeRecoveryCount = activeCount;
            }
        }

        internal Task WaitForActiveResumeRecoveryAsync()
        {
            lock (_resumeRecoveryStateLock)
            {
                return _activeResumeRecoveryCount == 0
                    ? Task.CompletedTask
                    : _resumeRecoveryCompletionSource.Task;
            }
        }

        internal void SignalShutdown()
        {
            TaskCompletionSource<bool>? completionSource;
            lock (_resumeRecoveryStateLock)
            {
                completionSource = _resumeRecoveryCompletionSource;
            }

            completionSource?.TrySetResult(true);
        }

        internal Task CreateBoundedDrainTask()
        {
            Task resumeRecoveryTask = WaitForActiveResumeRecoveryAsync();
            return resumeRecoveryTask.IsCompleted
                ? resumeRecoveryTask
                : WaitForResumeRecoveryDrainAsync(resumeRecoveryTask);
        }

        private static TaskCompletionSource<bool> CreateCompletedResumeRecoveryCompletionSource()
        {
            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            completionSource.TrySetResult(true);
            return completionSource;
        }

        private bool TryBeginResumeRecovery()
        {
            lock (_resumeRecoveryStateLock)
            {
                if (_isDisposed())
                {
                    return false;
                }

                if (_activeResumeRecoveryCount == 0)
                {
                    _resumeRecoveryCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _activeResumeRecoveryCount++;
                return true;
            }
        }

        private void EndResumeRecovery()
        {
            TaskCompletionSource<bool>? completionSource = null;

            lock (_resumeRecoveryStateLock)
            {
                if (_activeResumeRecoveryCount == 0)
                {
                    return;
                }

                _activeResumeRecoveryCount--;
                if (_activeResumeRecoveryCount == 0)
                {
                    completionSource = _resumeRecoveryCompletionSource;
                }
            }

            completionSource?.TrySetResult(true);
        }

        private async Task WaitForResumeRecoveryDrainAsync(Task resumeRecoveryTask)
        {
            try
            {
                await resumeRecoveryTask.WaitAsync(TimeSpan.FromMilliseconds(AppConstants.Timing.CleanupWaitMs));
            }
            catch (TimeoutException) when (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeForced} | reason=resume-recovery-timeout");
            }
        }

        public void Dispose()
        {
            _resumeRecoverySemaphore.Dispose();
        }
    }
}

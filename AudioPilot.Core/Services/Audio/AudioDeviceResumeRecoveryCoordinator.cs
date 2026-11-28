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
        private long _completedRecoveryVersion;
        private bool _lastRecoverySucceeded;
        private TaskCompletionSource<bool> _resumeRecoveryCompletionSource = CreateCompletedResumeRecoveryCompletionSource();
        private TaskCompletionSource<bool> _actualRecoveryCompletionSource = CreateCompletedResumeRecoveryCompletionSource();
        private int _activeResumeRecoveryCount;
        private int _resumeRecoverySemaphoreWaiterCount;

        internal SemaphoreSlim SemaphoreForTests => _resumeRecoverySemaphore;
        internal bool IsWaitingOnSemaphoreForTests => Volatile.Read(ref _resumeRecoverySemaphoreWaiterCount) > 0;
        internal int ActiveRecoveryCountForTests => Volatile.Read(ref _activeResumeRecoveryCount);

        internal async Task<bool> RecoverAfterSystemResumeAsync(
            Func<DeviceCacheHelper?> deviceCacheAccessor,
            Action invalidateRecentMixerSnapshotState,
            Func<CancellationToken, Task<bool>> recoverCallbacksAsync,
            CancellationToken cancellationToken)
        {
            if (_isDisposed())
            {
                return false;
            }

            long requestedVersion = Interlocked.Read(ref _completedRecoveryVersion);
            if (!TryBeginResumeRecovery())
            {
                return false;
            }

            bool semaphoreEntered = false;
            Stopwatch? stopwatch = null;
            bool resumeRecoverySucceeded = false;
            bool cacheInvalidated = false;
            try
            {
                Interlocked.Increment(ref _resumeRecoverySemaphoreWaiterCount);
                try
                {
                    await _resumeRecoverySemaphore.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _resumeRecoverySemaphoreWaiterCount);
                }

                semaphoreEntered = true;
                if (_isDisposed())
                {
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (requestedVersion != Interlocked.Read(ref _completedRecoveryVersion)) return _lastRecoverySucceeded;

                stopwatch = Stopwatch.StartNew();
                _logger.Info("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.Start);

                DeviceCacheHelper? deviceCache = deviceCacheAccessor();
                if (deviceCache != null)
                {
                    deviceCache.InvalidateCache();
                    cacheInvalidated = true;
                }

                invalidateRecentMixerSnapshotState();

                resumeRecoverySucceeded = await recoverCallbacksAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _lastRecoverySucceeded = resumeRecoverySucceeded;
                Interlocked.Increment(ref _completedRecoveryVersion);
                if (resumeRecoverySucceeded)
                    _logger.Info("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.Success);
                return resumeRecoverySucceeded;
            }
            catch (OperationCanceledException) when (_isDisposed() || cancellationToken.IsCancellationRequested)
            {
                resumeRecoverySucceeded = false;
                return false;
            }
            catch (Exception ex)
            {
                _lastRecoverySucceeded = false;
                Interlocked.Increment(ref _completedRecoveryVersion);
                _logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.Failed, nameof(RecoverAfterSystemResumeAsync), ex);
                return false;
            }
            finally
            {
                if (stopwatch != null)
                {
                    stopwatch.Stop();
                    _logger.Info(
                        "AudioDeviceService",
                        () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Summary} | durationMs={stopwatch.Elapsed.TotalMilliseconds:F1} success={resumeRecoverySucceeded} cacheInvalidated={cacheInvalidated} callbacksRecovered={resumeRecoverySucceeded}");
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

        internal Task WaitForActualRecoveryDrainAsync()
        {
            lock (_resumeRecoveryStateLock) return _actualRecoveryCompletionSource.Task;
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
                    _actualRecoveryCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    _actualRecoveryCompletionSource.TrySetResult(true);
                }
            }

            completionSource?.TrySetResult(true);
        }

        public void Dispose()
        {
            _resumeRecoverySemaphore.Dispose();
        }
    }
}

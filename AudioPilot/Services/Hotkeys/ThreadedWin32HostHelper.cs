using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys
{
    internal sealed class ThreadedWin32StartupSignal : IDisposable
    {
        private readonly ManualResetEventSlim _signal = new(false);
        private int _referenceCount = 1;

        public bool IsSet => _signal.IsSet;

        public void AddReference()
        {
            _ = Interlocked.Increment(ref _referenceCount);
        }

        public void Set() => _signal.Set();

        public bool Wait(int millisecondsTimeout) => _signal.Wait(millisecondsTimeout);

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                _signal.Dispose();
            }
        }
    }

    internal static class ThreadedWin32HostHelper
    {
        public static Thread StartBackgroundWorker(ThreadStart workerLoop, string threadName)
        {
            Thread worker = new(workerLoop)
            {
                IsBackground = true,
                Name = threadName,
            };

            worker.Start();
            return worker;
        }

        public static bool WaitForStartup(
            ThreadedWin32StartupSignal started,
            Func<Exception?> getStartupFailure,
            Func<bool> isRunning,
            Logger logger,
            string logSource,
            string timeoutMessage)
        {
            if (!started.Wait(AppConstants.Timing.CleanupWaitMs))
            {
                logger.Warning(logSource, () => timeoutMessage);
                return false;
            }

            return getStartupFailure() == null && isRunning();
        }

        public static void RequestStopAndJoin(
            Thread? worker,
            Func<uint> getWorkerThreadId,
            Func<uint, bool> requestStop,
            Logger logger,
            string logSource,
            string timeoutMessage)
        {
            if (worker == null)
            {
                return;
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int timeoutMs = AppConstants.Timing.CleanupWaitMs;
            bool quitPosted = false;

            while (worker.IsAlive)
            {
                if (!quitPosted)
                {
                    uint workerThreadId = getWorkerThreadId();
                    if (workerThreadId != 0)
                    {
                        quitPosted = requestStop(workerThreadId);
                    }
                }

                int elapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                int remainingMs = timeoutMs - elapsedMs;
                if (remainingMs <= 0)
                {
                    break;
                }

                int sliceMs = quitPosted ? remainingMs : Math.Min(10, remainingMs);
                if (worker.Join(sliceMs))
                {
                    return;
                }
            }

            if (worker.IsAlive)
            {
                logger.Warning(logSource, () => timeoutMessage);
            }
        }
    }
}

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
            Exception? startupFailure,
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

            return startupFailure == null && isRunning();
        }

        public static void RequestStopAndJoin(
            Thread? worker,
            uint workerThreadId,
            Action<uint> requestStop,
            Logger logger,
            string logSource,
            string timeoutMessage)
        {
            if (worker == null)
            {
                return;
            }

            if (workerThreadId != 0)
            {
                requestStop(workerThreadId);
            }

            if (!worker.Join(AppConstants.Timing.CleanupWaitMs))
            {
                logger.Warning(logSource, () => timeoutMessage);
            }
        }
    }
}

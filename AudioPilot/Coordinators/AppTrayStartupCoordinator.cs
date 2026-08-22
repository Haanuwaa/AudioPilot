using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators;

/// <summary>
/// Retries actual tray registration while Explorer starts, without blocking the dispatcher or hotkeys.
/// </summary>
internal static class AppTrayStartupCoordinator
{
    internal static async Task<bool> EnsureVisibleAsync(
        Func<bool> tryShow,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan>? elapsed = null)
    {
        long started = Stopwatch.GetTimestamp();
        elapsed ??= () => Stopwatch.GetElapsedTime(started);
        delay ??= static (duration, token) => Task.Delay(duration, token);
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        int attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            if (tryShow())
            {
                if (attempts > 1)
                {
                    logger.Info("AppTrayStartupCoordinator", $"startup-tray-ready | attempts={attempts} elapsedMs={elapsed().TotalMilliseconds:F0}");
                }
                return true;
            }

            TimeSpan remaining = timeout - elapsed();
            if (remaining <= TimeSpan.Zero)
            {
                logger.Warning("AppTrayStartupCoordinator", $"startup-tray-unavailable | attempts={attempts} elapsedMs={elapsed().TotalMilliseconds:F0}");
                return false;
            }
            if (attempts == 1)
            {
                logger.Info("AppTrayStartupCoordinator", "startup-tray-waiting | reason=registration-unavailable");
            }
            await delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
}

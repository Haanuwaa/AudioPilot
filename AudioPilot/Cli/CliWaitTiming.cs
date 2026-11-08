namespace AudioPilot.Cli
{
    internal static class CliWaitTiming
    {
        internal static int ResolveRemainingDelayMs(long elapsedMs, int timeoutMs, int pollIntervalMs)
        {
            long normalizedElapsedMs = Math.Max(0L, elapsedMs);
            int normalizedTimeoutMs = Math.Max(0, timeoutMs);
            int normalizedPollIntervalMs = Math.Max(1, pollIntervalMs);
            long remainingMs = normalizedTimeoutMs - normalizedElapsedMs;
            return remainingMs <= 0
                ? 0
                : (int)Math.Min(normalizedPollIntervalMs, remainingMs);
        }
    }
}

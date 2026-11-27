using System.Collections.Concurrent;

namespace AudioPilot.Logging;

internal sealed class LogCooldownGate(
    int cooldownMilliseconds,
    Func<long>? tickProvider = null)
{
    private readonly ConcurrentDictionary<string, long> _lastAcceptedTickByKey = new();
    private readonly int _cooldownMilliseconds = cooldownMilliseconds >= 0
        ? cooldownMilliseconds
        : throw new ArgumentOutOfRangeException(nameof(cooldownMilliseconds));
    private readonly Func<long> _tickProvider = tickProvider ?? (static () => Environment.TickCount64);

    internal bool TryEnter(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        long nowTick = _tickProvider();
        while (true)
        {
            if (!_lastAcceptedTickByKey.TryGetValue(key, out long lastTick))
            {
                if (_lastAcceptedTickByKey.TryAdd(key, nowTick))
                {
                    return true;
                }

                continue;
            }

            long elapsedMilliseconds = nowTick - lastTick;
            if (elapsedMilliseconds >= 0 && elapsedMilliseconds < _cooldownMilliseconds)
            {
                return false;
            }

            if (_lastAcceptedTickByKey.TryUpdate(key, nowTick, lastTick))
            {
                return true;
            }
        }
    }

    internal void Clear() => _lastAcceptedTickByKey.Clear();
}

namespace AudioPilot.Logging;

/// <summary>
/// Tracks per-key occurrence counts while bounding the lifetime of keys that are never explicitly cleared.
/// </summary>
internal sealed class BoundedLogOccurrenceCounter
{
    private readonly record struct CounterEntry(int Count, long Sequence);

    private readonly Dictionary<string, CounterEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly int _capacity;
    private long _sequence;

    internal BoundedLogOccurrenceCounter(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    internal int Increment(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_lock)
        {
            long sequence = ++_sequence;
            if (_entries.TryGetValue(key, out CounterEntry current))
            {
                int nextCount = current.Count == int.MaxValue ? 1 : current.Count + 1;
                _entries[key] = new CounterEntry(nextCount, sequence);
                return nextCount;
            }

            if (_entries.Count >= _capacity)
            {
                RemoveOldestEntry();
            }

            _entries.Add(key, new CounterEntry(1, sequence));
            return 1;
        }
    }

    internal void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_lock)
        {
            _entries.Remove(key);
        }
    }

    internal void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private void RemoveOldestEntry()
    {
        string? oldestKey = null;
        long oldestSequence = long.MaxValue;

        foreach ((string key, CounterEntry entry) in _entries)
        {
            if (entry.Sequence >= oldestSequence)
            {
                continue;
            }

            oldestKey = key;
            oldestSequence = entry.Sequence;
        }

        if (oldestKey is not null)
        {
            _entries.Remove(oldestKey);
        }
    }
}

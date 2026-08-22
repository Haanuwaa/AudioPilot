using System.Collections.ObjectModel;
using AudioPilot.Models;

namespace AudioPilot.Services.Diagnostics
{
    public sealed class ExecutionHistoryService
    {
        private readonly Lock _gate = new();
        private readonly LinkedList<ExecutionHistoryEntry> _entries = [];

        public ExecutionHistoryService(int capacity = 100)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

            Capacity = capacity;
        }

        public int Capacity { get; }

        public void Record(ExecutionHistoryEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            lock (_gate)
            {
                _entries.AddFirst(entry);
                while (_entries.Count > Capacity)
                {
                    _entries.RemoveLast();
                }
            }
        }

        public IReadOnlyList<ExecutionHistoryEntry> GetEntries(int? limit = null, ExecutionHistoryKind? kind = null)
        {
            int effectiveLimit = limit.GetValueOrDefault(int.MaxValue);
            if (effectiveLimit <= 0)
            {
                return [];
            }

            var results = new List<ExecutionHistoryEntry>();

            lock (_gate)
            {
                foreach (ExecutionHistoryEntry entry in _entries)
                {
                    if (kind.HasValue && entry.Kind != kind.Value)
                    {
                        continue;
                    }

                    results.Add(entry);
                    if (results.Count >= effectiveLimit)
                    {
                        break;
                    }
                }
            }

            return new ReadOnlyCollection<ExecutionHistoryEntry>(results);
        }

        public ExecutionHistoryEntry? GetEntry(string opId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(opId);

            lock (_gate)
            {
                foreach (ExecutionHistoryEntry entry in _entries)
                {
                    if (string.Equals(entry.OpId, opId, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }

            return null;
        }
    }
}

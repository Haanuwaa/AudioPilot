namespace AudioPilot.Services.Audio
{
    /// <summary>Disambiguates session labels while preserving names already present in the inventory.</summary>
    internal static class AudioSessionNameDisambiguator
    {
        internal static string[] GetUniqueNames<T>(
            IReadOnlyList<T> sessions,
            Func<T, string> getDisplayName,
            Func<T, string?> getProcessName,
            Func<T, string?> getWindowTitle)
        {
            var names = new string[sessions.Count];
            var groups = new Dictionary<string, (int FirstIndex, List<int>? Duplicates)>(StringComparer.OrdinalIgnoreCase);
            bool hasDuplicates = false;
            for (int index = 0; index < sessions.Count; index++)
            {
                string name = getDisplayName(sessions[index]);
                names[index] = name;
                if (!groups.TryGetValue(name, out var group))
                {
                    groups[name] = (index, null);
                }
                else if (group.Duplicates is { } duplicates)
                {
                    duplicates.Add(index);
                }
                else
                {
                    groups[name] = (group.FirstIndex, [group.FirstIndex, index]);
                    hasDuplicates = true;
                }
            }

            if (!hasDuplicates) return names;

            foreach (var (_, items) in groups.Values)
            {
                if (items == null) continue;

                var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool uniqueProcesses = true;
                foreach (int index in items)
                {
                    string? process = getProcessName(sessions[index]);
                    if (string.IsNullOrEmpty(process) || !processes.Add(process))
                    {
                        uniqueProcesses = false;
                        break;
                    }
                }

                if (uniqueProcesses)
                {
                    foreach (int index in items)
                    {
                        string process = getProcessName(sessions[index])!;
                        if (!names[index].Contains(process, StringComparison.OrdinalIgnoreCase))
                            names[index] = $"{names[index]} ({process})";
                    }
                    continue;
                }

                var titles = new HashSet<string>(StringComparer.Ordinal);
                foreach (int index in items)
                    titles.Add(getWindowTitle(sessions[index])?.Trim() ?? string.Empty);

                if (titles.Count != items.Count) continue;
                foreach (int index in items)
                {
                    string title = getWindowTitle(sessions[index])?.Trim() ?? string.Empty;
                    if (title.Length == 0) continue;
                    string name = title.Contains(names[index], StringComparison.OrdinalIgnoreCase)
                        ? title : $"{names[index]} ({title})";
                    if (titles.Contains(string.Empty) && string.Equals(name, getProcessName(sessions[index]), StringComparison.OrdinalIgnoreCase))
                        name = $"{name} (Window)";
                    names[index] = name;
                }
            }

            var reserved = new HashSet<string>(groups.Keys, StringComparer.OrdinalIgnoreCase);
            reserved.UnionWith(names);
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resolved = new bool[sessions.Count];
            for (int index = 0; index < sessions.Count; index++)
            {
                if (string.Equals(names[index], getDisplayName(sessions[index]), StringComparison.OrdinalIgnoreCase))
                    resolved[index] = assigned.Add(names[index]);
            }

            var nextSuffix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < sessions.Count; index++)
            {
                if (resolved[index]) continue;
                string name = names[index];
                if (!groups.ContainsKey(name) && assigned.Add(name)) continue;

                int suffix = nextSuffix.GetValueOrDefault(name, 2);
                string candidate;
                do { candidate = $"{name} ({suffix++})"; }
                while (reserved.Contains(candidate) || !assigned.Add(candidate));
                nextSuffix[name] = suffix;
                names[index] = candidate;
            }

            return names;
        }
    }
}

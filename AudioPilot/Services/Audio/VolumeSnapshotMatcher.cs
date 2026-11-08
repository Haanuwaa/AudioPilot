using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed partial class VolumeSnapshotMatcher(
        Logger logger,
        ConcurrentDictionary<string, string> normalizedNameCache,
        ConcurrentBag<HashSet<string>> wordSetPool,
        int maxNormalizedNameCacheEntries,
        TimeSpan pidFallbackFreshness)
    {
        private const int MaxPooledWordSetCapacity = AppConstants.Limits.MaxPidProcessMapEntries;
        private readonly Logger _logger = logger;
        private readonly ConcurrentDictionary<string, string> _normalizedNameCache = normalizedNameCache;
        private readonly ConcurrentBag<HashSet<string>> _wordSetPool = wordSetPool;
        private readonly int _maxNormalizedNameCacheEntries = maxNormalizedNameCacheEntries;
        private readonly TimeSpan _pidFallbackFreshness = pidFallbackFreshness;

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();

        [GeneratedRegex(@"[^\w\s\-]")]
        private static partial Regex NonWordRegex();

        public string NormalizeForMatching(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            if (_normalizedNameCache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            string normalized = name.Trim();
            normalized = WhitespaceRegex().Replace(normalized, " ");
            normalized = NonWordRegex().Replace(normalized, string.Empty);
            normalized = AudioDeviceHelper.CapitalizeName(normalized);

            if (_normalizedNameCache.Count < _maxNormalizedNameCacheEntries)
            {
                _normalizedNameCache.TryAdd(name, normalized);
            }

            return normalized;
        }

        public static void IndexNormalizedWords(string normalizedValue, Dictionary<string, HashSet<string>> wordIndex)
        {
            int start = 0;
            ReadOnlySpan<char> span = normalizedValue.AsSpan();

            while (start < span.Length)
            {
                while (start < span.Length && span[start] == ' ')
                {
                    start++;
                }

                if (start >= span.Length)
                {
                    break;
                }

                int end = start;
                while (end < span.Length && span[end] != ' ')
                {
                    end++;
                }

                string word = span[start..end].ToString();
                if (!wordIndex.TryGetValue(word, out var entrySet))
                {
                    entrySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    wordIndex[word] = entrySet;
                }

                entrySet.Add(normalizedValue);
                start = end + 1;
            }
        }

        /// <summary>
        /// Bounds candidate work with the smallest word index and rejects ambiguous matches.
        /// </summary>
        public float? FindFuzzyMatch(ReadOnlySpan<char> normalizedName, SessionVolumeSnapshot snapshot)
        {
            if (snapshot.ByName.Count == 0 || snapshot.WordIndex.Count == 0)
            {
                return null;
            }

            if (snapshot.ByName.TryGetValue(normalizedName.ToString(), out float exact)) return exact;
            float? matchedVolume = null;
            var uniqueInputWords = RentWordSet();
            HashSet<string>? firstIndexedEntries = null;
            HashSet<string>? smallestEntries = null;
            HashSet<string>? candidates = null;
            HashSet<string>? candidateWords = null;
            try
            {
                foreach (Range range in normalizedName.Split(' '))
                {
                    ReadOnlySpan<char> word = normalizedName[range];
                    if (!word.IsEmpty)
                    {
                        uniqueInputWords.Add(word.ToString());
                    }
                }

                foreach (string word in uniqueInputWords)
                {
                    if (snapshot.WordIndex.TryGetValue(word, out var entries))
                    {
                        firstIndexedEntries ??= entries;
                        if (smallestEntries == null || entries.Count < smallestEntries.Count)
                        {
                            smallestEntries = entries;
                        }
                    }
                }

                if (smallestEntries == null || smallestEntries.Count == 0)
                {
                    return null;
                }

                candidates = RentWordSet();
                foreach (string entry in smallestEntries)
                {
                    candidates.Add(entry);
                }

                foreach (string word in uniqueInputWords)
                {
                    if (!snapshot.WordIndex.TryGetValue(word, out var entriesWithWord)
                        || ReferenceEquals(entriesWithWord, smallestEntries))
                    {
                        continue;
                    }

                    candidates.IntersectWith(entriesWithWord);
                    if (candidates.Count == 0)
                    {
                        return null;
                    }
                }

                var orderedCandidates = candidates.Count == 1 ? candidates : firstIndexedEntries!;
                foreach (var candidateName in orderedCandidates)
                {
                    if (!candidates.Contains(candidateName) || !snapshot.ByName.TryGetValue(candidateName, out float volume))
                    {
                        continue;
                    }

                    if (normalizedName.Equals(candidateName.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        return volume;
                    }

                    candidateWords ??= RentWordSet();
                    candidateWords.Clear();
                    int sharedWordCount = 0;
                    foreach (Range range in candidateName.AsSpan().Split(' '))
                    {
                        ReadOnlySpan<char> word = candidateName.AsSpan()[range];
                        if (word.IsEmpty) continue;
                        string value = word.ToString();
                        if (candidateWords.Add(value) && uniqueInputWords.Contains(value)) sharedWordCount++;
                    }
                    int candidateWordCount = candidateWords.Count;
                    if (candidateWordCount == 0)
                    {
                        continue;
                    }

                    float matchRatio = (float)sharedWordCount / (uniqueInputWords.Count + candidateWordCount - sharedWordCount);

                    if (matchRatio >= 0.5f)
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("VolumeControlService",
                                $"{AppConstants.Audio.LogEvents.Volume.FuzzyMatchRatio} | ratio={matchRatio:F2} input={LogPrivacy.Label(normalizedName.ToString())} candidate={LogPrivacy.Label(candidateName)}");
                        }
                        if (matchedVolume.HasValue) return null;
                        matchedVolume = volume;
                    }
                }

                return matchedVolume;
            }
            finally
            {
                ReturnWordSet(uniqueInputWords);
                ReturnWordSet(candidates);
                ReturnWordSet(candidateWords);
            }
        }

        public bool TryResolveSnapshotTarget(
            SessionVolumeSnapshot snapshot,
            uint pid,
            string? normalizedDisplayName,
            string? normalizedProcessName,
            DateTime? nowUtc,
            out float targetVolume,
            out string matchMethod)
        {
            targetVolume = 0f;
            matchMethod = string.Empty;
            DateTime resolvedNowUtc = nowUtc ?? DateTime.UtcNow;

            if (pid == 0)
            {
                if (snapshot.SystemSoundsVolumePercent.HasValue)
                {
                    targetVolume = snapshot.SystemSoundsVolumePercent.Value;
                    matchMethod = "SystemSounds";
                    return true;
                }

                return false;
            }

            bool hasProcess = !string.IsNullOrWhiteSpace(normalizedProcessName);
            if (hasProcess && snapshot.ByProcess.TryGetValue(normalizedProcessName!, out float byProcess))
            {
                targetVolume = byProcess;
                matchMethod = "ProcessName";
                return true;
            }

            if (hasProcess && snapshot.ByProcess.Count != 0) return false;
            TimeSpan age = resolvedNowUtc - snapshot.SnapshotTime;
            if (age >= TimeSpan.Zero && age <= _pidFallbackFreshness && snapshot.ByPid.TryGetValue(pid, out float byPid))
            {
                targetVolume = byPid;
                matchMethod = "PID";
                return true;
            }

            if (hasProcess && snapshot.ByName.TryGetValue(normalizedProcessName!, out float exactProcess))
            {
                targetVolume = exactProcess;
                matchMethod = "ProcessName";
                return true;
            }
            if (!string.IsNullOrWhiteSpace(normalizedDisplayName))
            {
                if (snapshot.ByName.TryGetValue(normalizedDisplayName, out float exactName))
                {
                    targetVolume = exactName;
                    matchMethod = "Name";
                    return true;
                }
                float? fuzzy = FindFuzzyMatch(normalizedDisplayName.AsSpan(), snapshot);
                if (fuzzy.HasValue)
                {
                    targetVolume = fuzzy.Value;
                    matchMethod = "FuzzyName";
                    return true;
                }
            }

            return false;
        }

        private HashSet<string> RentWordSet()
        {
            if (_wordSetPool.TryTake(out var set))
            {
                set.Clear();
                return set;
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private void ReturnWordSet(HashSet<string>? set)
        {
            if (set == null)
            {
                return;
            }

            int capacity = set.EnsureCapacity(0);
            set.Clear();
            if (capacity > MaxPooledWordSetCapacity)
            {
                return;
            }

            _wordSetPool.Add(set);
        }
    }
}

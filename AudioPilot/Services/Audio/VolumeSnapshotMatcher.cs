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

        public float? FindFuzzyMatch(ReadOnlySpan<char> normalizedName, SessionVolumeSnapshot snapshot)
        {
            if (snapshot.ByName.Count == 0 || snapshot.WordIndex.Count == 0)
            {
                return null;
            }

            int inputWordCount = 0;
            var uniqueInputWords = RentWordSet();
            HashSet<string>? candidates = null;
            try
            {
                int spacePos = 0;
                while (true)
                {
                    int nextSpace = normalizedName[spacePos..].IndexOf(' ');
                    if (nextSpace < 0)
                    {
                        if (spacePos < normalizedName.Length)
                        {
                            inputWordCount++;
                            uniqueInputWords.Add(normalizedName[spacePos..].ToString());
                        }

                        break;
                    }

                    if (nextSpace > 0)
                    {
                        inputWordCount++;
                        uniqueInputWords.Add(normalizedName[spacePos..(spacePos + nextSpace)].ToString());
                    }

                    spacePos += nextSpace + 1;
                    while (spacePos < normalizedName.Length && normalizedName[spacePos] == ' ')
                    {
                        spacePos++;
                    }

                    if (spacePos >= normalizedName.Length)
                    {
                        break;
                    }
                }

                if (inputWordCount == 0 || uniqueInputWords.Count == 0)
                {
                    return null;
                }

                foreach (var word in uniqueInputWords)
                {
                    if (snapshot.WordIndex.TryGetValue(word, out var entriesWithWord))
                    {
                        if (candidates == null)
                        {
                            candidates = RentWordSet();
                            foreach (var entry in entriesWithWord)
                            {
                                candidates.Add(entry);
                            }
                        }
                        else
                        {
                            candidates.IntersectWith(entriesWithWord);
                        }

                        if (candidates.Count == 0)
                        {
                            return null;
                        }
                    }
                }

                if (candidates == null || candidates.Count == 0)
                {
                    return null;
                }

                foreach (var candidateName in candidates)
                {
                    if (!snapshot.ByName.TryGetValue(candidateName, out float volume))
                    {
                        continue;
                    }

                    if (normalizedName.Equals(candidateName.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        return volume;
                    }

                    int candidateWordCount = CountWords(candidateName.AsSpan());
                    if (candidateWordCount == 0)
                    {
                        continue;
                    }

                    float matchRatio = (float)inputWordCount / Math.Max(inputWordCount, candidateWordCount);

                    if (matchRatio >= 0.5f)
                    {
                        string inputLabel = LogPrivacy.Label(normalizedName.ToString());
                        _logger.Trace("VolumeControlService",
                            () => $"{AppConstants.Audio.LogEvents.Volume.FuzzyMatchRatio} | ratio={matchRatio:F2} input={inputLabel} candidate={LogPrivacy.Label(candidateName)}");
                        return volume;
                    }
                }

                return null;
            }
            finally
            {
                ReturnWordSet(uniqueInputWords);
                ReturnWordSet(candidates);
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

            if (!string.IsNullOrWhiteSpace(normalizedDisplayName))
            {
                if (snapshot.ByName.TryGetValue(normalizedDisplayName, out float exactByDisplay))
                {
                    targetVolume = exactByDisplay;
                    matchMethod = "Name";
                    return true;
                }

                float? fuzzyByDisplay = FindFuzzyMatch(normalizedDisplayName.AsSpan(), snapshot);
                if (fuzzyByDisplay.HasValue)
                {
                    targetVolume = fuzzyByDisplay.Value;
                    matchMethod = "FuzzyName";
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(normalizedProcessName))
            {
                if (snapshot.ByName.TryGetValue(normalizedProcessName, out float exactByProcess))
                {
                    targetVolume = exactByProcess;
                    matchMethod = "ProcessName";
                    return true;
                }

                float? fuzzyByProcess = FindFuzzyMatch(normalizedProcessName.AsSpan(), snapshot);
                if (fuzzyByProcess.HasValue)
                {
                    targetVolume = fuzzyByProcess.Value;
                    matchMethod = "FuzzyProcess";
                    return true;
                }
            }

            if (snapshot.ByPid.TryGetValue(pid, out float pidVolume))
            {
                bool hasNameIdentity =
                    !string.IsNullOrWhiteSpace(normalizedDisplayName) ||
                    !string.IsNullOrWhiteSpace(normalizedProcessName);
                bool snapshotIsFresh = (resolvedNowUtc - snapshot.SnapshotTime) <= _pidFallbackFreshness;

                if (!hasNameIdentity || snapshotIsFresh)
                {
                    targetVolume = pidVolume;
                    matchMethod = "PID";
                    return true;
                }
            }

            return false;
        }

        private static int CountWords(ReadOnlySpan<char> value)
        {
            int count = 0;
            bool inWord = false;

            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] == ' ')
                {
                    inWord = false;
                    continue;
                }

                if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }

            return count;
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

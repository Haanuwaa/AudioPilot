namespace AudioPilot.Cli
{
    internal static class CliSuggestionHelper
    {
        public static string BuildUnknownValueError(string valueKind, string requestedValue, IReadOnlyList<string> knownValues, string usageSuffix)
        {
            string? suggestion = FindBestMatch(requestedValue, knownValues);
            string suggestionText = suggestion == null ? string.Empty : $" Did you mean '{suggestion}'?";
            return $"Unknown {valueKind} '{requestedValue}'.{suggestionText} Use: {usageSuffix}";
        }

        public static string BuildUnknownKeyError(string keyKind, string requestedKey, IReadOnlyList<string> knownKeys)
        {
            string? suggestion = FindBestMatch(requestedKey, knownKeys);
            return suggestion == null
                ? $"Unknown {keyKind} key '{requestedKey}'."
                : $"Unknown {keyKind} key '{requestedKey}'. Did you mean '{suggestion}'?";
        }

        public static string? FindBestMatch(string requestedKey, IReadOnlyList<string> knownKeys)
        {
            if (string.IsNullOrWhiteSpace(requestedKey) || knownKeys.Count == 0)
            {
                return null;
            }

            string normalizedRequested = NormalizeKey(requestedKey);

            string? prefixMatch = knownKeys
                .FirstOrDefault(candidate => NormalizeKey(candidate).StartsWith(normalizedRequested, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(prefixMatch))
            {
                return prefixMatch;
            }

            string? containsMatch = knownKeys
                .FirstOrDefault(candidate => NormalizeKey(candidate).Contains(normalizedRequested, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(containsMatch))
            {
                return containsMatch;
            }

            string? bestCandidate = null;
            int bestDistance = int.MaxValue;
            foreach (string candidate in knownKeys)
            {
                int distance = ComputeLevenshteinDistance(normalizedRequested, NormalizeKey(candidate));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCandidate = candidate;
                }
            }

            int threshold = Math.Max(2, normalizedRequested.Length / 3);
            return bestDistance <= threshold ? bestCandidate : null;
        }

        private static string NormalizeKey(string key)
        {
            return key.Trim().ToLowerInvariant();
        }

        private static int ComputeLevenshteinDistance(string left, string right)
        {
            if (left.Length == 0)
            {
                return right.Length;
            }

            if (right.Length == 0)
            {
                return left.Length;
            }

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];

            for (int column = 0; column <= right.Length; column++)
            {
                previous[column] = column;
            }

            for (int row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (int column = 1; column <= right.Length; column++)
                {
                    int substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = Math.Min(
                        Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + substitutionCost);
                }

                (previous, current) = (current, previous);
            }

            return previous[right.Length];
        }
    }
}

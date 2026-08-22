using System.Windows.Input;

namespace AudioPilot.Services.Hotkeys
{
    internal static class HotkeyStandaloneKeyPolicy
    {
        public const int MaxAdditionalStandaloneKeys = 8;

        private static readonly HashSet<Key> AdditionalStandaloneCandidateKeys =
        [
            Key.PrintScreen,
            Key.Pause,
            Key.Scroll,
            Key.Insert,
            Key.Home,
            Key.End,
            Key.PageUp,
            Key.PageDown,
            Key.Delete,
            Key.NumLock,
        ];

        internal readonly record struct Analysis(
            IReadOnlyList<string> EffectiveTokens,
            IReadOnlyList<string> InvalidTokens,
            int DistinctValidCount)
        {
            public bool ExceedsLimit => DistinctValidCount > MaxAdditionalStandaloneKeys;
            public bool HasIssues => InvalidTokens.Count > 0 || ExceedsLimit;
        }

        public static Analysis Analyze(IEnumerable<string>? configuredTokens)
        {
            var effectiveTokens = new List<string>();
            var invalidTokens = new List<string>();
            var seenValid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenInvalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? rawToken in configuredTokens ?? [])
            {
                string trimmed = rawToken?.Trim() ?? string.Empty;
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!TryNormalizeConfiguredToken(trimmed, out string normalized))
                {
                    if (seenInvalid.Add(trimmed))
                    {
                        invalidTokens.Add(trimmed);
                    }

                    continue;
                }

                if (!seenValid.Add(normalized))
                {
                    continue;
                }

                if (effectiveTokens.Count < MaxAdditionalStandaloneKeys)
                {
                    effectiveTokens.Add(normalized);
                }
            }

            return new Analysis(effectiveTokens, invalidTokens, seenValid.Count);
        }

        public static bool IsStandaloneWithoutModifierAllowed(HotkeyMainInput input, IEnumerable<string>? configuredTokens)
        {
            if (!input.HasValue)
            {
                return false;
            }

            if (input.AllowsStandaloneWithoutModifierByDefault)
            {
                return true;
            }

            if (!IsAdditionalStandaloneCandidate(input))
            {
                return false;
            }

            string serializationToken = GetCanonicalConfiguredToken(input);
            foreach (string configuredToken in Analyze(configuredTokens).EffectiveTokens)
            {
                if (string.Equals(configuredToken, serializationToken, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryParseCliValue(string rawValue, out List<string> tokens, out string? error)
        {
            tokens = [];
            error = null;

            string trimmed = rawValue?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string[] parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Analysis analysis = Analyze(parts);
            if (analysis.InvalidTokens.Count > 0)
            {
                error = $"Invalid additional standalone hotkey keys: {string.Join(", ", analysis.InvalidTokens)}. Allowed values include PrintScreen, Pause, Scroll, Insert, Home, End, PageUp, PageDown, Delete, and NumLock.";
                return false;
            }

            if (analysis.ExceedsLimit)
            {
                error = $"Too many additional standalone hotkey keys. Use at most {MaxAdditionalStandaloneKeys}.";
                return false;
            }

            tokens = [.. analysis.EffectiveTokens];
            return true;
        }

        public static bool TryNormalizeConfiguredToken(string rawToken, out string normalizedToken)
        {
            normalizedToken = string.Empty;

            var parsed = new HotkeyParsingService().ParseHotkeyString(rawToken);
            if (!parsed.HasValue || parsed.Value.modifiers.Count > 0)
            {
                return false;
            }

            HotkeyMainInput input = parsed.Value.mainInput;
            if (!IsAdditionalStandaloneCandidate(input))
            {
                return false;
            }

            normalizedToken = GetCanonicalConfiguredToken(input);
            return normalizedToken.Length > 0;
        }

        private static string GetCanonicalConfiguredToken(HotkeyMainInput input)
        {
            if (input.Kind != HotkeyMainInputKind.Keyboard)
            {
                return input.SerializationToken;
            }

            return input.Key switch
            {
                Key.PrintScreen => "PrintScreen",
                _ => input.SerializationToken,
            };
        }

        private static bool IsAdditionalStandaloneCandidate(HotkeyMainInput input)
        {
            return input.Kind == HotkeyMainInputKind.Keyboard
                && AdditionalStandaloneCandidateKeys.Contains(input.Key);
        }
    }
}

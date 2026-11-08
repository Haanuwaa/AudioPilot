using AudioPilot.Models;

namespace AudioPilot.Cli
{
    internal enum CliDeviceSelectorKind
    {
        Raw = 0,
        ExactId,
        ExactName,
    }

    internal readonly record struct CliDeviceSelectorQuery(string Value, CliDeviceSelectorKind Kind);

    internal readonly record struct CliDeviceSelectorResolution(
        bool Success,
        bool Ambiguous,
        string Selector,
        CycleDevice? Device,
        IReadOnlyList<CycleDevice> Matches)
    {
        public static CliDeviceSelectorResolution NotFound(string selector) => new(false, false, selector, null, []);

        public static CliDeviceSelectorResolution AmbiguousMatch(string selector, IReadOnlyList<CycleDevice> matches) => new(false, true, selector, null, matches);

        public static CliDeviceSelectorResolution Matched(string selector, CycleDevice device) => new(true, false, selector, device, [device]);
    }

    internal static class CliDeviceSelectorResolver
    {
        private const string ExactIdPrefix = "[id]";
        private const string ExactNamePrefix = "[name]";

        public static string EncodeExactId(string deviceId) => ExactIdPrefix + (deviceId?.Trim() ?? string.Empty);

        public static string EncodeExactName(string deviceName) => ExactNamePrefix + (deviceName?.Trim() ?? string.Empty);

        public static CliDeviceSelectorQuery Decode(string? selectorSpec)
        {
            string normalized = selectorSpec?.Trim() ?? string.Empty;
            if (normalized.StartsWith(ExactIdPrefix, StringComparison.Ordinal))
            {
                return new CliDeviceSelectorQuery(normalized[ExactIdPrefix.Length..].Trim(), CliDeviceSelectorKind.ExactId);
            }

            if (normalized.StartsWith(ExactNamePrefix, StringComparison.Ordinal))
            {
                return new CliDeviceSelectorQuery(normalized[ExactNamePrefix.Length..].Trim(), CliDeviceSelectorKind.ExactName);
            }

            return new CliDeviceSelectorQuery(normalized, CliDeviceSelectorKind.Raw);
        }

        public static CliDeviceSelectorResolution ResolveExact(IReadOnlyList<CycleDevice> devices, string? selectorSpec)
        {
            CliDeviceSelectorQuery query = Decode(selectorSpec);
            if (string.IsNullOrWhiteSpace(query.Value))
            {
                return CliDeviceSelectorResolution.NotFound(string.Empty);
            }

            if (query.Kind is CliDeviceSelectorKind.Raw or CliDeviceSelectorKind.ExactId)
            {
                CycleDevice? idMatch = devices.FirstOrDefault(device => string.Equals(device.Id, query.Value, StringComparison.OrdinalIgnoreCase));
                if (idMatch != null)
                {
                    return CliDeviceSelectorResolution.Matched(query.Value, idMatch);
                }

                if (query.Kind == CliDeviceSelectorKind.ExactId)
                {
                    return CliDeviceSelectorResolution.NotFound(query.Value);
                }
            }

            List<CycleDevice> nameMatches = [.. devices.Where(device => string.Equals(device.Name, query.Value, StringComparison.OrdinalIgnoreCase))];
            if (nameMatches.Count == 1)
            {
                return CliDeviceSelectorResolution.Matched(query.Value, nameMatches[0]);
            }

            if (nameMatches.Count > 1)
            {
                return CliDeviceSelectorResolution.AmbiguousMatch(query.Value, nameMatches);
            }

            return CliDeviceSelectorResolution.NotFound(query.Value);
        }

        public static IReadOnlyList<CycleDevice> FindMatches(IReadOnlyList<CycleDevice> devices, string? query)
        {
            string normalized = query?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return [];
            }

            return [.. devices
                .Where(device =>
                    device.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(device => GetMatchRank(device, normalized))
                .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)];
        }

        public static string BuildNotFoundMessage(string kind, string selector)
        {
            return $"No active {kind} device matched '{selector}'.";
        }

        public static string BuildAmbiguousMessage(string kind, string selector, IReadOnlyList<CycleDevice> matches)
        {
            string matchingIds = string.Join(", ", matches.Select(static match => match.Id));
            return $"{kind} device selector '{selector}' is ambiguous. Matching IDs: {matchingIds}.";
        }

        private static int GetMatchRank(CycleDevice device, string query)
        {
            if (string.Equals(device.Id, query, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(device.Name, query, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (device.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (device.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (device.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }

            return 5;
        }
    }
}

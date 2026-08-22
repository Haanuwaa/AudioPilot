namespace AudioPilot.Cli
{
    internal static class CliValueParser
    {
        internal static bool TryParseBool(string value, out bool parsed)
        {
            string normalized = value.Trim();
            if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase))
            {
                parsed = true;
                return true;
            }

            if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase))
            {
                parsed = false;
                return true;
            }

            return bool.TryParse(normalized, out parsed);
        }
    }
}

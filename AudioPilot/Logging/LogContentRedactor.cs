using System.Text.RegularExpressions;

namespace AudioPilot.Logging
{
    internal static partial class LogContentRedactor
    {
        internal static string Sanitize(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            string sanitized = AbsoluteWindowsPathWithExtensionRegex().Replace(content, "<path>");
            sanitized = AbsoluteWindowsPathRegex().Replace(sanitized, "<path>");
            sanitized = QuotedLiteralRegex().Replace(sanitized, static match => $"'{LogPrivacy.RedactedLabel(match.Groups[1].Value)}'");
            return sanitized;
        }

        [GeneratedRegex("(?i)(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\r\\n,;'\\\"]*?\\.[A-Za-z0-9]{1,8}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
        private static partial Regex AbsoluteWindowsPathWithExtensionRegex();

        [GeneratedRegex("(?i)(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\s,;'\\\"]+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
        private static partial Regex AbsoluteWindowsPathRegex();

        [GeneratedRegex("'([^']+)'", RegexOptions.Compiled)]
        private static partial Regex QuotedLiteralRegex();
    }
}

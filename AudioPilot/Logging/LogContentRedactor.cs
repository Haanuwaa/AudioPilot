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
            sanitized = PrivacyLabelRegex().Replace(sanitized, static match =>
                $"{match.Groups[1].Value}[{RedactValue(match.Groups[2].Value)}]");
            sanitized = MediaMetadataRegex().Replace(sanitized, static match =>
                $"{match.Groups[1].Value}='{RedactValue(match.Groups[2].Value)}'");
            sanitized = QuotedLiteralRegex().Replace(sanitized, static match =>
                SafeMediaMetricRegex().IsMatch(match.Value)
                    ? match.Value
                    : $"{match.Groups[1].Value}'{RedactValue(match.Groups[2].Value)}'");
            return sanitized;
        }

        /// <summary>Preserves existing anonymized identities so exported logs retain cross-entry correlation.</summary>
        private static string RedactValue(string value) =>
            RedactedValueRegex().IsMatch(value) ? value : LogPrivacy.RedactedLabel(value);

        [GeneratedRegex("(?i)(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\r\\n,;'\\\"]*?\\.[A-Za-z0-9]{1,8}\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
        private static partial Regex AbsoluteWindowsPathWithExtensionRegex();

        [GeneratedRegex("(?i)(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\s,;'\\\"]+", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
        private static partial Regex AbsoluteWindowsPathRegex();

        [GeneratedRegex(@"\b(device|process|session|id)\[([^\]\r\n]*)\]", RegexOptions.CultureInvariant)]
        private static partial Regex PrivacyLabelRegex();

        /// <summary>Uses snapshot field boundaries to handle apostrophes inside track titles, artists, and albums.</summary>
        [GeneratedRegex(@"\b(title|artist|album|source)='([^\r\n]*?)'(?=, (?:artist|album|source|positionSec)=')", RegexOptions.CultureInvariant)]
        private static partial Regex MediaMetadataRegex();

        [GeneratedRegex(@"(\b(?:positionSec|status)=)?'([^'\r\n]+)'", RegexOptions.CultureInvariant)]
        private static partial Regex QuotedLiteralRegex();

        [GeneratedRegex(@"\A(?:positionSec='(?:[+-]?[0-9]+(?:[.,][0-9]+)?|<null>)'|status='(?:Closed|Opened|Changing|Stopped|Playing|Paused)')\z", RegexOptions.CultureInvariant)]
        private static partial Regex SafeMediaMetricRegex();

        [GeneratedRegex(@"\A(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>|<path>|(?:device|process|session|id)\[(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<path>)\])\z", RegexOptions.CultureInvariant)]
        private static partial Regex RedactedValueRegex();
    }
}

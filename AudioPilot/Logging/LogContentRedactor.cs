using System.Text.RegularExpressions;

namespace AudioPilot.Logging
{
    internal static partial class LogContentRedactor
    {
        internal static string Sanitize(string content) => Sanitize(content, RedactValue);

        internal static string Sanitize(string content, Func<string, string> redactQuotedValue)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            string sanitized = AbsoluteWindowsPathRegex().Replace(content, "<path>");
            sanitized = PrivacyLabelRegex().Replace(sanitized, static match =>
                match.Groups["safe"].Success ? match.Value : $"{match.Groups["kind"].Value}[{RedactValue(match.Groups["raw"].Value)}]");
            sanitized = MediaMetadataRegex().Replace(sanitized, static match =>
                $"{match.Groups[1].Value}='{RedactValue(match.Groups[2].Value)}'");
            sanitized = SensitiveFieldRegex().Replace(sanitized, static match =>
                match.Groups["safe"].Success ? match.Value : $"{match.Groups["prefix"].Value}{RedactValue(match.Groups["raw"].Value)}");
            sanitized = ExceptionMessageRegex().Replace(sanitized, static match =>
                $"{match.Groups[1].Value}{RedactValue(match.Groups[2].Value)}");
            sanitized = UnsupportedPropertyRegex().Replace(sanitized, static match =>
                $"{match.Groups[1].Value}{RedactValue(match.Groups[2].Value)}");
            sanitized = QuotedLiteralRegex().Replace(sanitized, match =>
                match.Groups["safe"].Success
                    ? match.Value
                    : $"'{redactQuotedValue(match.Groups["raw"].Value)}'");
            return sanitized;
        }

        /// <summary>Preserves existing anonymized identities so exported logs retain cross-entry correlation.</summary>
        private static string RedactValue(string value) =>
            RedactedValueRegex().IsMatch(value) ? value : LogPrivacy.RedactedLabel(value);

        [GeneratedRegex("""(?:(?<!\w)[A-Za-z]:[\\/]|\\\\|(?<!:)//)[^\r\n"]*?(?=;\s(?:reason|error|status|opId)=|["\r\n]|$)""", RegexOptions.CultureInvariant)]
        private static partial Regex AbsoluteWindowsPathRegex();

        [GeneratedRegex(@"\b(?<kind>device|process|session|id|networks)\[(?:(?<safe>len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>|<path>)\]|(?<raw>[^\r\n]*)\])", RegexOptions.CultureInvariant)]
        private static partial Regex PrivacyLabelRegex();

        /// <summary>Uses snapshot field boundaries to handle apostrophes inside track titles, artists, and albums.</summary>
        [GeneratedRegex(@"\b(title|artist|album|source)='([^\r\n]*?)'(?=, (?:artist|album|source|positionSec)=')", RegexOptions.CultureInvariant)]
        private static partial Regex MediaMetadataRegex();

        /// <summary>Preserves sanitized fields and numeric media metrics; ambiguous raw quotes are consumed through the last quote on their line.</summary>
        [GeneratedRegex(@"(?<safe>(?:positionSec='(?:[+-]?[0-9]+(?:[.,][0-9]+)?|<null>)'|status='(?:Closed|Opened|Changing|Stopped|Playing|Paused)'|'(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>|<path>|[a-z-]+\[len=[0-9]+ hash=[A-F0-9]{8}\])'))|'(?<raw>[^\r\n]*)'", RegexOptions.CultureInvariant)]
        private static partial Regex QuotedLiteralRegex();

        /// <summary>Old raw logs do not escape field delimiters; consume ambiguous sensitive tails rather than exposing part of a name.</summary>
        [GeneratedRegex(@"(?<prefix>\b(?:title|pattern|routineName|group|normalizedExpected|input|candidate|description|hotkey|exceptionMessage|loggingExceptionMessage)=)(?:(?<safe>'(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>)'|len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>)(?=[,\s]|$)|(?<raw>[^\r\n]*))", RegexOptions.CultureInvariant)]
        private static partial Regex SensitiveFieldRegex();

        [GeneratedRegex(@"(\b(?:Inner exception|Exception): [^:\r\n]+: )([^\r\n]*)", RegexOptions.CultureInvariant)]
        private static partial Regex ExceptionMessageRegex();

        [GeneratedRegex(@"(\bunsupported (?:top-level )?property: )([^\r\n]*)", RegexOptions.CultureInvariant)]
        private static partial Regex UnsupportedPropertyRegex();

        [GeneratedRegex(@"\A(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>|<path>|(?:device|process|session|id)\[(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<path>)\])\z", RegexOptions.CultureInvariant)]
        private static partial Regex RedactedValueRegex();
    }
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AudioPilot.Logging
{
    internal static partial class LogContentRedactor
    {
        internal const string RecordHeaderPattern = @"\[[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}\] \[";

        internal static string Sanitize(string content) => Sanitize(content, RedactValue);

        internal static string Sanitize(string content, Func<string, string> redactQuotedValue) => Sanitize(content, redactQuotedValue, null);

        internal static string SanitizeBatch(string content, StringBuilder scratch) => Sanitize(content, RedactValue, scratch);

        private static string Sanitize(string content, Func<string, string> redactQuotedValue, StringBuilder? scratch)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            string sanitized = SanitizeExceptions(content, scratch);
            sanitized = FramedRawValueRegex().Replace(sanitized, static match =>
            {
                try { return LogPrivacy.RedactedLabel(JsonSerializer.Deserialize<string>(match.Groups[1].Value)); }
                catch (JsonException) { return LogPrivacy.RedactedLabel(match.Value); }
            });
            sanitized = AbsoluteWindowsPathRegex().Replace(sanitized, "<path>");
            sanitized = SanitizePrivacyLabels(sanitized, scratch);
            sanitized = MediaMetadataRegex().Replace(sanitized, static match =>
                $"{match.Groups[1].Value}='{RedactValue(match.Groups[2].Value)}'");
            sanitized = SensitiveFieldRegex().Replace(sanitized, static match =>
                match.Groups["safe"].Success ? match.Value : $"{match.Groups["prefix"].Value}{RedactValue(match.Groups["raw"].Value)}");
            sanitized = UnsupportedPropertyRegex().Replace(sanitized, static match =>
                $"{match.Groups[1].Value}{RedactValue(match.Groups[2].Value)}");
            sanitized = QuotedLiteralRegex().Replace(sanitized, match =>
                match.Groups["safe"].Success
                    ? match.Value
                    : $"'{redactQuotedValue(match.Groups["raw"].Value)}'");
            return sanitized;
        }

        private static string SanitizeExceptions(string content, StringBuilder? scratch)
        {
            StringBuilder? result = null;
            int copied = 0;
            foreach (var match in ExceptionMessageRegex().EnumerateMatches(content))
            {
                ReadOnlySpan<char> entry = content.AsSpan(match.Index, match.Length);
                // The regex has already matched "Exception: " and the colon after the type.
                int separator = entry.IndexOf(": ", StringComparison.Ordinal) + 2;
                int messageStart = separator + entry[separator..].IndexOf(": ", StringComparison.Ordinal) + 2;
                ReadOnlySpan<char> message = entry[messageStart..];
                if (RedactedValueRegex().IsMatch(message)) continue;

                result ??= scratch?.Clear() ?? new StringBuilder();
                result.Append(content.AsSpan(copied, match.Index + messageStart - copied));
                result.Append(LogPrivacy.RedactedLabel(message));
                copied = match.Index + match.Length;
            }
            return result == null ? content : result.Append(content.AsSpan(copied)).ToString();
        }

        private static string SanitizePrivacyLabels(string content, StringBuilder? scratch)
        {
            StringBuilder? result = null;
            int copied = 0;
            foreach (var match in PrivacyLabelRegex().EnumerateMatches(content))
            {
                ReadOnlySpan<char> label = content.AsSpan(match.Index, match.Length);
                int valueStart = label.IndexOf('[') + 1;
                ReadOnlySpan<char> value = label[valueStart..^1];
                if (RedactedValueRegex().IsMatch(value)) continue;

                result ??= scratch?.Clear() ?? new StringBuilder();
                result.Append(content.AsSpan(copied, match.Index + valueStart - copied));
                result.Append(LogPrivacy.RedactedLabel(value));
                copied = match.Index + match.Length - 1; // The closing bracket is copied with the untouched text.
            }
            return result == null ? content : result.Append(content.AsSpan(copied)).ToString();
        }

        /// <summary>Preserves existing anonymized identities so exported logs retain cross-entry correlation.</summary>
        private static string RedactValue(string value) =>
            RedactedValueRegex().IsMatch(value) ? value : LogPrivacy.RedactedLabel(value);

        [GeneratedRegex("""(?:(?<!\w)[A-Za-z]:[\\/]|\\\\|(?<!:)//)[^\r\n"]*?(?=;\s(?:reason|error|status|opId)=|["\r\n]|$)""", RegexOptions.CultureInvariant)]
        private static partial Regex AbsoluteWindowsPathRegex();

        [GeneratedRegex(@"\b(?<kind>device|process|session|id|networks)\[(?:(?<safe>len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>|<path>)\](?=[ \t]*(?:$|[,;|]?[ \t]*[a-zA-Z][a-zA-Z0-9]*=))|(?<raw>[^\r\n]*)\])", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
        private static partial Regex PrivacyLabelRegex();

        /// <summary>Uses snapshot field boundaries to handle apostrophes inside track titles, artists, and albums.</summary>
        [GeneratedRegex(@"\b(title|artist|album|source)='([^\r\n]*?)'(?=, (?:artist|album|source|positionSec)=')", RegexOptions.CultureInvariant)]
        private static partial Regex MediaMetadataRegex();

        /// <summary>Preserves sanitized fields and numeric media metrics; ambiguous raw quotes are consumed through the last quote on their line.</summary>
        [GeneratedRegex(@"(?<safe>(?:positionSec='(?:[+-]?[0-9]+(?:[.,][0-9]+)?|<null>)'|status='(?:Closed|Opened|Changing|Stopped|Playing|Paused)'|'(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>|<path>|[a-z-]+\[len=[0-9]+ hash=[A-F0-9]{8}\])'))(?=[^'\r\n]*(?:[\r\n]|\z)|,\s+[a-zA-Z][a-zA-Z0-9]*=)|'(?<raw>[^\r\n]*)'", RegexOptions.CultureInvariant)]
        private static partial Regex QuotedLiteralRegex();

        /// <summary>Old raw logs do not escape field delimiters; consume ambiguous sensitive tails rather than exposing part of a name.</summary>
        [GeneratedRegex(@"(?<prefix>\b(?:title|pattern|routineName|group|normalizedExpected|input|candidate|description|hotkey|exceptionMessage|loggingExceptionMessage)=)(?:(?<safe>'(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>)'|len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>)(?=[ \t]*(?:$|[,;|]?[ \t]*[a-zA-Z][a-zA-Z0-9]*=))|(?<raw>[^\r\n]*))", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
        private static partial Regex SensitiveFieldRegex();

        /// <summary>Legacy exception continuations have no trusted delimiter; redact their whole body up to the next entry or exception header.</summary>
        [GeneratedRegex(@"(\b(?:Inner exception|Exception): [^:\r\n]+: )([^\r\n]*(?:(?:\r\n|\r|\n)(?![ \t]*(?:Inner exception|Exception): |" + RecordHeaderPattern + @")[^\r\n]*)*)", RegexOptions.CultureInvariant)]
        private static partial Regex ExceptionMessageRegex();

        [GeneratedRegex("""raw:("(?:\\.|[^"\\\r\n])*")|raw:"[^\r\n]*""", RegexOptions.CultureInvariant)]
        private static partial Regex FramedRawValueRegex();

        [GeneratedRegex(@"(\bunsupported (?:top-level )?property: )([^\r\n]*)", RegexOptions.CultureInvariant)]
        private static partial Regex UnsupportedPropertyRegex();

        [GeneratedRegex(@"\A(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<null>|<path>|(?:device|process|session|id)\[(?:len=[0-9]+ hash=[A-F0-9]{8}|<empty>|<path>)\])\z", RegexOptions.CultureInvariant)]
        private static partial Regex RedactedValueRegex();
    }
}

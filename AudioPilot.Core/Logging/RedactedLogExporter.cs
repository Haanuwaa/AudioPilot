using System.Text;
using System.Text.RegularExpressions;

namespace AudioPilot.Logging;

/// <summary>Copies redacted log entries without buffering all the decoded text for ordinary log files.</summary>
internal sealed partial class RedactedLogExporter
{
    private const int BatchCharacters = 128 * 1024;
    private const int HeaderLength = 27;
    private static readonly UTF8Encoding OutputEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private char[]? _input;
    private StringBuilder? _scratch;

    [GeneratedRegex(@"\A" + LogContentRedactor.RecordHeaderPattern, RegexOptions.CultureInvariant)]
    private static partial Regex RecordHeaderRegex();

    internal long Copy(Stream source, Stream destination)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var writer = new StreamWriter(destination, OutputEncoding, leaveOpen: true);
        if (source.CanSeek && source.Length < BatchCharacters)
            return Write(writer, LogContentRedactor.Sanitize(reader.ReadToEnd()));

        char[] input = _input ??= new char[BatchCharacters];
        int buffered = 0;
        long bytes = 0;
        while (true)
        {
            buffered += reader.ReadBlock(input.AsSpan(buffered));
            if (buffered < input.Length)
            {
                string remainder = new(input, 0, buffered);
                string sanitized = _scratch == null ? LogContentRedactor.Sanitize(remainder) : LogContentRedactor.SanitizeBatch(remainder, _scratch);
                return bytes + Write(writer, sanitized);
            }

            int boundary = FindRecordBoundary(input);
            if (boundary == 0)
                return bytes + WriteRemainder(reader, writer, input);

            // Keep the next real header visible to the exception redactor so it handles the
            // preceding newline exactly as it would in the whole file. Emit the header with the next batch.
            ReadOnlySpan<char> nextHeader = input.AsSpan(boundary, HeaderLength);
            string redacted = SanitizeBatch(new string(input, 0, boundary + HeaderLength));
            if (!redacted.AsSpan().EndsWith(nextHeader, StringComparison.Ordinal))
                return bytes + WriteRemainder(reader, writer, input);

            bytes += Write(writer, redacted.AsSpan(0, redacted.Length - HeaderLength));
            buffered = input.Length - boundary;
            input.AsSpan(boundary, buffered).CopyTo(input);
        }
    }

    private string SanitizeBatch(string content) => LogContentRedactor.SanitizeBatch(content, _scratch ??= new StringBuilder(BatchCharacters));

    private static int FindRecordBoundary(ReadOnlySpan<char> input)
    {
        int end = input.Length - HeaderLength;
        while (end > 0)
        {
            // Legacy backslash escapes can consume a bare carriage return in a raw value.
            int newline = input[..end].LastIndexOf('\n');
            if (newline < 0) return 0;
            int header = newline + 1;
            if (RecordHeaderRegex().IsMatch(input.Slice(header, HeaderLength))) return header;
            end = newline;
        }
        return 0;
    }

    private static long WriteRemainder(TextReader reader, TextWriter writer, char[] input)
    {
        var remainder = new StringBuilder();
        remainder.Append(input);
        int read;
        while ((read = reader.Read(input.AsSpan())) != 0)
            remainder.Append(input.AsSpan(0, read));
        return Write(writer, LogContentRedactor.Sanitize(remainder.ToString()));
    }

    private static long Write(TextWriter writer, ReadOnlySpan<char> content)
    {
        writer.Write(content);
        return OutputEncoding.GetByteCount(content);
    }
}

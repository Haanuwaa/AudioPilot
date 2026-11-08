using System.Text.RegularExpressions;

namespace AudioPilot.Localization;

/// <summary>Projects existing app messages for the UI while keeping the text used by logs and automation stable.</summary>
internal static partial class AppMessages
{
    public static string Translate(string message, LocalizationService localization)
    {
        if (message.Length == 0 || localization.EffectiveLanguage == "en") return message;
        if (!message.Contains('\n', StringComparison.Ordinal)) return TranslateLine(message, localization);
        return string.Join('\n', message.Split('\n').Select(line =>
        {
            bool crlf = line.EndsWith('\r');
            return crlf ? TranslateLine(line[..^1], localization) + "\r" : TranslateLine(line, localization);
        }));
    }

    internal static bool CanTranslateOverlayHeading(string heading) => FindKey(heading) != null
        || heading.StartsWith("Master volume ", StringComparison.Ordinal)
        || heading.StartsWith("Microphone volume ", StringComparison.Ordinal)
        || heading.StartsWith("App volume: ", StringComparison.Ordinal)
        || heading.StartsWith("Reconnecting input device (attempt ", StringComparison.Ordinal)
        || heading.StartsWith("Reconnecting output device (attempt ", StringComparison.Ordinal);

    private static string TranslateLine(string line, LocalizationService localization)
    {
        if (FindKey(line) is { } exact) return localization[exact];
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("• ", StringComparison.Ordinal))
            return line[..2] + TranslateLine(line[2..], localization);
        if (line.StartsWith("  Imported: ", StringComparison.Ordinal))
            return "  " + TranslateLine(line[2..], localization);
        if (line.StartsWith("Unsaved: ", StringComparison.Ordinal) && line.EndsWith('.'))
        {
            string labels = line["Unsaved: ".Length..^1];
            int moreIndex = labels.LastIndexOf(", and ", StringComparison.Ordinal);
            string more = string.Empty;
            int countStart = moreIndex + ", and ".Length;
            int countEnd = labels.Length - " more".Length;
            if (moreIndex >= 0 && countStart < countEnd && labels.EndsWith(" more", StringComparison.Ordinal) &&
                int.TryParse(labels.AsSpan(countStart, countEnd - countStart), out int count))
            {
                more = localization.FormatKey("Message_MoreSettingsChanges", count);
                labels = labels[..moreIndex];
            }
            return localization.FormatKey("Message_UnsavedChangesList",
                string.Join(", ", labels.Split(", ").Select(label => TranslateLine(label, localization))) + more);
        }
        if (line.StartsWith("Routine disabled. Next scheduled trigger:", StringComparison.Ordinal))
            return localization.FormatKey("Message_SchedulePrefix", TranslateLine("Routine disabled.", localization), TranslateLine(line["Routine disabled. ".Length..], localization));
        if (line.StartsWith("Automatic routines paused. Next scheduled trigger:", StringComparison.Ordinal))
            return localization.FormatKey("Message_SchedulePrefix", TranslateLine("Automatic routines paused.", localization), TranslateLine(line["Automatic routines paused. ".Length..], localization));

        foreach (var pattern in Patterns)
            if (pattern.TryTranslate(line, localization) is { } text) return text;
        return line;
    }

    private sealed partial class MessagePattern(string resource, int localizedArguments)
    {
        private readonly string _prefix = LocalizationService.English[resource].Split('{', 2)[0];
        private readonly Lazy<Regex> _matcher = new(() => new Regex(
            @"\A" + MyRegex().Replace(Regex.Escape(LocalizationService.English[resource]), @"(?<arg$1>.+?)") + @"\z",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking));

        public string? TryTranslate(string message, LocalizationService localization)
        {
            if (!message.StartsWith(_prefix, StringComparison.Ordinal)) return null;
            Match match = _matcher.Value.Match(message);
            if (!match.Success) return null;
            var arguments = new object?[match.Groups.Count - 1];
            for (int index = 0; index < arguments.Length; index++)
            {
                string value = match.Groups[$"arg{index}"].Value;
                arguments[index] = (localizedArguments & (1 << index)) != 0 ? TranslateLine(value, localization) : value;
            }
            return localization.FormatKey(resource, arguments);
        }

        [GeneratedRegex(@"\\\{([0-9]+)}")]
        private static partial Regex MyRegex();
    }
}

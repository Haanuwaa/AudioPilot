using System.Text;

namespace AudioPilot.Cli;

internal static partial class CliCommandHelpMetadata
{
    internal static string BuildOverviewHelp()
    {
        var text = new StringBuilder("AudioPilot CLI\nUsage:\n  AudioPilot.Cli.exe <command> [options]\n  AudioPilot.Cli.exe help <topic>|all\n\nTopics:\n");
        foreach (var family in CommandFamilies)
            text.AppendLine($"  {family.Topic,-12} {family.Summary}");
        text.AppendLine("\nExamples:");
        text.AppendLine("  AudioPilot.Cli.exe devices list output --json --redact");
        text.AppendLine("  AudioPilot.Cli.exe routine check --all --json --redact");
        text.AppendLine("\nNotes:");
        AppendNote(text, "Use help all for the complete command reference. Topic help includes syntax, behavior, and examples.");
        AppendNote(text, "In PowerShell, use .\\AudioPilot.Cli.exe from its folder, or add that folder to PATH.");
        AppendNote(text, "show, hide, startup open, runtime set, routine pause, and routine resume require a running AudioPilot instance. Other commands can run headlessly.");
        AppendNote(text, "Where supported, --json returns structured output and --redact masks sensitive values. JSON alone does not redact them.");
        AppendNote(text, "Exit codes: 0 success; 2 usage; 3 operation unavailable/failed; 4 forwarding failure; 5 precondition; 6 protocol; 7 runtime; 70 unexpected internal error. Inspect diagnostic codes before retrying a write.");
        return text.ToString().TrimEnd();
    }

    private static string BuildHelpText(CommandFamilyHelpMetadata family)
    {
        var text = new StringBuilder($"AudioPilot CLI - {family.Topic}\n{family.Summary}\n\nUsage:\n");
        foreach (string line in family.UsageLines) text.AppendLine($"  {line}");
        if (family.Notes.Length > 0)
        {
            text.AppendLine("\nNotes:");
            foreach (string note in family.Notes) AppendNote(text, note);
        }
        if (family.Examples is { Length: > 0 })
        {
            text.AppendLine("\nExamples:");
            foreach (string example in family.Examples) text.AppendLine($"  {example}");
        }
        return text.ToString().TrimEnd();
    }

    private static void AppendNote(StringBuilder text, string note)
    {
        const int contentWidth = 96;
        string indent = "  - ";
        while (note.Length > contentWidth)
        {
            int boundary = note.LastIndexOf(' ', contentWidth);
            if (boundary <= 0) break;
            text.AppendLine(indent + note[..boundary]);
            indent = "    ";
            note = note[(boundary + 1)..];
        }
        text.AppendLine(indent + note);
    }
}

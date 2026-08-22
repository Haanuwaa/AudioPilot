using System.Text;

namespace AudioPilot.Cli
{
    internal static class CliDocumentationMarkdownGenerator
    {
        private sealed record QuickReferenceGroup(string Heading, string[] Commands);
        private sealed record AutomationRecipe(string Title, string[] Lines);

        private static readonly QuickReferenceGroup[] QuickReferenceGroups =
        [
            new("Inspect state",
            [
                "AudioPilot.Cli.exe status --json",
                "AudioPilot.Cli.exe diagnostics status --json --redact",
                "AudioPilot.Cli.exe diagnostics history --limit 20 --json --redact",
                "AudioPilot.Cli.exe diagnostics history-detail <opId> --json --redact",
                "AudioPilot.Cli.exe diagnostics export-logs .\\artifacts\\logs.zip --json --redact --detail manifest",
                "AudioPilot.Cli.exe diagnostics export-bundle .\\artifacts\\support-bundle.zip --json --detail manifest",
                "AudioPilot.Cli.exe media status --json --redact",
            ]),
            new("Switch devices and listen state",
            [
                "AudioPilot.Cli.exe switch output",
                "AudioPilot.Cli.exe switch input --reverse",
                "AudioPilot.Cli.exe listen toggle --json",
            ]),
            new("Work with volume",
            [
                "AudioPilot.Cli.exe volume get master --json",
                "AudioPilot.Cli.exe volume set mic 25",
                "AudioPilot.Cli.exe volume get master --device \"Speakers\"",
                "AudioPilot.Cli.exe volume get master --device-id <playbackDeviceId>",
                "AudioPilot.Cli.exe volume set mic 15 --device-id <recordingDeviceId>",
            ]),
            new("Resolve devices and cycle entries",
            [
                "AudioPilot.Cli.exe devices get output \"Speakers\" --json",
                "AudioPilot.Cli.exe devices find input usb --json",
                "AudioPilot.Cli.exe cycle test output --json",
                "AudioPilot.Cli.exe cycle add output <deviceId>",
            ]),
            new("Work with routines",
            [
                "AudioPilot.Cli.exe routine list --json",
                "AudioPilot.Cli.exe network list --json --redact",
                "AudioPilot.Cli.exe routine create routine.json --allow-any-path",
                "AudioPilot.Cli.exe routine update routine-desk routine.json --allow-any-path",
                "AudioPilot.Cli.exe routine delete routine-desk",
                "AudioPilot.Cli.exe routine import routines.json --replace --allow-any-path",
                "AudioPilot.Cli.exe routine export routines.json --allow-any-path",
            ]),
            new("Persisted config and runtime tuning",
            [
                "AudioPilot.Cli.exe config list --json",
                "AudioPilot.Cli.exe runtime list --json",
                "AudioPilot.Cli.exe config get output-switch-hotkey --json",
                "AudioPilot.Cli.exe config get redact-log-content --json",
                "AudioPilot.Cli.exe config set overlay-position BottomCenter",
                "AudioPilot.Cli.exe config set play-dialog-sounds false",
                "AudioPilot.Cli.exe config set redact-log-content false",
            ]),
            new("Startup control",
            [
                "AudioPilot.Cli.exe startup status --json",
            ]),
        ];

        private static readonly AutomationRecipe[] AutomationRecipes =
        [
            new("Switch Then Validate",
            [
                "AudioPilot.Cli.exe switch output --json | Out-Null",
                "if ($LASTEXITCODE -ne 0) {",
                "  throw \"Output switch failed\"",
                "}",
                string.Empty,
                "$status = AudioPilot.Cli.exe status --json | ConvertFrom-Json",
                "$status.data.currentOutputDeviceName",
            ]),
            new("Export Config Safely",
            [
                "# Export under the current working directory so no path override is needed",
                "AudioPilot.Cli.exe config export .\\backup\\settings-export.json --json --redact",
                string.Empty,
                "if ($LASTEXITCODE -ne 0) {",
                "  throw \"Config export failed\"",
                "}",
            ]),
            new("Run A Routine From PowerShell",
            [
                "$result = AudioPilot.Cli.exe routine run routine-desk --json --redact | ConvertFrom-Json",
                string.Empty,
                "if ($LASTEXITCODE -ne 0) {",
                "  throw \"Routine run failed\"",
                "}",
                string.Empty,
                "$result.data.routine.id",
                "$result.data.targetSummary",
            ]),
        ];

        internal static string GenerateQuickReferenceSection()
        {
            var builder = new StringBuilder();
            builder.AppendLine("## Quick Reference");
            builder.AppendLine();
            builder.AppendLine("Common commands by task:");
            builder.AppendLine();
            builder.AppendLine("```powershell");

            for (int index = 0; index < QuickReferenceGroups.Length; index++)
            {
                QuickReferenceGroup group = QuickReferenceGroups[index];
                builder.AppendLine($"# {group.Heading}");
                foreach (string command in group.Commands)
                {
                    builder.AppendLine(command);
                }

                if (index < QuickReferenceGroups.Length - 1)
                {
                    builder.AppendLine();
                }
            }

            builder.AppendLine("```");
            builder.AppendLine();
            return builder.ToString().TrimEnd();
        }

        internal static string GenerateAutomationRecipesSection()
        {
            var builder = new StringBuilder();
            builder.AppendLine("## Common Automation Recipes");
            builder.AppendLine();
            builder.AppendLine("These examples are intentionally short end-to-end flows you can paste into a shell and adapt.");
            builder.AppendLine();

            for (int index = 0; index < AutomationRecipes.Length; index++)
            {
                AutomationRecipe recipe = AutomationRecipes[index];
                builder.AppendLine($"### {recipe.Title}");
                builder.AppendLine();
                builder.AppendLine("```powershell");
                foreach (string line in recipe.Lines)
                {
                    builder.AppendLine(line);
                }
                builder.AppendLine("```");

                if (index < AutomationRecipes.Length - 1)
                {
                    builder.AppendLine();
                }
            }

            return builder.ToString().TrimEnd();
        }

        internal static string GenerateHelpTopicsSection()
        {
            var builder = new StringBuilder();
            builder.AppendLine("## Help Topics");
            builder.AppendLine();
            builder.AppendLine("Use either `AudioPilot.Cli.exe help <topic>` or `AudioPilot.Cli.exe <topic> help` for grouped command help.");
            builder.AppendLine();

            foreach (string topic in CliCommandHelpMetadata.Topics)
            {
                builder.AppendLine($"### {topic}");
                builder.AppendLine();
                builder.AppendLine("```powershell");
                builder.AppendLine($"AudioPilot.Cli.exe help {topic}");
                builder.AppendLine($"AudioPilot.Cli.exe {topic} help");
                foreach (string usageLine in GetTopicUsageLines(topic))
                {
                    builder.AppendLine(ToDocumentedCommand(usageLine));
                }
                builder.AppendLine("```");

                IReadOnlyList<string> notes = GetTopicNotes(topic);
                foreach (string note in notes)
                {
                    builder.AppendLine();
                    builder.AppendLine(note);
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        internal static string GenerateCommandsSection()
        {
            var builder = new StringBuilder();
            builder.AppendLine("## Commands");
            builder.AppendLine();

            AppendCommandGroup(
                builder,
                "UI And Lifecycle Commands",
                [
                    .. GetTopicUsageLines("completion"),
                    "audio-pilot show",
                    "audio-pilot hide",
                    .. GetTopicUsageLines("startup"),
                    $"audio-pilot help [{CliCommandHelpMetadata.HelpTopicListForUsage}]",
                    "audio-pilot version",
                ]);

            builder.AppendLine();
            builder.AppendLine("Grouped help is also available as `<topic> help`, for example `AudioPilot.Cli.exe diagnostics help` or `AudioPilot.Cli.exe volume help`.");
            builder.AppendLine("When a help topic or grouped subcommand is close to a known value, text-mode parse errors also include a `Did you mean ...?` suggestion.");
            builder.AppendLine();

            AppendCommandGroup(
                builder,
                "Switching, Refresh, And Direct Control",
                [
                    .. GetTopicUsageLines("switch"),
                    .. GetTopicUsageLines("wait"),
                    "audio-pilot refresh [--json]",
                    .. GetTopicUsageLines("diagnostics"),
                    .. GetTopicUsageLines("media"),
                    .. GetTopicUsageLines("mute"),
                    .. GetTopicUsageLines("listen"),
                    .. GetTopicUsageLines("volume"),
                ]);

            builder.AppendLine();
            AppendCommandGroup(builder, "Routine Commands", GetTopicUsageLines("routine"));
            builder.AppendLine();
            AppendCommandGroup(
                builder,
                "Status, Config, And Runtime Commands",
                [
                    "audio-pilot status [--json] [--redact]",
                    .. GetTopicUsageLines("config"),
                    .. GetTopicUsageLines("runtime"),
                    .. GetTopicUsageLines("network"),
                ]);
            builder.AppendLine();
            AppendCommandGroup(
                builder,
                "Device And Cycle Commands",
                [
                    .. GetTopicUsageLines("devices"),
                    .. GetTopicUsageLines("cycle"),
                ]);

            return builder.ToString().TrimEnd();
        }

        private static void AppendCommandGroup(StringBuilder builder, string heading, IReadOnlyList<string> usageLines)
        {
            builder.AppendLine($"### {heading}");
            builder.AppendLine();
            builder.AppendLine("```powershell");
            foreach (string usageLine in usageLines)
            {
                builder.AppendLine(ToDocumentedCommand(usageLine));
            }
            builder.AppendLine("```");
        }

        private static IReadOnlyList<string> GetTopicUsageLines(string topic)
        {
            return CliCommandHelpMetadata.TryGetUsageLinesForTopic(topic, out IReadOnlyList<string>? usageLines)
                ? usageLines!
                : throw new InvalidOperationException($"Missing CLI usage metadata for topic '{topic}'.");
        }

        private static IReadOnlyList<string> GetTopicNotes(string topic)
        {
            return CliCommandHelpMetadata.TryGetNotesForTopic(topic, out IReadOnlyList<string>? notes)
                ? notes!
                : throw new InvalidOperationException($"Missing CLI notes metadata for topic '{topic}'.");
        }

        private static string ToDocumentedCommand(string cliCommand)
        {
            return cliCommand.Replace("audio-pilot", "AudioPilot.Cli.exe", StringComparison.Ordinal);
        }
    }
}

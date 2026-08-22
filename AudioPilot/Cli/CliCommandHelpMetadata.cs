namespace AudioPilot.Cli
{
    internal static class CliCommandHelpMetadata
    {
        private sealed record CommandFamilyHelpMetadata(string Topic, string[] UsageLines, string[] Notes);

        internal enum ParserUsageId
        {
            Completion,
            Diagnostics,
            DiagnosticsHistory,
            DiagnosticsHistoryDetail,
            DiagnosticsExport,
            DiagnosticsBundleExport,
            DiagnosticsResetPerAppAudio,
            Media,
            Mute,
            Listen,
            Volume,
            VolumeGet,
            VolumeSet,
            Config,
            ConfigGet,
            ConfigSet,
            ConfigExport,
            ConfigImport,
            Routine,
            RoutineCreate,
            RoutineUpdate,
            RoutineImport,
            RoutineExport,
            Runtime,
            RuntimeGet,
            RuntimeSet,
            Network,
            Devices,
            DevicesList,
            DevicesGet,
            DevicesFind,
            Cycle,
            CycleReorder,
            CycleTest,
            CycleShowValidate,
            Switch,
            Wait,
            Startup,
            StartupAll,
        }

        internal enum ParserUsageTemplateId
        {
            RoutineSelector,
            CycleMutation,
        }

        private static readonly Dictionary<ParserUsageId, string> ParserUsages = new()
        {
            [ParserUsageId.Completion] = "completion powershell|bash",
            [ParserUsageId.Diagnostics] = "diagnostics refresh [--json] | diagnostics status [--json] [--redact] [--show-paths] | diagnostics history [--limit <n>] [--type routine|switch|media|mute] [--json] [--redact] | diagnostics history-detail <opId> [--json] [--redact] | diagnostics export-logs <path.zip> [--json] [--redact] [--detail summary|manifest] [--allow-any-path] | diagnostics export-bundle <path.zip> [--json] [--detail summary|manifest] [--allow-any-path] [--include-sensitive] | diagnostics reset-per-app-audio [--json]",
            [ParserUsageId.DiagnosticsHistory] = "diagnostics history [--limit <n>] [--type routine|switch|media|mute] [--json] [--redact]",
            [ParserUsageId.DiagnosticsHistoryDetail] = "diagnostics history-detail <opId> [--json] [--redact]",
            [ParserUsageId.DiagnosticsExport] = "diagnostics export-logs <path.zip> [--json] [--redact] [--detail summary|manifest] [--allow-any-path]",
            [ParserUsageId.DiagnosticsBundleExport] = "diagnostics export-bundle <path.zip> [--json] [--detail summary|manifest] [--allow-any-path] [--include-sensitive]",
            [ParserUsageId.DiagnosticsResetPerAppAudio] = "diagnostics reset-per-app-audio [--json]",
            [ParserUsageId.Media] = "media play-pause|next|previous|status [--json] [--redact]",
            [ParserUsageId.Mute] = "mute mic|sound|deafen [toggle|on|off] [--json]",
            [ParserUsageId.Listen] = "listen toggle|on|off [--json] [--redact]",
            [ParserUsageId.Volume] = "volume get master|mic [--device <name>|--device-id <id>] [--json] | volume set master|mic <percent> [--device <name>|--device-id <id>] [--json]",
            [ParserUsageId.VolumeGet] = "volume get master|mic [--device <name>|--device-id <id>] [--json]",
            [ParserUsageId.VolumeSet] = "volume set master|mic <percent> [--device <name>|--device-id <id>] [--json]",
            [ParserUsageId.Config] = "config list [--json] | config get <key> [--json] [--redact] | config set <key> <value> | config export <path.json|path.zip> [--json] [--redact] [--allow-any-path] | config import <path.json|path.zip> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path] | config validate [--json] [--redact]",
            [ParserUsageId.ConfigGet] = "config get <key> [--json]",
            [ParserUsageId.ConfigSet] = "config set <key> <value>",
            [ParserUsageId.ConfigExport] = "config export <path.json|path.zip> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.ConfigImport] = "config import <path.json|path.zip> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.Routine] = "routine list [--json] [--redact] | routine run|enable|disable|delete <id|name> [--json] [--redact] | routine create <path.json> [--json] [--redact] [--allow-any-path] | routine update <id|name> <path.json> [--json] [--redact] [--allow-any-path] | routine import <path.json> [--merge|--replace] [--json] [--redact] [--allow-any-path] | routine export <path.json> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.RoutineCreate] = "routine create <path.json> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.RoutineUpdate] = "routine update <id|name> <path.json> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.RoutineImport] = "routine import <path.json> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.RoutineExport] = "routine export <path.json> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.Runtime] = "runtime list [--json] | runtime get <key> [--json] | runtime set <key> <value>",
            [ParserUsageId.RuntimeGet] = "runtime get <key> [--json] [--redact]",
            [ParserUsageId.RuntimeSet] = "runtime set <key> <value>",
            [ParserUsageId.Network] = "network list [--json] [--redact]",
            [ParserUsageId.Devices] = "devices list|get|find output|input ...",
            [ParserUsageId.DevicesList] = "devices list output|input [--json] [--redact]",
            [ParserUsageId.DevicesGet] = "devices get output|input <id|name> [--json] [--redact]",
            [ParserUsageId.DevicesFind] = "devices find output|input <text> [--json] [--redact]",
            [ParserUsageId.Cycle] = "cycle show|validate|test output|input [--json] [--redact] | cycle add|remove output|input <deviceId> [--json] [--redact] | cycle reorder output|input <deviceId...> [--json] [--redact]",
            [ParserUsageId.CycleReorder] = "cycle reorder output|input <deviceId...> [--json] [--redact]",
            [ParserUsageId.CycleTest] = "cycle test output|input [--json] [--redact]",
            [ParserUsageId.CycleShowValidate] = "cycle show|validate output|input [--json] [--redact]",
            [ParserUsageId.Switch] = "switch output|input [flags] [--json] [--redact]",
            [ParserUsageId.Wait] = "wait --wait-for-device <deviceId> [--timeout <ms>] [--output|--input] [--json] [--redact]",
            [ParserUsageId.Startup] = "startup enable|disable|status [--json] [--redact] | startup open",
            [ParserUsageId.StartupAll] = "startup enable|disable|status [--json] [--redact] | startup open",
        };

        private static readonly string[] DiagnosticsUsageLines =
        [
            "audio-pilot diagnostics refresh [--json]",
            "audio-pilot diagnostics status [--json] [--redact] [--show-paths]",
            "audio-pilot diagnostics history [--limit <n>] [--type routine|switch|media|mute] [--json] [--redact]",
            "audio-pilot diagnostics history-detail <opId> [--json] [--redact]",
            "audio-pilot diagnostics export-logs <path.zip> [--json] [--redact] [--detail summary|manifest] [--allow-any-path]",
            "audio-pilot diagnostics export-bundle <path.zip> [--json] [--detail summary|manifest] [--allow-any-path] [--include-sensitive]",
            "audio-pilot diagnostics reset-per-app-audio [--json]",
          ];

        private static readonly string[] CompletionUsageLines = ["audio-pilot completion powershell|bash"];

        private static readonly string[] CoreUsageLines =
        [
            "audio-pilot show",
            "audio-pilot hide",
            "audio-pilot refresh [--json]",
            "audio-pilot status [--json] [--redact]",
            "audio-pilot version",
        ];

        private static readonly string[] MediaUsageLines =
        [
            "audio-pilot media play-pause|next|previous",
            "audio-pilot media status [--json] [--redact]",
        ];

        private static readonly string[] MuteUsageLines = ["audio-pilot mute mic|sound|deafen [toggle|on|off] [--json]"];

        private static readonly string[] ListenUsageLines = ["audio-pilot listen toggle|on|off [--json] [--redact]"];

        private static readonly string[] VolumeUsageLines =
        [
            "audio-pilot volume get master|mic [--device <name>|--device-id <deviceId>] [--json]",
            "audio-pilot volume set master|mic <percent> [--device <name>|--device-id <deviceId>] [--json]",
        ];

        private static readonly string[] RoutineUsageLines =
        [
            "audio-pilot routine list [--json] [--redact]",
            "audio-pilot routine run|enable|disable|delete <id|name> [--json] [--redact]",
            "audio-pilot routine create <path.json> [--json] [--redact] [--allow-any-path]",
            "audio-pilot routine update <id|name> <path.json> [--json] [--redact] [--allow-any-path]",
            "audio-pilot routine import <path.json> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]",
            "audio-pilot routine export <path.json> [--json] [--redact] [--allow-any-path]",
        ];

        private static readonly string[] ConfigUsageLines =
        [
            "audio-pilot config list [--json]",
            "audio-pilot config get <key> [--json] [--redact]",
            "audio-pilot config set <key> <value>",
            "audio-pilot config export <path.json|path.zip> [--json] [--redact] [--allow-any-path]",
            "audio-pilot config import <path.json|path.zip> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]",
            "audio-pilot config validate [--json] [--redact]",
        ];

        private static readonly string[] RuntimeUsageLines =
        [
            "audio-pilot runtime list [--json]",
            "audio-pilot runtime get <key> [--json] [--redact]",
            "audio-pilot runtime set <key> <value>",
        ];

        private static readonly string[] NetworkUsageLines = ["audio-pilot network list [--json] [--redact]"];

        private static readonly string[] DevicesUsageLines =
        [
            "audio-pilot devices list output|input [--json] [--redact]",
            "audio-pilot devices get output|input <id|name> [--json] [--redact]",
            "audio-pilot devices find output|input <text> [--json] [--redact]",
        ];

        private static readonly string[] CycleUsageLines =
        [
            "audio-pilot cycle show|validate|test output|input [--json] [--redact]",
            "audio-pilot cycle add|remove output|input <deviceId> [--json] [--redact]",
            "audio-pilot cycle reorder output|input <deviceId...> [--json] [--redact]",
        ];

        private static readonly string[] SwitchUsageLines =
        [
            "audio-pilot switch output [--reverse] [--mute-mic] [--mute-sound] [--deafen] [--dry-run] [--require-current <deviceId>] [--json] [--redact]",
            "audio-pilot switch input [--reverse] [--dry-run] [--require-current <deviceId>] [--json] [--redact]",
        ];

        private static readonly string[] WaitUsageLines = ["audio-pilot wait --wait-for-device <deviceId> [--timeout <ms>] [--output|--input] [--json] [--redact]"];

        private static readonly string[] StartupUsageLines =
        [
            "audio-pilot startup enable|disable|status [--json] [--redact]",
            "audio-pilot startup open",
        ];

        private static readonly CommandFamilyHelpMetadata[] CommandFamilies =
        [
            new("core", CoreUsageLines, ["Use <command> --help or audio-pilot help core for these one-shot commands."]),
            new("completion", CompletionUsageLines, ["Use completion powershell or completion bash to print a shell script generated from the centralized CLI metadata."]),
            new("diagnostics", DiagnosticsUsageLines, ["Use diagnostics history to inspect recent routine, switch, media, and mute outcomes for the current app session.", "Use diagnostics export-bundle to create a redacted support zip; pass --include-sensitive only for local private troubleshooting.", "Use --detail manifest when you want per-entry archive results in addition to the summary."]),
            new("media", MediaUsageLines, ["Use media status when you need the current media snapshot in text or JSON form. The transport commands remain fire-and-forget."]),
            new("mute", MuteUsageLines, ["If no mode is provided, mute commands default to toggle. Pass --json to return the resulting mute state."]),
            new("listen", ListenUsageLines, ["If no mode is provided, listen defaults to toggle."]),
            new("volume", VolumeUsageLines, ["Use either --device or --device-id to target a non-default endpoint."]),
            new("routine", RoutineUsageLines, ["routine import merges by default. Pass --replace to replace the full saved routine list."]),
            new("config", ConfigUsageLines, ["config import merges by default. Pass --replace to replace the imported settings snapshot."]),
            new("runtime", RuntimeUsageLines, ["Runtime changes affect only the current AudioPilot process and are reset when that process exits. Use config set for the subset of tuning keys that can be persisted."]),
            new("network", NetworkUsageLines, ["Lists currently visible network names for network-trigger routine setup. Use --redact before sharing output because network names can identify locations."]),
            new("devices", DevicesUsageLines, ["devices find accepts multi-word text and performs a case-insensitive substring search across ids and names."]),
            new("cycle", CycleUsageLines, ["cycle reorder expects the full current cycle device list in the new order; the parser rejects blank or duplicate ids, and execution verifies the configured cycle membership." ]),
            new("switch", SwitchUsageLines, ["Input switching supports --reverse, --dry-run, and --require-current, but not --mute-mic, --mute-sound, or --deafen."]),
            new("wait", WaitUsageLines, ["Use --output or --input to scope the wait to one device class; the parser rejects passing both flags together, and omitting both lets either class satisfy the wait."]),
            new("startup", StartupUsageLines, ["startup open is UI-only and requires a running UI host instance."]),
        ];

        private static readonly string[] TopicNames = [.. CommandFamilies.Select(static family => family.Topic)];

        private static readonly Dictionary<string, CommandFamilyHelpMetadata> CommandFamiliesByTopic =
            CommandFamilies.ToDictionary(static family => family.Topic, StringComparer.Ordinal);

        private static readonly string[] TopLevelUsageLines =
        [
            .. CompletionUsageLines,
            .. CoreUsageLines.Take(2),
            .. SwitchUsageLines,
            .. WaitUsageLines,
            CoreUsageLines[2],
            .. DiagnosticsUsageLines,
            .. MediaUsageLines,
            .. MuteUsageLines,
            .. ListenUsageLines,
            .. VolumeUsageLines,
            .. RoutineUsageLines,
            CoreUsageLines[3],
            .. ConfigUsageLines,
            .. RuntimeUsageLines,
            .. NetworkUsageLines,
            .. DevicesUsageLines,
            .. CycleUsageLines,
            .. StartupUsageLines,
            CoreUsageLines[4],
        ];

        internal static string HelpTopicListForUsage => string.Join('|', TopicNames);

        internal static IReadOnlyList<string> Topics => TopicNames;

        internal static IReadOnlyList<string> UsageLines => TopLevelUsageLines;

        internal static bool TryGetUsageLinesForTopic(string topic, out IReadOnlyList<string>? usageLines)
        {
            if (!CommandFamiliesByTopic.TryGetValue(topic, out CommandFamilyHelpMetadata? family))
            {
                usageLines = null;
                return false;
            }

            usageLines = family.UsageLines;
            return true;
        }

        internal static bool TryGetNotesForTopic(string topic, out IReadOnlyList<string>? notes)
        {
            if (!CommandFamiliesByTopic.TryGetValue(topic, out CommandFamilyHelpMetadata? family))
            {
                notes = null;
                return false;
            }

            notes = family.Notes;
            return true;
        }

        internal static string GetParserUsage(ParserUsageId usageId)
        {
            return ParserUsages[usageId];
        }

        internal static string FormatParserUsage(ParserUsageTemplateId templateId, string value)
        {
            return templateId switch
            {
                ParserUsageTemplateId.RoutineSelector => $"routine {value} <id|name> [--json] [--redact]",
                ParserUsageTemplateId.CycleMutation => $"cycle {value} output|input <deviceId> [--json] [--redact]",
                _ => throw new ArgumentOutOfRangeException(nameof(templateId)),
            };
        }

        internal static bool TryNormalizeTopic(string? topic, out string? normalizedTopic)
        {
            normalizedTopic = null;
            if (string.IsNullOrWhiteSpace(topic))
            {
                return false;
            }

            string candidate = topic.Trim().ToLowerInvariant();
            if (!CommandFamiliesByTopic.ContainsKey(candidate))
            {
                return false;
            }

            normalizedTopic = candidate;
            return true;
        }

        internal static bool TryGetHelpText(string topic, out string? helpText)
        {
            if (!CommandFamiliesByTopic.TryGetValue(topic, out CommandFamilyHelpMetadata? family))
            {
                helpText = null;
                return false;
            }

            helpText = BuildHelpText(family);
            return true;
        }

        private static string BuildHelpText(CommandFamilyHelpMetadata family)
        {
            string text =
                $"AudioPilot CLI - {family.Topic}\n" +
                "Usage:\n" +
                string.Join("\n", family.UsageLines.Select(line => $"  {line}"));

            if (family.Notes.Length == 0)
            {
                return text;
            }

            return text +
                "\n\nNotes:\n" +
                string.Join("\n", family.Notes.Select(note => $"  {note}"));
        }
    }
}

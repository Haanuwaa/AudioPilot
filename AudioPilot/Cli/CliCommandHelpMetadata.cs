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
            VolumeAdjust,
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
            [ParserUsageId.Media] = "media play-pause|next|previous; media play|pause [--json]; media status [--json] [--redact]; media seek forward|backward [duration] [--json]",
            [ParserUsageId.Mute] = "mute mic|sound|deafen [toggle|on|off] [--json]",
            [ParserUsageId.Listen] = "listen toggle|on|off [--json] [--redact]",
            [ParserUsageId.Volume] = "volume get|set|adjust master|mic [percent|delta] [--device <name>|--device-id <id>] [--json] [--redact]",
            [ParserUsageId.VolumeGet] = "volume get master|mic [--device <name>|--device-id <id>] [--json] [--redact]",
            [ParserUsageId.VolumeSet] = "volume set master|mic <percent> [--device <name>|--device-id <id>] [--json] [--redact]",
            [ParserUsageId.VolumeAdjust] = "volume adjust master|mic <delta> [--device <name>|--device-id <id>] [--json] [--redact]",
            [ParserUsageId.Config] = "config list [--json] | config get <key> [--json] [--redact] | config set <key> <value> [--json] [--redact] | config export <path.json|path.zip> [--json] [--redact] [--allow-any-path] | config import <path.json|path.zip> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path] | config validate [--json] [--redact]",
            [ParserUsageId.ConfigGet] = "config get <key> [--json]",
            [ParserUsageId.ConfigSet] = "config set <key> <value> [--json] [--redact]",
            [ParserUsageId.ConfigExport] = "config export <path.json|path.zip> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.ConfigImport] = "config import <path.json|path.zip> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.Routine] = "routine list [--json] [--redact] | routine get|next|run|enable|disable|delete <id|name> [--json] [--redact] | routine create <path.json> [--json] [--redact] [--allow-any-path] | routine update <id|name> <path.json> [--json] [--redact] [--allow-any-path] | routine import <path.json> [--merge|--replace] [--json] [--redact] [--allow-any-path] | routine export <path.json> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.RoutineCreate] = "routine create <path.json> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.RoutineUpdate] = "routine update <id|name> <path.json> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.RoutineImport] = "routine import <path.json> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.RoutineExport] = "routine export <path.json> [--json] [--redact] [--allow-any-path]",
            [ParserUsageId.Runtime] = "runtime list [--json] | runtime get <key> [--json] | runtime set <key> <value> [--json] [--redact]",
            [ParserUsageId.RuntimeGet] = "runtime get <key> [--json] [--redact]",
            [ParserUsageId.RuntimeSet] = "runtime set <key> <value> [--json] [--redact]",
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
            "AudioPilot.Cli.exe diagnostics refresh [--json]",
            "AudioPilot.Cli.exe diagnostics status [--json] [--redact] [--show-paths]",
            "AudioPilot.Cli.exe diagnostics history [--limit <n>] [--type routine|switch|media|mute] [--json] [--redact]",
            "AudioPilot.Cli.exe diagnostics history-detail <opId> [--json] [--redact]",
            "AudioPilot.Cli.exe diagnostics export-logs <path.zip> [--json] [--redact] [--detail summary|manifest] [--allow-any-path]",
            "AudioPilot.Cli.exe diagnostics export-bundle <path.zip> [--json] [--detail summary|manifest] [--allow-any-path] [--include-sensitive]",
            "AudioPilot.Cli.exe diagnostics reset-per-app-audio [--json]",
          ];

        private static readonly string[] CompletionUsageLines = ["AudioPilot.Cli.exe completion powershell|bash"];

        private static readonly string[] CoreUsageLines =
        [
            "AudioPilot.Cli.exe show",
            "AudioPilot.Cli.exe hide",
            "AudioPilot.Cli.exe refresh [--json]",
            "AudioPilot.Cli.exe status [--json] [--redact]",
            "AudioPilot.Cli.exe version",
        ];

        private static readonly string[] MediaUsageLines =
        [
            "AudioPilot.Cli.exe media play-pause|next|previous",
            "AudioPilot.Cli.exe media play|pause [--json]",
            "AudioPilot.Cli.exe media status [--json] [--redact]",
            "AudioPilot.Cli.exe media seek forward|backward [duration] [--json]",
        ];

        private static readonly string[] MuteUsageLines = ["AudioPilot.Cli.exe mute mic|sound|deafen [toggle|on|off] [--json]"];

        private static readonly string[] ListenUsageLines = ["AudioPilot.Cli.exe listen toggle|on|off [--json] [--redact]"];

        private static readonly string[] VolumeUsageLines =
        [
            "AudioPilot.Cli.exe volume get master|mic [--device <name>|--device-id <deviceId>] [--json] [--redact]",
            "AudioPilot.Cli.exe volume set master|mic <percent> [--device <name>|--device-id <deviceId>] [--json] [--redact]",
            "AudioPilot.Cli.exe volume adjust master|mic <delta> [--device <name>|--device-id <deviceId>] [--json] [--redact]",
        ];

        private static readonly string[] RoutineUsageLines =
        [
            "AudioPilot.Cli.exe routine list [--json] [--redact]",
            "AudioPilot.Cli.exe routine get <id|name> [--json] [--redact]",
            "AudioPilot.Cli.exe routine next <id|name> [--json] [--redact]",
            "AudioPilot.Cli.exe routine run|enable|disable|delete <id|name> [--json] [--redact]",
            "AudioPilot.Cli.exe routine create <path.json> [--json] [--redact] [--allow-any-path]",
            "AudioPilot.Cli.exe routine update <id|name> <path.json> [--json] [--redact] [--allow-any-path]",
            "AudioPilot.Cli.exe routine import <path.json> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]",
            "AudioPilot.Cli.exe routine export <path.json> [--json] [--redact] [--allow-any-path]",
        ];

        private static readonly string[] ConfigUsageLines =
        [
            "AudioPilot.Cli.exe config list [--json]",
            "AudioPilot.Cli.exe config get <key> [--json] [--redact]",
            "AudioPilot.Cli.exe config set <key> <value> [--json] [--redact]",
            "AudioPilot.Cli.exe config export <path.json|path.zip> [--json] [--redact] [--allow-any-path]",
            "AudioPilot.Cli.exe config import <path.json|path.zip> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]",
            "AudioPilot.Cli.exe config validate [--json] [--redact]",
        ];

        private static readonly string[] RuntimeUsageLines =
        [
            "AudioPilot.Cli.exe runtime list [--json]",
            "AudioPilot.Cli.exe runtime get <key> [--json] [--redact]",
            "AudioPilot.Cli.exe runtime set <key> <value> [--json] [--redact]",
        ];

        private static readonly string[] NetworkUsageLines = ["AudioPilot.Cli.exe network list [--json] [--redact]"];

        private static readonly string[] DevicesUsageLines =
        [
            "AudioPilot.Cli.exe devices list output|input [--json] [--redact]",
            "AudioPilot.Cli.exe devices get output|input <id|name> [--json] [--redact]",
            "AudioPilot.Cli.exe devices find output|input <text> [--json] [--redact]",
        ];

        private static readonly string[] CycleUsageLines =
        [
            "AudioPilot.Cli.exe cycle show|validate|test output|input [--json] [--redact]",
            "AudioPilot.Cli.exe cycle add|remove output|input <deviceId> [--json] [--redact]",
            "AudioPilot.Cli.exe cycle reorder output|input <deviceId...> [--json] [--redact]",
        ];

        private static readonly string[] SwitchUsageLines =
        [
            "AudioPilot.Cli.exe switch output [--reverse|--device <name>|--device-id <deviceId>] [--mute-mic] [--mute-sound] [--deafen] [--dry-run] [--require-current <deviceId>] [--json] [--redact]",
            "AudioPilot.Cli.exe switch input [--reverse|--device <name>|--device-id <deviceId>] [--dry-run] [--require-current <deviceId>] [--json] [--redact]",
        ];

        private static readonly string[] WaitUsageLines = ["AudioPilot.Cli.exe wait --wait-for-device <deviceId> [--timeout <ms>] [--output|--input] [--json] [--redact]"];

        private static readonly string[] StartupUsageLines =
        [
            "AudioPilot.Cli.exe startup enable|disable|status [--json] [--redact]",
            "AudioPilot.Cli.exe startup open",
        ];

        private static readonly CommandFamilyHelpMetadata[] CommandFamilies =
        [
            new("core", CoreUsageLines, ["Use a command's --help option or AudioPilot.Cli.exe help core for these one-shot commands."]),
            new("completion", CompletionUsageLines, ["Use completion powershell or completion bash to print a shell script generated from the centralized CLI metadata."]),
            new("diagnostics", DiagnosticsUsageLines, ["Use diagnostics history to inspect recent routine, switch, media, and mute outcomes for the current app session.", "Use diagnostics export-bundle to create a redacted support zip; pass --include-sensitive only for local private troubleshooting.", "Use --detail manifest when you want per-entry archive results in addition to the summary."]),
            new("media", MediaUsageLines, ["Use media status when you need the current media snapshot in text or JSON form. Play/pause and track navigation remain fire-and-forget. Separate play and pause commands await an explicit request, succeed without sending when already in the requested state, and never fall back to a toggle. They report accepted versus confirmed playback; exit code 0 includes accepted requests whose state is not yet confirmed. They do not show overlays. Seeking reports the result; durations accept seconds or minutes (90, 90s, 1.5m, 1m30s, or 1:30). Omit the duration to use media-seek-step-seconds (default 10 seconds, range 1 second to 60 minutes). Seeking requires the selected player to expose a seekable timeline; live streams and some browser sessions may not support it."]),
            new("mute", MuteUsageLines, ["If no mode is provided, mute commands default to toggle. Pass --json to return the resulting mute state."]),
            new("listen", ListenUsageLines, ["If no mode is provided, listen defaults to toggle."]),
            new("volume", VolumeUsageLines, ["Use either --device or --device-id to target a non-default endpoint.", "adjust accepts -100 to +100 percentage points, including decimals, and clamps the result to 0-100%. Like set, zero mutes and a positive result unmutes; an adjustment of 0 leaves volume and mute unchanged. JSON includes previousPercent and the resulting percent."]),
            new("routine", RoutineUsageLines, ["routine get inspects one routine without running it. Use an id when names are ambiguous.", "routine next previews the next scheduled occurrence; it does not run the routine or keep the app awake.", "routine import merges by default. Pass --replace to replace the full saved routine list."]),
            new("config", ConfigUsageLines, ["config import merges by default. Pass --replace to replace the imported settings snapshot.", "config set accepts --json and --redact after the value. Quote values containing spaces. For get/set, --redact masks the returned value; it does not change the stored value."]),
            new("runtime", RuntimeUsageLines, ["runtime set requires a running AudioPilot instance and accepts --json and --redact after the value. Changes reset when that app exits. Use config set for supported persistent settings. Without a running app, runtime get/list report this CLI process's defaults."]),
            new("network", NetworkUsageLines, ["Lists currently visible network names for network-trigger routine setup. Use --redact before sharing output because network names can identify locations."]),
            new("devices", DevicesUsageLines, ["devices find accepts multi-word text and performs a case-insensitive substring search across ids and names."]),
            new("cycle", CycleUsageLines, ["cycle reorder expects the full current cycle device list in the new order; the parser rejects blank or duplicate ids, and execution verifies the configured cycle membership." ]),
            new("switch", SwitchUsageLines, ["Without a device selector, switching cycles through the configured order. --device matches an exact name (case-insensitive); --device-id matches an exact ID. Direct targets must be active and need not be in a cycle. Duplicate names require an ID. Direct switching respects configured audio roles and Preserve Audio Levels.", "--reverse cannot be combined with a device selector. --dry-run previews without changing audio; --require-current checks the current device before switching.", "Input switching supports --reverse, --dry-run, and --require-current, but not --mute-mic, --mute-sound, or --deafen."]),
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

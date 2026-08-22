using System.Text.Json;

namespace AudioPilot.Cli
{
    public enum CliAction
    {
        None = 0,
        Completion,
        Show,
        Hide,
        SwitchOutput,
        SwitchInput,
        Refresh,
        StartupEnable,
        StartupDisable,
        StartupStatus,
        StartupOpen,
        Help,
        Version,
        Status,
        DevicesListOutput,
        DevicesListInput,
        DevicesGetOutput,
        DevicesGetInput,
        DevicesFindOutput,
        DevicesFindInput,
        CycleShowOutput,
        CycleShowInput,
        CycleValidateOutput,
        CycleValidateInput,
        CycleTestOutput,
        CycleTestInput,
        CycleAddOutput,
        CycleAddInput,
        CycleRemoveOutput,
        CycleRemoveInput,
        CycleReorderOutput,
        CycleReorderInput,
        MediaPlayPause,
        MediaNextTrack,
        MediaPreviousTrack,
        MediaStatus,
        MuteMicToggle,
        MuteMicOn,
        MuteMicOff,
        MuteSoundToggle,
        MuteSoundOn,
        MuteSoundOff,
        DeafenToggle,
        DeafenOn,
        DeafenOff,
        NetworkList,
        ConfigGet,
        ConfigSet,
        RuntimeGet,
        RuntimeSet,
        ConfigValidate,
        ConfigExport,
        ConfigImport,
        WaitForDevice,
        ListenToggle,
        ListenOn,
        ListenOff,
        VolumeGetMaster,
        VolumeGetMic,
        VolumeSetMaster,
        VolumeSetMic,
        DiagnosticsStatus,
        DiagnosticsHistory,
        DiagnosticsHistoryDetail,
        DiagnosticsExportLogs,
        DiagnosticsExportBundle,
        DiagnosticsResetPerAppAudio,
        RoutineList,
        RoutineRun,
        RoutineEnable,
        RoutineDisable,
        RoutineCreate,
        RoutineUpdate,
        RoutineDelete,
        RoutineImport,
        RoutineExport,
        ConfigList,
        RuntimeList,
    }

    public enum CliDiagnosticsExportDetailLevel
    {
        Summary = 0,
        Manifest,
    }

    public sealed partial class CliCommand
    {
        private const int PipeProtocolVersion = 1;
        private const string PipeEnvelopeKind = "cli-command";
        private static readonly JsonSerializerOptions PipeSerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private sealed class PipePayloadEnvelope
        {
            public string Kind { get; init; } = PipeEnvelopeKind;
            public int ProtocolVersion { get; init; } = PipeProtocolVersion;
            public string Action { get; init; } = CliAction.None.ToString();
            public bool Reverse { get; init; }
            public bool MuteMic { get; init; }
            public bool MuteSound { get; init; }
            public bool Deafen { get; init; }
            public bool JsonOutput { get; init; }
            public bool DryRun { get; init; }
            public bool WaitOutputOnly { get; init; }
            public bool WaitInputOnly { get; init; }
            public bool ReplaceImport { get; init; }
            public bool ShowPaths { get; init; }
            public bool AllowAnyPath { get; init; }
            public bool RedactOutput { get; init; }
            public bool IncludeSensitive { get; init; }
            public string? Key { get; init; }
            public string? Value { get; init; }
            public int? Limit { get; init; }
            public string DiagnosticsExportDetailLevel { get; init; } = CliDiagnosticsExportDetailLevel.Summary.ToString().ToLowerInvariant();
        }

        public CliAction Action { get; init; } = CliAction.None;
        public bool Reverse { get; init; }
        public bool MuteMic { get; init; }
        public bool MuteSound { get; init; }
        public bool Deafen { get; init; }
        public bool JsonOutput { get; init; }
        public bool DryRun { get; init; }
        public bool WaitOutputOnly { get; init; }
        public bool WaitInputOnly { get; init; }
        public bool ReplaceImport { get; init; }
        public bool ShowPaths { get; init; }
        public bool AllowAnyPath { get; init; }
        public bool RedactOutput { get; init; }
        public bool IncludeSensitive { get; init; }
        public CliDiagnosticsExportDetailLevel DiagnosticsExportDetailLevel { get; init; } = CliDiagnosticsExportDetailLevel.Summary;
        public string? Key { get; init; }
        public string? Value { get; init; }

        public int? Limit { get; init; }

        public bool IsNoOpLaunch => Action == CliAction.None;

        public string ToPipePayload()
        {
            return JsonSerializer.Serialize(
                new PipePayloadEnvelope
                {
                    Action = Action.ToString(),
                    Reverse = Reverse,
                    MuteMic = MuteMic,
                    MuteSound = MuteSound,
                    Deafen = Deafen,
                    JsonOutput = JsonOutput,
                    DryRun = DryRun,
                    WaitOutputOnly = WaitOutputOnly,
                    WaitInputOnly = WaitInputOnly,
                    ReplaceImport = ReplaceImport,
                    ShowPaths = ShowPaths,
                    AllowAnyPath = AllowAnyPath,
                    RedactOutput = RedactOutput,
                    IncludeSensitive = IncludeSensitive,
                    Key = Key,
                    Value = Value,
                    Limit = Limit,
                    DiagnosticsExportDetailLevel = DiagnosticsExportDetailLevel.ToString().ToLowerInvariant(),
                },
                PipeSerializerOptions);
        }

        public static bool TryFromPipePayload(string payload, out CliCommand command)
        {
            return TryFromPipePayload(payload, out command, out _, out _);
        }

        internal static bool TryFromPipePayload(string payload, out CliCommand command, out string? failureReason, out int? protocolVersion)
        {
            command = new();
            protocolVersion = null;

            if (string.IsNullOrWhiteSpace(payload))
            {
                failureReason = "empty-payload";
                return false;
            }

            string trimmedPayload = payload.TrimStart();
            if (!trimmedPayload.StartsWith('{'))
            {
                failureReason = "invalid-envelope-format";
                return false;
            }

            return TryFromPipeEnvelopePayload(trimmedPayload, out command, out failureReason, out protocolVersion);
        }

        private static bool TryFromPipeEnvelopePayload(string payload, out CliCommand command, out string? failureReason, out int? protocolVersion)
        {
            command = new();
            failureReason = null;
            protocolVersion = null;

            PipePayloadEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<PipePayloadEnvelope>(payload, PipeSerializerOptions);
            }
            catch (JsonException)
            {
                failureReason = "invalid-json-envelope";
                return false;
            }

            if (envelope == null)
            {
                failureReason = "missing-envelope";
                return false;
            }

            protocolVersion = envelope.ProtocolVersion;
            if (!string.Equals(envelope.Kind, PipeEnvelopeKind, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "invalid-envelope-kind";
                return false;
            }

            if (envelope.ProtocolVersion != PipeProtocolVersion)
            {
                failureReason = "unsupported-protocol-version";
                return false;
            }

            if (!Enum.TryParse(envelope.Action, ignoreCase: true, out CliAction action)
                || !Enum.IsDefined(action))
            {
                failureReason = "invalid-action";
                return false;
            }

            if (!TryParseDiagnosticsExportDetailLevel(envelope.DiagnosticsExportDetailLevel, out CliDiagnosticsExportDetailLevel diagnosticsExportDetailLevel))
            {
                failureReason = "invalid-diagnostics-export-detail-level";
                return false;
            }

            command = new CliCommand
            {
                Action = action,
                Reverse = envelope.Reverse,
                MuteMic = envelope.MuteMic,
                MuteSound = envelope.MuteSound,
                Deafen = envelope.Deafen,
                JsonOutput = envelope.JsonOutput,
                DryRun = envelope.DryRun,
                WaitOutputOnly = envelope.WaitOutputOnly,
                WaitInputOnly = envelope.WaitInputOnly,
                ReplaceImport = envelope.ReplaceImport,
                ShowPaths = envelope.ShowPaths,
                AllowAnyPath = envelope.AllowAnyPath,
                RedactOutput = envelope.RedactOutput,
                IncludeSensitive = envelope.IncludeSensitive,
                Key = envelope.Key,
                Value = envelope.Value,
                Limit = envelope.Limit,
                DiagnosticsExportDetailLevel = diagnosticsExportDetailLevel,
            };

            return true;
        }

        public static bool TryParse(string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (args.Length == 0)
            {
                return true;
            }

            string[] tokens = new string[args.Length];
            for (int index = 0; index < args.Length; index++)
            {
                tokens[index] = NormalizeToken(args[index]);
            }
            string verb = tokens[0];

            if (tokens.Length == 2 && IsHelpToken(tokens[1]) && IsCoreCommand(verb))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "core" };
                return true;
            }

            if (tokens.Length == 2
                && IsHelpToken(tokens[1])
                && TryNormalizeHelpTopic(verb, out string? normalizedHelpTopic))
            {
                command = new CliCommand { Action = CliAction.Help, Key = normalizedHelpTopic };
                return true;
            }

            if (verb == "-startup")
            {
                command = new();
                return true;
            }

            switch (verb)
            {
                case "completion":
                    return TryParseCompletionCommand(tokens, args, out command, out error);
                case "help" or "--help" or "-h":
                    return TryParseHelpCommand(tokens, args, out command, out error);
                case "version" or "--version" or "-v":
                    return TryParseArgumentlessCoreCommand(tokens, CliAction.Version, "version", out command, out error);
                case "show":
                    return TryParseArgumentlessCoreCommand(tokens, CliAction.Show, "show", out command, out error);
                case "hide":
                    return TryParseArgumentlessCoreCommand(tokens, CliAction.Hide, "hide", out command, out error);
                case "refresh":
                    return TryParseRefreshCommand(tokens, args, out command, out error);
                case "diagnostics":
                    return TryParseDiagnosticsCommand(tokens, args, out command, out error);
                case "media":
                    return TryParseMediaCommand(tokens, args, out command, out error);
                case "mute":
                    return TryParseMuteCommand(tokens, args, out command, out error);
                case "listen":
                    return TryParseListenCommand(tokens, args, out command, out error);
                case "volume":
                    return TryParseVolumeCommand(tokens, args, out command, out error);
                case "routine":
                    return TryParseRoutineCommand(tokens, args, out command, out error);
                case "config":
                    return TryParseConfigCommand(tokens, args, out command, out error);
                case "runtime":
                    return TryParseRuntimeCommand(tokens, args, out command, out error);
                case "wait":
                    return TryParseWaitCommand(tokens, args, out command, out error);
                case "startup":
                    return TryParseStartupCommand(tokens, args, out command, out error);
                case "status":
                    return TryParseStatusCommand(tokens, args, out command, out error);
                case "devices":
                    return TryParseDevicesCommand(tokens, args, out command, out error);
                case "cycle":
                    return TryParseCycleCommand(tokens, args, out command, out error);
                case "switch":
                    return TryParseSwitchCommand(tokens, args, out command, out error);
                case "network":
                    return TryParseNetworkCommand(tokens, args, out command, out error);
            }

            error = CliSuggestionHelper.BuildUnknownValueError(
                "command",
                args[0],
                ["completion", "help", "version", "show", "hide", "refresh", "diagnostics", "media", "mute", "listen", "volume", "routine", "config", "runtime", "wait", "startup", "status", "devices", "cycle", "switch", "network"],
                "completion|show|hide|refresh|diagnostics|media|mute|listen|volume|routine|config|runtime|wait|startup|status|devices|cycle|switch|network|help|version");
            return false;
        }

        private static bool TryParseNetworkCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                error = null;
                command = new CliCommand { Action = CliAction.Help, Key = "network" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing network command. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Network)}";
                return false;
            }

            if (tokens[1] == "list")
            {
                if (!CliOutputFlagParser.TryParse(tokens, args, 2, out bool jsonOutput, out bool redactOutput, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.NetworkList,
                    JsonOutput = jsonOutput,
                    RedactOutput = redactOutput,
                };
                return true;
            }

            error = CliSuggestionHelper.BuildUnknownValueError("network command", args[1], ["list"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Network));
            return false;
        }

        public static string UsageText =>
            "AudioPilot CLI\n" +
            "Usage:\n" +
            string.Join("\n", CliCommandHelpMetadata.UsageLines.Select(line => $"  {line}")) +
            "\n" +
            $"  audio-pilot help [{CliCommandHelpMetadata.HelpTopicListForUsage}]";

        public static string GetHelpText(string? topic)
        {
            if (!TryNormalizeHelpTopic(topic, out string? normalizedTopic))
            {
                return UsageText;
            }

            return CliCommandHelpMetadata.TryGetHelpText(normalizedTopic!, out string? helpText)
                ? helpText!
                : UsageText;
        }

        private static bool TryNormalizeHelpTopic(string? topic, out string? normalizedTopic)
        {
            return CliCommandHelpMetadata.TryNormalizeTopic(topic, out normalizedTopic);
        }

        private static bool IsHelpToken(string token)
        {
            return token is "help" or "--help" or "-h";
        }

        private static bool IsCoreCommand(string verb)
        {
            return verb is "show" or "hide" or "refresh" or "status" or "version" or "--version" or "-v";
        }

        private static bool TryParseArgumentlessCoreCommand(
            string[] tokens,
            CliAction action,
            string usage,
            out CliCommand command,
            out string? error)
        {
            command = new CliCommand();
            if (tokens.Length != 1)
            {
                error = $"Too many arguments for {usage} command. Use: {usage}";
                return false;
            }

            command = new CliCommand { Action = action };
            error = null;
            return true;
        }

        private static string NormalizeToken(string token) => token.Trim().ToLowerInvariant();
    }
}

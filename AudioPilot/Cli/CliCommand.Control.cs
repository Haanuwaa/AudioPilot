using System.Globalization;

namespace AudioPilot.Cli
{
    public sealed partial class CliCommand
    {
        private static bool TryParseCompletionCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "completion" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing completion shell. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Completion)}";
                return false;
            }

            if (tokens.Length > 2)
            {
                error = $"Too many arguments for completion command. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Completion)}";
                return false;
            }

            if (!CliShellCompletionGenerator.TryNormalizeShell(args[1], out string? shell))
            {
                error = CliSuggestionHelper.BuildUnknownValueError(
                    "completion shell",
                    args[1],
                    CliShellCompletionGenerator.DocumentedShellNames,
                    CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Completion));
                return false;
            }

            command = new CliCommand
            {
                Action = CliAction.Completion,
                Key = shell,
            };

            return true;
        }

        private static bool TryParseHelpCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length == 1)
            {
                command = new CliCommand { Action = CliAction.Help };
                return true;
            }

            if (tokens.Length > 2)
            {
                error = $"Too many arguments for help command. Use: help [{CliCommandHelpMetadata.HelpTopicListForUsage}]";
                return false;
            }

            if (!TryNormalizeHelpTopic(tokens[1], out string? topic))
            {
                error = CliSuggestionHelper.BuildUnknownValueError(
                    "help topic",
                    args[1],
                    CliCommandHelpMetadata.Topics,
                    $"help [{CliCommandHelpMetadata.HelpTopicListForUsage}]");
                return false;
            }

            command = new CliCommand
            {
                Action = CliAction.Help,
                Key = topic,
            };

            return true;
        }

        private static bool TryParseRefreshCommand(string[] tokens, string[] args, out CliCommand command, out string? error, int startIndex = 1)
        {
            command = new CliCommand();

            if (!CliOutputFlagParser.TryParse(tokens, args, startIndex, out bool jsonOutput, out bool redactOutput, out error))
            {
                return false;
            }

            if (redactOutput)
            {
                error = "Refresh commands do not support --redact.";
                return false;
            }

            command = new CliCommand
            {
                Action = CliAction.Refresh,
                JsonOutput = jsonOutput,
            };

            return true;
        }

        private static bool TryParseWaitCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "wait" };
                return true;
            }

            string? waitDeviceId = null;
            int timeoutMs = 30000;
            bool waitOutputOnly = false;
            bool waitInputOnly = false;
            bool jsonOutput = false;
            bool redactOutput = false;
            bool showPaths = false;
            bool allowAnyPath = false;
            bool replaceImport = false;
            bool importModeSpecified = false;

            for (int i = 1; i < tokens.Length; i++)
            {
                switch (tokens[i])
                {
                    case "--wait-for-device":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing device ID for --wait-for-device.";
                            return false;
                        }

                        waitDeviceId = args[++i];
                        break;
                    case "--timeout":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out timeoutMs) || timeoutMs < 0)
                        {
                            error = "Invalid timeout value. Use milliseconds (>= 0).";
                            return false;
                        }

                        break;
                    case "--output":
                        waitOutputOnly = true;
                        break;
                    case "--input":
                        waitInputOnly = true;
                        break;
                    default:
                        if (!CliOutputFlagParser.TryParseToken(
                            tokens[i],
                            CliOutputFlagOptions.None,
                            ref jsonOutput,
                            ref redactOutput,
                            ref showPaths,
                            ref allowAnyPath,
                            ref replaceImport,
                            ref importModeSpecified,
                            out bool handled,
                            out error))
                        {
                            return false;
                        }

                        if (handled)
                        {
                            break;
                        }

                        error = $"Unknown wait flag '{args[i]}'.";
                        return false;
                }
            }

            if (string.IsNullOrWhiteSpace(waitDeviceId))
            {
                error = $"Missing --wait-for-device <id> for wait command. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Wait)}";
                return false;
            }

            if (waitOutputOnly && waitInputOnly)
            {
                error = "Wait command supports only one scope: --output or --input.";
                return false;
            }

            command = new CliCommand
            {
                Action = CliAction.WaitForDevice,
                Key = waitDeviceId,
                Value = timeoutMs.ToString(CultureInfo.InvariantCulture),
                WaitOutputOnly = waitOutputOnly,
                WaitInputOnly = waitInputOnly,
                JsonOutput = jsonOutput,
                RedactOutput = redactOutput,
            };
            return true;
        }

        private static bool TryParseStartupCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "startup" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing startup mode. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Startup)}";
                return false;
            }

            if (tokens[1] == "status")
            {
                if (!CliOutputFlagParser.TryParse(tokens, args, 2, out bool jsonOutput, out bool redactOutput, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.StartupStatus,
                    JsonOutput = jsonOutput,
                    RedactOutput = redactOutput,
                };

                return true;
            }

            if (tokens[1] == "open")
            {
                if (tokens.Length > 2)
                {
                    error = "Too many arguments for startup open command.";
                    return false;
                }

                command = new CliCommand { Action = CliAction.StartupOpen };
                return true;
            }

            command = tokens[1] switch
            {
                "enable" => new CliCommand { Action = CliAction.StartupEnable },
                "disable" => new CliCommand { Action = CliAction.StartupDisable },
                _ => new CliCommand(),
            };

            if (command.Action == CliAction.None)
            {
                error = CliSuggestionHelper.BuildUnknownValueError("startup mode", args[1], ["enable", "disable", "status", "open"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.StartupAll));
                return false;
            }

            if (tokens.Length > 2)
            {
                error = "Too many arguments for startup command.";
                return false;
            }

            return true;
        }

        private static bool TryParseStatusCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            if (!CliOutputFlagParser.TryParse(tokens, args, 1, out bool jsonOutput, out bool redactOutput, out error))
            {
                return false;
            }

            command = new CliCommand
            {
                Action = CliAction.Status,
                JsonOutput = jsonOutput,
                RedactOutput = redactOutput,
            };

            return true;
        }

        private static bool TryParseSwitchCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "switch" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing switch target. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Switch)}";
                return false;
            }

            bool reverse = false;
            bool muteMic = false;
            bool muteSound = false;
            bool deafen = false;
            bool dryRun = false;
            bool jsonOutput = false;
            bool redactOutput = false;
            bool showPaths = false;
            bool allowAnyPath = false;
            bool replaceImport = false;
            bool importModeSpecified = false;
            string? requireCurrentDeviceId = null;

            for (int i = 2; i < tokens.Length; i++)
            {
                switch (tokens[i])
                {
                    case "--reverse":
                        reverse = true;
                        break;
                    case "--mute-mic":
                        muteMic = true;
                        break;
                    case "--mute-sound":
                        muteSound = true;
                        break;
                    case "--deafen":
                        deafen = true;
                        break;
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "--require-current":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing device ID for --require-current.";
                            return false;
                        }

                        if (requireCurrentDeviceId != null)
                        {
                            error = "Duplicate --require-current flag.";
                            return false;
                        }

                        requireCurrentDeviceId = args[++i].Trim();
                        if (string.IsNullOrWhiteSpace(requireCurrentDeviceId))
                        {
                            error = "Missing device ID for --require-current.";
                            return false;
                        }

                        break;
                    default:
                        if (!CliOutputFlagParser.TryParseToken(
                            tokens[i],
                            CliOutputFlagOptions.None,
                            ref jsonOutput,
                            ref redactOutput,
                            ref showPaths,
                            ref allowAnyPath,
                            ref replaceImport,
                            ref importModeSpecified,
                            out bool handled,
                            out error))
                        {
                            return false;
                        }

                        if (handled)
                        {
                            break;
                        }

                        error = $"Unknown switch flag '{args[i]}'.";
                        return false;
                }
            }

            switch (tokens[1])
            {
                case "output":
                    command = new CliCommand
                    {
                        Action = CliAction.SwitchOutput,
                        Reverse = reverse,
                        MuteMic = muteMic,
                        MuteSound = muteSound,
                        Deafen = deafen,
                        DryRun = dryRun,
                        JsonOutput = jsonOutput,
                        RedactOutput = redactOutput,
                        Key = requireCurrentDeviceId,
                    };
                    return true;
                case "input":
                    if (muteMic || muteSound || deafen)
                    {
                        error = "Input switching supports --reverse, --dry-run, and --require-current, but not --mute-mic, --mute-sound, or --deafen.";
                        return false;
                    }

                    command = new CliCommand
                    {
                        Action = CliAction.SwitchInput,
                        Reverse = reverse,
                        DryRun = dryRun,
                        JsonOutput = jsonOutput,
                        RedactOutput = redactOutput,
                        Key = requireCurrentDeviceId,
                    };
                    return true;
                default:
                    error = CliSuggestionHelper.BuildUnknownValueError("switch target", args[1], ["output", "input"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Switch));
                    return false;
            }
        }
    }
}

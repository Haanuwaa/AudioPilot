using System.Globalization;

namespace AudioPilot.Cli
{
    public sealed partial class CliCommand
    {
        private static bool TryParseMediaCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "media" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing media command. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Media)}";
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
                    Action = CliAction.MediaStatus,
                    JsonOutput = jsonOutput,
                    RedactOutput = redactOutput,
                };
                return true;
            }

            if (tokens.Length > 2)
            {
                error = "Too many arguments for media command.";
                return false;
            }

            command = tokens[1] switch
            {
                "play-pause" => new CliCommand { Action = CliAction.MediaPlayPause },
                "next" => new CliCommand { Action = CliAction.MediaNextTrack },
                "previous" => new CliCommand { Action = CliAction.MediaPreviousTrack },
                _ => new CliCommand(),
            };

            if (command.Action == CliAction.None)
            {
                error = CliSuggestionHelper.BuildUnknownValueError("media command", args[1], ["play-pause", "next", "previous", "status"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Media));
                return false;
            }

            return true;
        }

        private static bool TryParseMuteCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "mute" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing mute target. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Mute)}";
                return false;
            }

            string mode = "toggle";
            int flagStartIndex = 2;
            if (tokens.Length >= 3 && !tokens[2].StartsWith("--", StringComparison.Ordinal))
            {
                mode = tokens[2];
                flagStartIndex = 3;
            }

            if (tokens.Length > flagStartIndex && !tokens[flagStartIndex].StartsWith("--", StringComparison.Ordinal))
            {
                error = "Too many arguments for mute command.";
                return false;
            }

            if (!CliOutputFlagParser.TryParse(tokens, args, flagStartIndex, out bool jsonOutput, out bool redactOutput, out error))
            {
                return false;
            }

            if (redactOutput)
            {
                error = "Mute commands do not support --redact.";
                return false;
            }

            command = (tokens[1], mode) switch
            {
                ("mic", "toggle") => new CliCommand { Action = CliAction.MuteMicToggle, JsonOutput = jsonOutput },
                ("mic", "on") => new CliCommand { Action = CliAction.MuteMicOn, JsonOutput = jsonOutput },
                ("mic", "off") => new CliCommand { Action = CliAction.MuteMicOff, JsonOutput = jsonOutput },
                ("sound", "toggle") => new CliCommand { Action = CliAction.MuteSoundToggle, JsonOutput = jsonOutput },
                ("sound", "on") => new CliCommand { Action = CliAction.MuteSoundOn, JsonOutput = jsonOutput },
                ("sound", "off") => new CliCommand { Action = CliAction.MuteSoundOff, JsonOutput = jsonOutput },
                ("deafen", "toggle") => new CliCommand { Action = CliAction.DeafenToggle, JsonOutput = jsonOutput },
                ("deafen", "on") => new CliCommand { Action = CliAction.DeafenOn, JsonOutput = jsonOutput },
                ("deafen", "off") => new CliCommand { Action = CliAction.DeafenOff, JsonOutput = jsonOutput },
                _ => new CliCommand(),
            };

            if (command.Action == CliAction.None)
            {
                if (tokens[1] is not ("mic" or "sound" or "deafen"))
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("mute target", args[1], ["mic", "sound", "deafen"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Mute));
                    return false;
                }

                error = CliSuggestionHelper.BuildUnknownValueError("mute mode", mode, ["toggle", "on", "off"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Mute));
                return false;
            }

            return true;
        }

        private static bool TryParseListenCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                error = null;
                command = new CliCommand { Action = CliAction.Help, Key = "listen" };
                return true;
            }

            string mode = tokens.Length >= 2 ? tokens[1] : "toggle";
            if (!CliOutputFlagParser.TryParse(tokens, args, 2, out bool jsonOutput, out bool redactOutput, out error))
            {
                return false;
            }

            command = mode switch
            {
                "toggle" => new CliCommand { Action = CliAction.ListenToggle, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                "on" => new CliCommand { Action = CliAction.ListenOn, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                "off" => new CliCommand { Action = CliAction.ListenOff, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                _ => new CliCommand(),
            };

            if (command.Action == CliAction.None)
            {
                error = CliSuggestionHelper.BuildUnknownValueError("listen command", args.Length > 1 ? args[1] : string.Empty, ["toggle", "on", "off"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Listen));
                return false;
            }

            return true;
        }

        private static bool TryParseVolumeCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "volume" };
                return true;
            }

            if (tokens.Length < 3)
            {
                error = $"Missing volume arguments. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Volume)}";
                return false;
            }

            if (tokens[1] == "get")
            {
                if (!TryParseVolumeFlags(tokens, args, 3, out bool jsonOutput, out bool redactOutput, out string? deviceId, out error))
                {
                    return false;
                }

                command = tokens[2] switch
                {
                    "master" => new CliCommand { Action = CliAction.VolumeGetMaster, Key = deviceId, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    "mic" => new CliCommand { Action = CliAction.VolumeGetMic, Key = deviceId, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    _ => new CliCommand(),
                };

                if (command.Action == CliAction.None)
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("volume target", args[2], ["master", "mic"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.VolumeGet));
                    return false;
                }

                return true;
            }

            if (tokens[1] == "set")
            {
                if (args.Length < 4)
                {
                    error = $"Missing volume percent. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.VolumeSet)}";
                    return false;
                }

                if (!float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float percent) || percent < 0f || percent > 100f)
                {
                    error = "Invalid volume percent. Use a number between 0 and 100.";
                    return false;
                }

                if (!TryParseVolumeFlags(tokens, args, 4, out bool jsonOutput, out bool redactOutput, out string? deviceId, out error))
                {
                    return false;
                }

                command = tokens[2] switch
                {
                    "master" => new CliCommand { Action = CliAction.VolumeSetMaster, Key = deviceId, Value = args[3], JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    "mic" => new CliCommand { Action = CliAction.VolumeSetMic, Key = deviceId, Value = args[3], JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    _ => new CliCommand(),
                };

                if (command.Action == CliAction.None)
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("volume target", args[2], ["master", "mic"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.VolumeSet));
                    return false;
                }

                return true;
            }

            error = CliSuggestionHelper.BuildUnknownValueError("volume command", args[1], ["get", "set"], "volume get|set master|mic ...");
            return false;
        }

        private static bool TryParseVolumeFlags(
            string[] tokens,
            string[] args,
            int startIndex,
            out bool jsonOutput,
            out bool redactOutput,
            out string? deviceSelector,
            out string? error)
        {
            jsonOutput = false;
            redactOutput = false;
            deviceSelector = null;
            List<string> sharedFlagTokens = [];
            List<string> sharedFlagArgs = [];
            for (int i = startIndex; i < tokens.Length; i++)
            {
                if (tokens[i] == "--device-id")
                {
                    if (deviceSelector != null)
                    {
                        error = "Conflicting device selector flags. Use either --device-id or --device.";
                        return false;
                    }

                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = "Missing device ID for --device-id.";
                        return false;
                    }

                    deviceSelector = CliDeviceSelectorResolver.EncodeExactId(args[++i]);
                    continue;
                }

                if (tokens[i] == "--device")
                {
                    if (deviceSelector != null)
                    {
                        error = "Conflicting device selector flags. Use either --device-id or --device.";
                        return false;
                    }

                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = "Missing device name for --device.";
                        return false;
                    }

                    deviceSelector = CliDeviceSelectorResolver.EncodeExactName(args[++i]);
                    continue;
                }

                sharedFlagTokens.Add(tokens[i]);
                sharedFlagArgs.Add(args[i]);
            }

            return CliOutputFlagParser.TryParse([.. sharedFlagTokens], [.. sharedFlagArgs], 0, out jsonOutput, out redactOutput, out error);
        }
    }
}

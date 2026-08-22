namespace AudioPilot.Cli
{
    public sealed partial class CliCommand
    {
        private static bool TryParseRoutineCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                error = null;
                command = new CliCommand { Action = CliAction.Help, Key = "routine" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing routine command. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Routine)}";
                return false;
            }

            if (tokens[1] is "pause" or "resume")
            {
                int flagStart = 2;
                string? minutes = null;
                if (tokens[1] == "pause" && HasRequiredValue(args, 2))
                {
                    minutes = args[2];
                    if (!int.TryParse(minutes, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) || value is < 1 or > Constants.AppConstants.Routines.AutomaticPauseMaxMinutes)
                    {
                        error = "Pause duration must be a whole number from 1 to 1440 minutes.";
                        return false;
                    }
                    flagStart = 3;
                }
                if (!CliOutputFlagParser.TryParse(tokens, args, flagStart, out bool json, out bool redact, out error)) return false;
                command = new CliCommand
                {
                    Action = tokens[1] == "pause" ? CliAction.RoutinePause : CliAction.RoutineResume,
                    Value = minutes,
                    JsonOutput = json,
                    RedactOutput = redact,
                };
                return true;
            }

            if (tokens[1] == "list")
            {
                if (!CliOutputFlagParser.TryParse(tokens, args, 2, out bool jsonOutput, out bool listRedactOutput, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.RoutineList,
                    JsonOutput = jsonOutput,
                    RedactOutput = listRedactOutput,
                };
                return true;
            }

            if (tokens[1] == "export")
            {
                if (!HasRequiredValue(args, 2))
                {
                    error = $"Missing export path. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.RoutineExport)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(
                    tokens,
                    args,
                    3,
                    CliOutputFlagOptions.AllowAnyPath,
                    out CliOutputFlagParseResult flags,
                    out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.RoutineExport,
                    Key = args[2],
                    JsonOutput = flags.JsonOutput,
                    RedactOutput = flags.RedactOutput,
                    AllowAnyPath = flags.AllowAnyPath,
                };
                return true;
            }

            if (tokens[1] == "create")
            {
                if (!HasRequiredValue(args, 2))
                {
                    error = $"Missing routine create path. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.RoutineCreate)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(
                    tokens,
                    args,
                    3,
                    CliOutputFlagOptions.AllowAnyPath,
                    out CliOutputFlagParseResult flags,
                    out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.RoutineCreate,
                    Key = args[2],
                    JsonOutput = flags.JsonOutput,
                    RedactOutput = flags.RedactOutput,
                    AllowAnyPath = flags.AllowAnyPath,
                };
                return true;
            }

            if (tokens[1] == "update")
            {
                if (!HasRequiredValue(args, 2) || !HasRequiredValue(args, 3))
                {
                    error = $"Missing routine update arguments. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.RoutineUpdate)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(
                    tokens,
                    args,
                    4,
                    CliOutputFlagOptions.AllowAnyPath,
                    out CliOutputFlagParseResult flags,
                    out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.RoutineUpdate,
                    Key = args[2],
                    Value = args[3],
                    JsonOutput = flags.JsonOutput,
                    RedactOutput = flags.RedactOutput,
                    AllowAnyPath = flags.AllowAnyPath,
                };
                return true;
            }

            if (tokens[1] == "import")
            {
                if (!HasRequiredValue(args, 2))
                {
                    error = $"Missing routine import path. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.RoutineImport)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(
                    tokens,
                    args,
                    3,
                    CliOutputFlagOptions.ReplaceImport | CliOutputFlagOptions.AllowAnyPath,
                    out CliOutputFlagParseResult flags,
                    out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.RoutineImport,
                    Key = args[2],
                    JsonOutput = flags.JsonOutput,
                    RedactOutput = flags.RedactOutput,
                    ReplaceImport = flags.ReplaceImport,
                    AllowAnyPath = flags.AllowAnyPath,
                };
                return true;
            }

            if (tokens[1] is not ("get" or "next" or "run" or "check" or "enable" or "disable" or "delete"))
            {
                error = CliSuggestionHelper.BuildUnknownValueError("routine command", args[1], ["list", "get", "next", "run", "check", "enable", "disable", "create", "update", "delete", "import", "export"], "routine list|get|next|run|check|enable|disable|create|update|delete|import|export ...");
                return false;
            }

            if (!HasRequiredValue(args, 2) && !(tokens[1] == "check" && tokens.Length > 2 && tokens[2] == "--all"))
            {
                error = $"Missing routine selector. Use: {CliCommandHelpMetadata.FormatParserUsage(CliCommandHelpMetadata.ParserUsageTemplateId.RoutineSelector, args[1])}";
                return false;
            }

            if (!CliOutputFlagParser.TryParse(tokens, args, 3, out bool actionJsonOutput, out bool redactOutput, out error))
            {
                return false;
            }

            command = new CliCommand
            {
                Action = tokens[1] switch
                {
                    "get" => CliAction.RoutineGet,
                    "next" => CliAction.RoutineNext,
                    "run" => CliAction.RoutineRun,
                    "check" => CliAction.RoutineCheck,
                    "enable" => CliAction.RoutineEnable,
                    "disable" => CliAction.RoutineDisable,
                    "delete" => CliAction.RoutineDelete,
                    _ => CliAction.None,
                },
                Key = tokens[1] == "check" && tokens[2] == "--all" ? "--all" : args[2],
                JsonOutput = actionJsonOutput,
                RedactOutput = redactOutput,
            };

            return true;
        }
    }
}

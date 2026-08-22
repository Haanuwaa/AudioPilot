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
                if (args.Length < 3)
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
                if (args.Length < 3)
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
                if (args.Length < 4)
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
                if (args.Length < 3)
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

            if (tokens[1] is not ("run" or "enable" or "disable" or "delete"))
            {
                error = CliSuggestionHelper.BuildUnknownValueError("routine command", args[1], ["list", "run", "enable", "disable", "create", "update", "delete", "import", "export"], "routine list|run|enable|disable|create|update|delete|import|export ...");
                return false;
            }

            if (args.Length < 3)
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
                    "run" => CliAction.RoutineRun,
                    "enable" => CliAction.RoutineEnable,
                    "disable" => CliAction.RoutineDisable,
                    "delete" => CliAction.RoutineDelete,
                    _ => CliAction.None,
                },
                Key = args[2],
                JsonOutput = actionJsonOutput,
                RedactOutput = redactOutput,
            };

            return true;
        }
    }
}

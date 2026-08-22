namespace AudioPilot.Cli
{
    public sealed partial class CliCommand
    {
        private static bool TryParseConfigCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "config" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing config arguments. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Config)}";
                return false;
            }

            if (tokens[1] == "list")
            {
                if (!CliOutputFlagParser.TryParse(tokens, args, 2, out bool jsonOutput, out _, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.ConfigList,
                    JsonOutput = jsonOutput,
                };
                return true;
            }

            if (tokens[1] == "get")
            {
                if (tokens.Length < 3)
                {
                    error = $"Missing config key. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.ConfigGet)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(tokens, args, 3, out bool jsonOutput, out bool redactOutput, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.ConfigGet,
                    Key = args[2],
                    JsonOutput = jsonOutput,
                    RedactOutput = redactOutput,
                };
                return true;
            }

            if (tokens[1] == "set")
            {
                if (args.Length < 4)
                {
                    error = $"Missing config value. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.ConfigSet)}";
                    return false;
                }

                string value = string.Join(" ", args.Skip(3));
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = "Config value cannot be empty.";
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.ConfigSet,
                    Key = args[2],
                    Value = value,
                };
                return true;
            }

            if (tokens[1] == "export")
            {
                if (args.Length < 3)
                {
                    error = $"Missing export path. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.ConfigExport)}";
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
                    if (!string.IsNullOrWhiteSpace(error) && error.StartsWith("Unknown flag '", StringComparison.Ordinal))
                    {
                        error = error.Replace("Unknown flag", "Unknown export flag", StringComparison.Ordinal);
                        error += " Use --json, --redact, or --allow-any-path.";
                    }

                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.ConfigExport,
                    Key = args[2],
                    JsonOutput = flags.JsonOutput,
                    AllowAnyPath = flags.AllowAnyPath,
                    RedactOutput = flags.RedactOutput,
                };
                return true;
            }

            if (tokens[1] == "import")
            {
                return TryParseConfigImportCommand(tokens, args, out command, out error);
            }

            if (tokens[1] == "validate")
            {
                if (!CliOutputFlagParser.TryParse(tokens, args, 2, out bool jsonOutput, out bool redactOutput, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.ConfigValidate,
                    JsonOutput = jsonOutput,
                    RedactOutput = redactOutput,
                };
                return true;
            }

            error = CliSuggestionHelper.BuildUnknownValueError("config command", args[1], ["list", "get", "set", "validate", "export", "import"], "config list|get|set|validate|export|import ...");
            return false;
        }

        private static bool TryParseRuntimeCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "runtime" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing runtime arguments. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Runtime)}";
                return false;
            }

            if (tokens[1] == "list")
            {
                if (!CliOutputFlagParser.TryParse(tokens, args, 2, out bool jsonOutput, out _, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.RuntimeList,
                    JsonOutput = jsonOutput,
                };
                return true;
            }

            if (tokens[1] == "get")
            {
                if (tokens.Length < 3)
                {
                    error = $"Missing runtime key. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.RuntimeGet)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(tokens, args, 3, out bool jsonOutput, out bool redactOutput, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.RuntimeGet,
                    Key = args[2],
                    JsonOutput = jsonOutput,
                    RedactOutput = redactOutput,
                };
                return true;
            }

            if (tokens[1] == "set")
            {
                if (args.Length < 4)
                {
                    error = $"Missing runtime value. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.RuntimeSet)}";
                    return false;
                }

                string value = string.Join(" ", args.Skip(3));
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = "Runtime value cannot be empty.";
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.RuntimeSet,
                    Key = args[2],
                    Value = value,
                };
                return true;
            }

            error = CliSuggestionHelper.BuildUnknownValueError("runtime command", args[1], ["list", "get", "set"], "runtime list|get|set ...");
            return false;
        }

        private static bool TryParseConfigImportCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();

            if (args.Length < 3)
            {
                error = $"Missing import path. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.ConfigImport)}";
                return false;
            }

            bool replaceImport = false;
            bool sawMerge = false;
            bool sawReplace = false;
            List<string> sharedFlagTokens = [];
            List<string> sharedFlagArgs = [];
            for (int i = 3; i < tokens.Length; i++)
            {
                if (tokens[i] == "--replace")
                {
                    replaceImport = true;
                    sawReplace = true;
                    continue;
                }

                if (tokens[i] == "--merge")
                {
                    replaceImport = false;
                    sawMerge = true;
                    continue;
                }

                sharedFlagTokens.Add(tokens[i]);
                sharedFlagArgs.Add(args[i]);
            }

            if (!CliOutputFlagParser.TryParse(
                [.. sharedFlagTokens],
                [.. sharedFlagArgs],
                0,
                CliOutputFlagOptions.AllowAnyPath,
                out CliOutputFlagParseResult flags,
                out error))
            {
                return false;
            }

            if (flags.ReplaceImport || (sawMerge && sawReplace))
            {
                error = "Use either --merge or --replace, not both.";
                return false;
            }

            command = new CliCommand
            {
                Action = CliAction.ConfigImport,
                Key = args[2],
                ReplaceImport = replaceImport,
                JsonOutput = flags.JsonOutput,
                RedactOutput = flags.RedactOutput,
                AllowAnyPath = flags.AllowAnyPath,
            };

            return true;
        }
    }
}

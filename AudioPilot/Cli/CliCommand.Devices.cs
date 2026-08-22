namespace AudioPilot.Cli
{
    public sealed partial class CliCommand
    {
        private static bool TryParseDevicesCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                error = null;
                command = new CliCommand { Action = CliAction.Help, Key = "devices" };
                return true;
            }

            if (tokens.Length < 2 || tokens[1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Missing devices arguments. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Devices)}";
                return false;
            }

            if (tokens[1] == "list")
            {
                if (tokens.Length < 3)
                {
                    error = $"Missing devices arguments. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.DevicesList)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(tokens, args, 3, out bool jsonOutput, out bool redactOutput, out error))
                {
                    return false;
                }

                command = tokens[2] switch
                {
                    "output" => new CliCommand { Action = CliAction.DevicesListOutput, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    "input" => new CliCommand { Action = CliAction.DevicesListInput, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    _ => new CliCommand(),
                };

                if (command.Action == CliAction.None)
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("devices target", args[2], ["output", "input"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.DevicesList));
                    return false;
                }

                return true;
            }

            if (tokens[1] == "get")
            {
                if (tokens.Length < 4)
                {
                    error = $"Missing device selector. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.DevicesGet)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(tokens, args, 4, out bool jsonOutput, out bool redactOutput, out error))
                {
                    return false;
                }

                command = tokens[2] switch
                {
                    "output" => new CliCommand { Action = CliAction.DevicesGetOutput, Key = args[3], JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    "input" => new CliCommand { Action = CliAction.DevicesGetInput, Key = args[3], JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    _ => new CliCommand(),
                };

                if (command.Action == CliAction.None)
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("devices target", args[2], ["output", "input"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.DevicesGet));
                    return false;
                }

                return true;
            }

            if (tokens[1] == "find")
            {
                if (tokens.Length < 4)
                {
                    error = $"Missing search query. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.DevicesFind)}";
                    return false;
                }

                int flagStartIndex = tokens.Length;
                for (int index = 3; index < tokens.Length; index++)
                {
                    if (tokens[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        flagStartIndex = index;
                        break;
                    }
                }

                string query = string.Join(" ", args[3..flagStartIndex]).Trim();
                if (string.IsNullOrWhiteSpace(query))
                {
                    error = $"Missing search query. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.DevicesFind)}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(tokens, args, flagStartIndex, out bool jsonOutput, out bool redactOutput, out error))
                {
                    return false;
                }

                command = tokens[2] switch
                {
                    "output" => new CliCommand { Action = CliAction.DevicesFindOutput, Key = query, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    "input" => new CliCommand { Action = CliAction.DevicesFindInput, Key = query, JsonOutput = jsonOutput, RedactOutput = redactOutput },
                    _ => new CliCommand(),
                };

                if (command.Action == CliAction.None)
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("devices target", args[2], ["output", "input"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.DevicesFind));
                    return false;
                }

                return true;
            }

            error = CliSuggestionHelper.BuildUnknownValueError("devices command", args[1], ["list", "get", "find"], "devices list|get|find ...");
            return false;
        }

        private static bool TryParseCycleCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                error = null;
                command = new CliCommand { Action = CliAction.Help, Key = "cycle" };
                return true;
            }

            if (tokens.Length < 3)
            {
                error = $"Missing cycle arguments. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Cycle)}";
                return false;
            }

            if (tokens[1] is not ("show" or "validate" or "test" or "add" or "remove" or "reorder"))
            {
                error = CliSuggestionHelper.BuildUnknownValueError("cycle command", args[1], ["show", "validate", "test", "add", "remove", "reorder"], "cycle show|validate|test|add|remove|reorder output|input");
                return false;
            }

            if (tokens[1] == "add" || tokens[1] == "remove")
            {
                if (args.Length < 4)
                {
                    error = $"Missing cycle device id. Use: {CliCommandHelpMetadata.FormatParserUsage(CliCommandHelpMetadata.ParserUsageTemplateId.CycleMutation, args[1])}";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(tokens, args, 4, out bool mutationJsonOutput, out bool mutationRedactOutput, out error))
                {
                    return false;
                }

                if (!TryNormalizeCycleMutationDeviceId(args[3], args[1], out string cycleDeviceId, out error))
                {
                    return false;
                }

                command = tokens[2] switch
                {
                    "output" => new CliCommand
                    {
                        Action = tokens[1] == "add" ? CliAction.CycleAddOutput : CliAction.CycleRemoveOutput,
                        Key = cycleDeviceId,
                        JsonOutput = mutationJsonOutput,
                        RedactOutput = mutationRedactOutput,
                    },
                    "input" => new CliCommand
                    {
                        Action = tokens[1] == "add" ? CliAction.CycleAddInput : CliAction.CycleRemoveInput,
                        Key = cycleDeviceId,
                        JsonOutput = mutationJsonOutput,
                        RedactOutput = mutationRedactOutput,
                    },
                    _ => new CliCommand(),
                };

                if (command.Action == CliAction.None)
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("cycle target", args[2], ["output", "input"], CliCommandHelpMetadata.FormatParserUsage(CliCommandHelpMetadata.ParserUsageTemplateId.CycleMutation, args[1]));
                    return false;
                }

                return true;
            }

            if (tokens[1] == "reorder")
            {
                if (args.Length < 5)
                {
                    error = $"Missing cycle reorder device ids. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.CycleReorder)}";
                    return false;
                }

                int flagStartIndex = tokens.Length;
                for (int index = 3; index < tokens.Length; index++)
                {
                    if (tokens[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        flagStartIndex = index;
                        break;
                    }
                }

                string[] orderedIds = args[3..flagStartIndex];
                if (!TryNormalizeCycleReorderDeviceIds(orderedIds, out string[] normalizedOrderedIds, out error))
                {
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(tokens, args, flagStartIndex, out bool reorderJsonOutput, out bool reorderRedactOutput, out error))
                {
                    return false;
                }

                command = tokens[2] switch
                {
                    "output" => new CliCommand
                    {
                        Action = CliAction.CycleReorderOutput,
                        Value = string.Join("\n", normalizedOrderedIds),
                        JsonOutput = reorderJsonOutput,
                        RedactOutput = reorderRedactOutput,
                    },
                    "input" => new CliCommand
                    {
                        Action = CliAction.CycleReorderInput,
                        Value = string.Join("\n", normalizedOrderedIds),
                        JsonOutput = reorderJsonOutput,
                        RedactOutput = reorderRedactOutput,
                    },
                    _ => new CliCommand(),
                };

                if (command.Action == CliAction.None)
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("cycle target", args[2], ["output", "input"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.CycleReorder));
                    return false;
                }

                return true;
            }

            if (tokens[1] == "test")
            {
                if (!CliOutputFlagParser.TryParse(tokens, args, 3, out bool testJsonOutput, out bool testRedactOutput, out error))
                {
                    return false;
                }

                command = tokens[2] switch
                {
                    "output" => new CliCommand { Action = CliAction.CycleTestOutput, JsonOutput = testJsonOutput, RedactOutput = testRedactOutput },
                    "input" => new CliCommand { Action = CliAction.CycleTestInput, JsonOutput = testJsonOutput, RedactOutput = testRedactOutput },
                    _ => new CliCommand(),
                };

                if (command.Action == CliAction.None)
                {
                    error = CliSuggestionHelper.BuildUnknownValueError("cycle target", args[2], ["output", "input"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.CycleTest));
                    return false;
                }

                return true;
            }

            if (!CliOutputFlagParser.TryParse(tokens, args, 3, out bool cycleJsonOutput, out bool cycleRedactOutput, out error))
            {
                return false;
            }

            command = tokens[2] switch
            {
                "output" => new CliCommand
                {
                    Action = tokens[1] == "show" ? CliAction.CycleShowOutput : CliAction.CycleValidateOutput,
                    JsonOutput = cycleJsonOutput,
                    RedactOutput = cycleRedactOutput,
                },
                "input" => new CliCommand
                {
                    Action = tokens[1] == "show" ? CliAction.CycleShowInput : CliAction.CycleValidateInput,
                    JsonOutput = cycleJsonOutput,
                    RedactOutput = cycleRedactOutput,
                },
                _ => new CliCommand(),
            };

            if (command.Action == CliAction.None)
            {
                error = CliSuggestionHelper.BuildUnknownValueError("cycle target", args[2], ["output", "input"], CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.CycleShowValidate));
                return false;
            }

            return true;
        }

        private static bool TryNormalizeCycleMutationDeviceId(string? deviceId, string action, out string normalizedDeviceId, out string? error)
        {
            normalizedDeviceId = deviceId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedDeviceId))
            {
                error = $"Missing cycle device id. Use: {CliCommandHelpMetadata.FormatParserUsage(CliCommandHelpMetadata.ParserUsageTemplateId.CycleMutation, action)}";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryNormalizeCycleReorderDeviceIds(string[] orderedIds, out string[] normalizedOrderedIds, out string? error)
        {
            if (orderedIds.Length == 0)
            {
                normalizedOrderedIds = [];
                error = $"Missing cycle reorder device ids. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.CycleReorder)}";
                return false;
            }

            normalizedOrderedIds = new string[orderedIds.Length];
            HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < orderedIds.Length; index++)
            {
                string normalizedId = orderedIds[index].Trim();
                if (string.IsNullOrWhiteSpace(normalizedId))
                {
                    error = $"Cycle reorder device ids must not be blank. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.CycleReorder)}";
                    return false;
                }

                if (!seenIds.Add(normalizedId))
                {
                    error = $"Duplicate cycle reorder device id '{normalizedId}'. Pass every existing cycle device id exactly once.";
                    return false;
                }

                normalizedOrderedIds[index] = normalizedId;
            }

            error = null;
            return true;
        }
    }
}

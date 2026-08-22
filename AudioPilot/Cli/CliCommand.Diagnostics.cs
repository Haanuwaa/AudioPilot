using System.Globalization;

namespace AudioPilot.Cli
{
    public sealed partial class CliCommand
    {
        private static bool TryParseDiagnosticsCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
        {
            command = new CliCommand();
            error = null;

            if (tokens.Length >= 2 && IsHelpToken(tokens[1]))
            {
                command = new CliCommand { Action = CliAction.Help, Key = "diagnostics" };
                return true;
            }

            if (tokens.Length < 2)
            {
                error = $"Missing diagnostics command. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Diagnostics)}";
                return false;
            }

            if (tokens[1] == "refresh")
            {
                return TryParseRefreshCommand(tokens, args, out command, out error, startIndex: 2);
            }

            if (tokens[1] == "status")
            {
                if (!CliOutputFlagParser.TryParse(
                    tokens,
                    args,
                    2,
                    CliOutputFlagOptions.ShowPaths,
                    out CliOutputFlagParseResult flags,
                    out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.DiagnosticsStatus,
                    JsonOutput = flags.JsonOutput,
                    ShowPaths = flags.ShowPaths,
                    RedactOutput = flags.RedactOutput,
                };
                return true;
            }

            if (tokens[1] == "history")
            {
                bool jsonOutput = false;
                bool redactOutput = false;
                bool showPaths = false;
                bool allowAnyPath = false;
                bool replaceImport = false;
                bool importModeSpecified = false;
                int? limit = null;
                string? type = null;

                for (int index = 2; index < tokens.Length; index++)
                {
                    if (tokens[index] == "--limit")
                    {
                        if (limit.HasValue)
                        {
                            error = "Duplicate --limit flag.";
                            return false;
                        }

                        if (index + 1 >= tokens.Length || !int.TryParse(tokens[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit) || parsedLimit <= 0)
                        {
                            error = "Invalid value for --limit. Use a positive integer.";
                            return false;
                        }

                        limit = parsedLimit;
                        index++;
                        continue;
                    }

                    if (tokens[index] == "--type")
                    {
                        if (type != null)
                        {
                            error = "Duplicate --type flag.";
                            return false;
                        }

                        if (index + 1 >= tokens.Length)
                        {
                            error = "Missing value for --type. Use routine, switch, media, or mute.";
                            return false;
                        }

                        string parsedType = NormalizeToken(tokens[index + 1]);
                        if (parsedType is not ("routine" or "switch" or "media" or "mute"))
                        {
                            error = $"Unsupported history type '{args[index + 1]}'. Use routine, switch, media, or mute.";
                            return false;
                        }

                        type = parsedType;
                        index++;
                        continue;
                    }

                    if (!CliOutputFlagParser.TryParseToken(
                        tokens[index],
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
                        continue;
                    }

                    error = $"Unknown flag '{args[index]}' for diagnostics history.";
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.DiagnosticsHistory,
                    JsonOutput = jsonOutput,
                    RedactOutput = redactOutput,
                    Key = type,
                    Limit = limit,
                };
                return true;
            }

            if (tokens[1] == "history-detail")
            {
                if (tokens.Length < 3)
                {
                    error = "Missing operation id for diagnostics history-detail.";
                    return false;
                }

                if (!CliOutputFlagParser.TryParse(tokens, args, 3, CliOutputFlagOptions.None, out CliOutputFlagParseResult flags, out error))
                {
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.DiagnosticsHistoryDetail,
                    Key = args[2],
                    JsonOutput = flags.JsonOutput,
                    RedactOutput = flags.RedactOutput,
                };
                return true;
            }

            if (tokens[1] == "export-logs")
            {
                if (tokens.Length < 3)
                {
                    error = $"Missing diagnostics export path. Use: {CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.DiagnosticsExport)}";
                    return false;
                }

                bool jsonOutput = false;
                bool redactOutput = false;
                bool showPaths = false;
                bool allowAnyPath = false;
                bool replaceImport = false;
                bool importModeSpecified = false;
                bool detailSpecified = false;
                CliDiagnosticsExportDetailLevel detailLevel = CliDiagnosticsExportDetailLevel.Summary;

                for (int index = 3; index < tokens.Length; index++)
                {
                    if (tokens[index] == "--detail")
                    {
                        if (detailSpecified)
                        {
                            error = "Duplicate --detail flag.";
                            return false;
                        }

                        if (index + 1 >= tokens.Length)
                        {
                            error = "Missing value for --detail. Use summary or manifest.";
                            return false;
                        }

                        if (!TryParseDiagnosticsExportDetailLevel(tokens[index + 1], out detailLevel))
                        {
                            error = $"Unsupported diagnostics export detail '{args[index + 1]}'. Use summary or manifest.";
                            return false;
                        }

                        detailSpecified = true;
                        index++;
                        continue;
                    }

                    if (!CliOutputFlagParser.TryParseToken(
                        tokens[index],
                        CliOutputFlagOptions.AllowAnyPath,
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
                        continue;
                    }

                    error = $"Unknown flag '{args[index]}'.";
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.DiagnosticsExportLogs,
                    Key = args[2],
                    JsonOutput = jsonOutput,
                    RedactOutput = redactOutput,
                    AllowAnyPath = allowAnyPath,
                    DiagnosticsExportDetailLevel = detailLevel,
                };
                return true;
            }

            if (tokens[1] == "export-bundle")
            {
                if (tokens.Length < 3)
                {
                    error = "Missing diagnostics bundle export path. Use: diagnostics export-bundle <path.zip> [--json] [--detail summary|manifest] [--allow-any-path] [--include-sensitive]";
                    return false;
                }

                bool jsonOutput = false;
                bool redactOutput = false;
                bool showPaths = false;
                bool allowAnyPath = false;
                bool replaceImport = false;
                bool importModeSpecified = false;
                bool detailSpecified = false;
                bool includeSensitive = false;
                CliDiagnosticsExportDetailLevel detailLevel = CliDiagnosticsExportDetailLevel.Summary;

                for (int index = 3; index < tokens.Length; index++)
                {
                    if (tokens[index] == "--detail")
                    {
                        if (detailSpecified)
                        {
                            error = "Duplicate --detail flag.";
                            return false;
                        }

                        if (index + 1 >= tokens.Length)
                        {
                            error = "Missing value for --detail. Use summary or manifest.";
                            return false;
                        }

                        if (!TryParseDiagnosticsExportDetailLevel(tokens[index + 1], out detailLevel))
                        {
                            error = $"Unsupported diagnostics bundle detail '{args[index + 1]}'. Use summary or manifest.";
                            return false;
                        }

                        detailSpecified = true;
                        index++;
                        continue;
                    }

                    if (tokens[index] == "--include-sensitive")
                    {
                        if (includeSensitive)
                        {
                            error = "Duplicate --include-sensitive flag.";
                            return false;
                        }

                        includeSensitive = true;
                        continue;
                    }

                    if (tokens[index] == "--redact")
                    {
                        error = "diagnostics export-bundle is redacted by default. Use --include-sensitive only for local, private troubleshooting.";
                        return false;
                    }

                    if (!CliOutputFlagParser.TryParseToken(
                        tokens[index],
                        CliOutputFlagOptions.AllowAnyPath,
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
                        continue;
                    }

                    error = $"Unknown flag '{args[index]}'.";
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.DiagnosticsExportBundle,
                    Key = args[2],
                    JsonOutput = jsonOutput,
                    AllowAnyPath = allowAnyPath,
                    IncludeSensitive = includeSensitive,
                    DiagnosticsExportDetailLevel = detailLevel,
                };
                return true;
            }

            if (tokens[1] == "reset-per-app-audio")
            {
                if (!CliOutputFlagParser.TryParse(tokens, args, 2, CliOutputFlagOptions.None, out CliOutputFlagParseResult flags, out error))
                {
                    return false;
                }

                if (flags.RedactOutput)
                {
                    error = "diagnostics reset-per-app-audio does not support --redact.";
                    return false;
                }

                command = new CliCommand
                {
                    Action = CliAction.DiagnosticsResetPerAppAudio,
                    JsonOutput = flags.JsonOutput,
                };
                return true;
            }

            error = CliSuggestionHelper.BuildUnknownValueError(
                "diagnostics command",
                args[1],
                ["refresh", "status", "history", "history-detail", "export-logs", "export-bundle", "reset-per-app-audio"],
                "diagnostics refresh | diagnostics status [--json] [--redact] [--show-paths] | diagnostics history [--limit <n>] [--type routine|switch|media|mute] [--json] [--redact] | diagnostics history-detail <opId> [--json] [--redact] | diagnostics export-logs <path.zip> [--json] [--redact] [--detail summary|manifest] [--allow-any-path] | diagnostics export-bundle <path.zip> [--json] [--detail summary|manifest] [--allow-any-path] [--include-sensitive] | diagnostics reset-per-app-audio [--json]");
            return false;
        }

        private static bool TryParseDiagnosticsExportDetailLevel(string? token, out CliDiagnosticsExportDetailLevel detailLevel)
        {
            detailLevel = CliDiagnosticsExportDetailLevel.Summary;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string normalizedToken = NormalizeToken(token);
            if (string.Equals(normalizedToken, "summary", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(normalizedToken, "manifest", StringComparison.Ordinal))
            {
                detailLevel = CliDiagnosticsExportDetailLevel.Manifest;
                return true;
            }

            return false;
        }
    }
}

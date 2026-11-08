namespace AudioPilot.Cli
{
    [Flags]
    internal enum CliOutputFlagOptions
    {
        None = 0,
        ShowPaths = 1,
        AllowAnyPath = 2,
        ReplaceImport = 4,
    }

    internal readonly record struct CliOutputFlagParseResult(
        bool JsonOutput,
        bool RedactOutput,
        bool ShowPaths = false,
        bool AllowAnyPath = false,
        bool ReplaceImport = false);

    internal static class CliOutputFlagParser
    {
        public static bool TryParseToken(
            string normalizedToken,
            CliOutputFlagOptions options,
            ref bool jsonOutput,
            ref bool redactOutput,
            ref bool showPaths,
            ref bool allowAnyPath,
            ref bool replaceImport,
            ref bool importModeSpecified,
            out bool handled,
            out string? error)
        {
            handled = true;
            error = null;

            if (normalizedToken == "--json")
            {
                if (jsonOutput)
                {
                    error = "Duplicate --json flag.";
                    return false;
                }

                jsonOutput = true;
                return true;
            }

            if (normalizedToken == "--redact")
            {
                if (redactOutput)
                {
                    error = "Duplicate --redact flag.";
                    return false;
                }

                redactOutput = true;
                return true;
            }

            if (normalizedToken == "--show-paths" && options.HasFlag(CliOutputFlagOptions.ShowPaths))
            {
                if (showPaths)
                {
                    error = "Duplicate --show-paths flag.";
                    return false;
                }

                showPaths = true;
                return true;
            }

            if (normalizedToken == "--allow-any-path" && options.HasFlag(CliOutputFlagOptions.AllowAnyPath))
            {
                if (allowAnyPath)
                {
                    error = "Duplicate --allow-any-path flag.";
                    return false;
                }

                allowAnyPath = true;
                return true;
            }

            if ((normalizedToken == "--replace" || normalizedToken == "--merge") && options.HasFlag(CliOutputFlagOptions.ReplaceImport))
            {
                if (importModeSpecified)
                {
                    error = "Duplicate import mode flag.";
                    return false;
                }

                importModeSpecified = true;
                replaceImport = normalizedToken == "--replace";
                return true;
            }

            handled = false;
            return true;
        }

        public static bool TryParse(string[] normalizedTokens, string[] originalArgs, int startIndex, out bool jsonOutput, out bool redactOutput, out string? error)
        {
            bool parsed = TryParse(
                normalizedTokens,
                originalArgs,
                startIndex,
                CliOutputFlagOptions.None,
                out CliOutputFlagParseResult result,
                out error);
            jsonOutput = result.JsonOutput;
            redactOutput = result.RedactOutput;
            return parsed;
        }

        public static bool TryParse(
            string[] normalizedTokens,
            string[] originalArgs,
            int startIndex,
            CliOutputFlagOptions options,
            out CliOutputFlagParseResult result,
            out string? error)
        {
            bool jsonOutput = false;
            bool redactOutput = false;
            bool showPaths = false;
            bool allowAnyPath = false;
            bool replaceImport = false;
            bool importModeSpecified = false;
            error = null;

            for (int i = startIndex; i < normalizedTokens.Length; i++)
            {
                if (!TryParseToken(
                    normalizedTokens[i],
                    options,
                    ref jsonOutput,
                    ref redactOutput,
                    ref showPaths,
                    ref allowAnyPath,
                    ref replaceImport,
                    ref importModeSpecified,
                    out bool handled,
                    out error))
                {
                    result = default;
                    return false;
                }

                if (handled)
                {
                    continue;
                }

                error = $"Unknown flag '{originalArgs[i]}'.";
                result = default;
                return false;
            }

            result = new CliOutputFlagParseResult(jsonOutput, redactOutput, showPaths, allowAnyPath, replaceImport);
            jsonOutput = result.JsonOutput;
            redactOutput = result.RedactOutput;
            return true;
        }
    }
}

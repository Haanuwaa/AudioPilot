using System.IO;

namespace AudioPilot.Cli
{
    public static class CliHostUtilities
    {
        private const string DefaultCliExecutableName = "AudioPilot.Cli.exe";

        public static string? InferHelpTopic(string[] args)
        {
            if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                return null;
            }

            return args[0].Trim().ToLowerInvariant() switch
            {
                "diagnostics" or "media" or "mute" or "listen" or "volume" or "routine" or "config" or "runtime" or "devices" or "cycle" or "switch" or "wait" or "startup" => args[0].Trim().ToLowerInvariant(),
                _ => null,
            };
        }

        public static bool PrefersJson(string[] args)
        {
            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], "--json", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsUiOnlyAction(CliAction action)
        {
            return action is CliAction.Show or CliAction.Hide or CliAction.StartupOpen;
        }

        public static string FormatTextError(string errorCode, string message)
        {
            return $"[diag-code:{errorCode}] {message}";
        }

        public static string ResolveCliExecutableName(string? executablePathOrName = null)
        {
            string? candidate = executablePathOrName;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = Environment.ProcessPath;
            }

            string? fileName = string.IsNullOrWhiteSpace(candidate)
                ? null
                : Path.GetFileName(candidate);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return DefaultCliExecutableName;
            }

            return string.Equals(fileName, "testhost.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "testhost.dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
                ? DefaultCliExecutableName
                : fileName;
        }

        public static string FormatSuggestedHelpCommand(string topic, string? executablePathOrName = null)
        {
            return $"{ResolveCliExecutableName(executablePathOrName)} help {topic}";
        }

        public static void WriteCliError(TextWriter errorWriter, int exitCode, string errorCode, string message, bool jsonOutput, bool includeUsage, string? suggestedHelpTopic = null, string? helpExecutablePathOrName = null)
        {
            if (jsonOutput)
            {
                errorWriter.WriteLine(CliCommandExecutor.BuildJsonErrorPayload(exitCode, errorCode, message));
                return;
            }

            errorWriter.WriteLine(FormatTextError(errorCode, message));
            if (!string.IsNullOrWhiteSpace(suggestedHelpTopic))
            {
                errorWriter.WriteLine($"For more help: {FormatSuggestedHelpCommand(suggestedHelpTopic, helpExecutablePathOrName)}");
            }

            if (includeUsage)
            {
                errorWriter.WriteLine(CliCommand.UsageText);
            }
        }
    }
}

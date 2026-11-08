namespace AudioPilot.Cli;

public sealed partial class CliCommand
{
    private static bool TryParseUpdateCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
    {
        command = new(); error = null;
        string usage = CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Update);
        if (tokens.Length < 2 || tokens[1] is not ("status" or "check" or "apply" or "resume" or "pause" or "cancel"))
        { error = "Use: " + usage; return false; }
        bool json = false, approved = false;
        string? version = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 2; index < tokens.Length; index++)
        {
            if (!seen.Add(tokens[index])) { error = "Repeated option. Use: " + usage; return false; }
            switch (tokens[index])
            {
                case "--json": json = true; break;
                case "--approve": approved = true; break;
                case "--version" when HasRequiredValue(args, index + 1): version = args[++index]; break;
                default: error = "Unknown or incomplete update option. Use: " + usage; return false;
            }
        }
        bool mutation = tokens[1] is "apply" or "resume";
        if (mutation ? !approved || !IsUpdateVersion(version) : approved || version != null)
        { error = "apply/resume require an exact --version and --approve. Other commands accept only --json. Use: " + usage; return false; }
        command = new() { Action = CliAction.Update, Key = tokens[1], Value = version, ApproveUpdate = approved, JsonOutput = json };
        return true;
    }

    private static bool IsUpdateVersion(string? text) => Version.TryParse(text, out var version) && version.Build >= 0 && version.Revision == -1 && version.ToString(3) == text;
    private static bool ValidateUpdateValues(string? operation, string? version, bool approved) => operation is "apply" or "resume"
        ? approved && IsUpdateVersion(version) : operation is "status" or "check" or "pause" or "cancel" && version == null && !approved;
}

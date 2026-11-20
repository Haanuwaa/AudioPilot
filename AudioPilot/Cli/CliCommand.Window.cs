using System.Globalization;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Cli;

public sealed partial class CliCommand
{
    private static bool TryParseWindowCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
    {
        command = new();
        error = null;
        string usage = CliCommandHelpMetadata.GetParserUsage(CliCommandHelpMetadata.ParserUsageId.Window);
        if (tokens.Length < 2 || tokens[1] is not ("status" or "set" or "reset"))
        {
            error = $"Use: {usage}";
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? x = null, y = null, width = null, height = null;
        bool json = false;
        for (int index = 2; index < tokens.Length; index++)
        {
            string option = tokens[index];
            if (!seen.Add(option)) { error = $"Repeated option: {args[index]}. Use: {usage}"; return false; }
            if (option == "--json") { json = true; continue; }
            if (tokens[1] != "set" || option is not ("--x" or "--y" or "--width" or "--height"))
            {
                error = $"Unknown window option: {args[index]}. Use: {usage}";
                return false;
            }
            if (!HasRequiredValue(args, index + 1) || !int.TryParse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                error = $"{option} requires an integer in physical pixels. Use: {usage}";
                return false;
            }
            switch (option)
            {
                case "--x": x = value; break;
                case "--y": y = value; break;
                case "--width": width = value; break;
                case "--height": height = value; break;
            }
        }
        WindowLayoutChange? change = tokens[1] == "set" ? new(x, y, width, height) : null;
        if (change != null)
        {
            try { WindowPlacementHelper.ValidateLayoutChange(change); }
            catch (ArgumentException ex) { error = $"{ex.Message} Use: {usage}"; return false; }
        }
        command = new()
        {
            Action = tokens[1] switch { "set" => CliAction.WindowSet, "reset" => CliAction.WindowReset, _ => CliAction.WindowStatus },
            WindowLayout = change,
            JsonOutput = json
        };
        return true;
    }
}

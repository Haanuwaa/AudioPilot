using System.Globalization;

namespace AudioPilot.Cli;

public sealed partial class CliCommand
{
    private static bool TryParseAppCommand(string[] tokens, string[] args, out CliCommand command, out string? error)
    {
        command = new();
        error = null;
        if (tokens.Length < 2)
        {
            error = "Use: app list|volume|mute. See help app.";
            return false;
        }
        CliAction action;
        int next;
        if (tokens[1] == "list") { action = CliAction.AppList; next = 2; }
        else if (tokens.Length >= 3 && tokens[1] == "volume")
        {
            action = tokens[2] switch
            {
                "get" => CliAction.AppVolumeGet,
                "set" => CliAction.AppVolumeSet,
                "adjust" => CliAction.AppVolumeAdjust,
                _ => CliAction.None,
            };
            next = 3;
        }
        else if (tokens.Length >= 3 && tokens[1] == "mute")
        {
            action = tokens[2] switch
            {
                "on" => CliAction.AppMuteOn,
                "off" => CliAction.AppMuteOff,
                "toggle" => CliAction.AppMuteToggle,
                _ => CliAction.None,
            };
            next = 3;
        }
        else { action = CliAction.None; next = tokens.Length; }
        if (action == CliAction.None)
        {
            error = "Unknown application audio command. See help app.";
            return false;
        }

        string? value = null;
        if (action is CliAction.AppVolumeSet or CliAction.AppVolumeAdjust)
        {
            if (next >= args.Length || !float.TryParse(args[next], NumberStyles.Float, CultureInfo.InvariantCulture, out float percent)
                || !float.IsFinite(percent) || percent > 100 || percent < (action == CliAction.AppVolumeAdjust ? -100 : 0))
            {
                error = action == CliAction.AppVolumeAdjust ? "Use an adjustment from -100 to 100 percentage points." : "Use a volume from 0 to 100 percent.";
                return false;
            }
            value = percent.ToString(CultureInfo.InvariantCulture);
            next++;
        }
        string? selector = null;
        bool json = false, redact = false;
        for (; next < tokens.Length; next++)
        {
            if (tokens[next] == "--json" && !json) { json = true; continue; }
            if (tokens[next] == "--redact" && !redact) { redact = true; continue; }
            if (tokens[next] is "--pid" or "--process" && selector == null && action != CliAction.AppList && next + 1 < tokens.Length)
            {
                bool byPid = tokens[next] == "--pid";
                string selected = args[++next].Trim();
                if (byPid)
                {
                    if (!int.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                    {
                        error = "Use a positive process ID.";
                        return false;
                    }
                    selector = "pid:" + pid.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    if (selected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) selected = selected[..^4];
                    if (string.IsNullOrWhiteSpace(selected) || selected.IndexOfAny(['/', '\\', '*', '?', ':']) >= 0 || selected.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = "Use an exact process name, without a path or wildcard.";
                        return false;
                    }
                    selector = "name:" + selected;
                }
                continue;
            }
            error = "Unexpected or duplicate argument. Use one --pid or --process selector. See help app.";
            return false;
        }
        if (action != CliAction.AppList && selector == null)
        {
            error = "Specify --pid <id> or --process <name>. Use app list to find playback processes.";
            return false;
        }
        command = new() { Action = action, Key = selector, Value = value, JsonOutput = json, RedactOutput = redact };
        return true;
    }
}

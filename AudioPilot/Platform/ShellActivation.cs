namespace AudioPilot.Platform;

internal enum ShellAction { None, DevicePicker, VolumeMixer, Routines }

/// <summary>Bounded shell activation messages reuse the existing per-user single-instance transport.</summary>
internal static class ShellActivation
{
    private const string PayloadPrefix = "AudioPilot.Shell.v1:";

    internal static bool TryParseArguments(string[] arguments, out ShellAction action)
    {
        action = ShellAction.None;
        return arguments.Length == 2 && arguments[0] == "--shell-action" && TryParseName(arguments[1], out action);
    }

    internal static bool TryParsePayload(string payload, out ShellAction action)
    {
        action = ShellAction.None;
        return payload.StartsWith(PayloadPrefix, StringComparison.Ordinal) && TryParseName(payload[PayloadPrefix.Length..], out action);
    }

    internal static string ToPayload(ShellAction action) => PayloadPrefix + GetName(action);
    internal static string GetArguments(ShellAction action) => "--shell-action " + GetName(action);

    private static bool TryParseName(string name, out ShellAction action)
    {
        action = name switch { "picker" => ShellAction.DevicePicker, "mixer" => ShellAction.VolumeMixer, "routines" => ShellAction.Routines, _ => ShellAction.None };
        return action != ShellAction.None;
    }

    internal static string GetName(ShellAction action) => action switch
    {
        ShellAction.DevicePicker => "picker",
        ShellAction.VolumeMixer => "mixer",
        ShellAction.Routines => "routines",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}

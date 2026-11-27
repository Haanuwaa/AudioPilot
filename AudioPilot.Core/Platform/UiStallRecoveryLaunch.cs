using System.Diagnostics;
using System.Globalization;
using AudioPilot.Constants;

namespace AudioPilot.Platform;

internal static class UiStallRecoveryLaunch
{
    private const string Argument = "--ui-stall-recovery";

    internal static bool TryParse(string[] args, out SingleInstanceOwnerIdentity owner)
    {
        owner = default;
        if (args.Length != 3 || args[0] != Argument
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return false;
        owner = new(pid, ticks);
        return true;
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, SingleInstanceOwnerIdentity owner)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add(Argument);
        info.ArgumentList.Add(owner.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(owner.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture));
        // Recovery is neither a candidate update launch nor an update-control attachment.
        info.Environment.Remove(UpdateProtocol.UpdateLifecycleWire.EnvironmentVariable);
        info.Environment.Remove(AppConstants.Updates.AttachmentEnvironment);
        info.Environment.Remove("AUDIOPILOT_DIAGNOSTIC_SESSION");
        return info;
    }
}

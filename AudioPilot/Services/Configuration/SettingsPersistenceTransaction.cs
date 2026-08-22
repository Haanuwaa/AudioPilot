using AudioPilot.Models;

namespace AudioPilot.Services.Configuration;

/// <summary>Commits settings and startup registration together, leaving presentation and settings admission to the caller.</summary>
internal static class SettingsPersistenceTransaction
{
    internal static void Save(Settings previous, Settings candidate, Action<Settings> save,
        Action<Settings, Action> applyStartup, bool forceStartup = false)
    {
        bool startupChanged = forceStartup || previous.RunAtStartup != candidate.RunAtStartup
            || (candidate.RunAtStartup && previous.Miscellaneous.UseScheduledStartup != candidate.Miscellaneous.UseScheduledStartup);
        if (startupChanged) applyStartup(candidate, () => save(candidate));
        else save(candidate);
    }
}

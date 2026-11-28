using System.IO;

namespace AudioPilot.Services.UI;

internal readonly record struct RunningAppIdentity(
    string DisplayName,
    string TargetPath,
    string DetailText,
    int ProcessId,
    long? StartedUtcTicks,
    bool IsPackagedApp,
    string? WindowTitle = null)
{
    public string SecondaryText => string.IsNullOrWhiteSpace(WindowTitle) || string.Equals(WindowTitle, DisplayName, StringComparison.OrdinalIgnoreCase)
        ? DetailText
        : $"{WindowTitle} · {DetailText}";
}

internal static class RunningApplicationDiscovery
{
    private static readonly string SystemRootPrefix = ResolveSystemRootPrefix();

    internal static IReadOnlyList<RunningAppIdentity> Discover(
        IRoutineProcessSnapshotProvider? snapshotProvider = null,
        Func<IEnumerable<int>, IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>>? visibleWindowReader = null,
        Func<int, string?>? fileDescriptionReader = null,
        Func<string?, string?>? aumidDisplayNameReader = null,
        int? currentProcessId = null)
    {
        IRoutineProcessSnapshotProvider provider = snapshotProvider ?? new RoutineProcessSnapshotProvider();
        List<RoutineProcessSnapshot> snapshots = provider.CaptureAll(RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId);
        if (snapshots.Count == 0)
        {
            return [];
        }

        int selfPid = currentProcessId ?? Environment.ProcessId;
        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> windowsByPid =
            (visibleWindowReader ?? AudioDeviceHelper.GetVisibleWindowMetadataByProcessIds)(
                snapshots.Where(static s => s.ProcessId > 0).Select(static s => s.ProcessId));

        Func<int, string?> readFileDescription = fileDescriptionReader ?? AudioDeviceHelper.GetFileDescription;
        Func<string?, string?> readAumidName = aumidDisplayNameReader ?? AudioDeviceHelper.GetAppUserModelDisplayName;

        Dictionary<string, (RunningAppIdentity Identity, bool HasWindow)> byTarget = [with(StringComparer.OrdinalIgnoreCase)];

        foreach (RoutineProcessSnapshot snapshot in snapshots)
        {
            if (snapshot.ProcessId <= 0 || snapshot.ProcessId == selfPid || snapshot.IdentityUnavailable)
            {
                continue;
            }

            string normalizedAumid = RoutineTriggerPathHelper.NormalizeTriggerTarget(snapshot.AppUserModelId);
            bool isPackaged = RoutineTriggerPathHelper.LooksLikePackagedAppId(normalizedAumid);
            string normalizedExe = RoutineTriggerPathHelper.NormalizeExecutablePath(snapshot.ExecutablePath);
            string targetPath = isPackaged ? normalizedAumid : normalizedExe;

            if (!RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(targetPath))
            {
                continue;
            }

            bool hasWindow = windowsByPid.TryGetValue(snapshot.ProcessId, out IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>? windows)
                && windows.Count > 0;
            string? windowTitle = hasWindow ? windows![0].Title : null;

            if (!hasWindow && !isPackaged && IsSystemBackgroundExecutable(normalizedExe))
            {
                continue;
            }

            if (byTarget.TryGetValue(targetPath, out var existing))
            {
                if (existing.HasWindow || !hasWindow)
                {
                    continue;
                }
            }

            string displayName = ResolveDisplayName(snapshot.ProcessId, targetPath, isPackaged, readFileDescription, readAumidName);
            string detail = isPackaged ? normalizedAumid : normalizedExe;
            var identity = new RunningAppIdentity(
                displayName,
                targetPath,
                detail,
                snapshot.ProcessId,
                snapshot.StartTimeUtcTicks,
                isPackaged,
                windowTitle);

            byTarget[targetPath] = (identity, hasWindow);
        }

        return [.. byTarget.Values
            .OrderByDescending(static entry => entry.HasWindow)
            .ThenBy(static entry => entry.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static entry => entry.Identity.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => entry.Identity)];
    }

    private static string ResolveDisplayName(
        int processId,
        string targetPath,
        bool isPackaged,
        Func<int, string?> readFileDescription,
        Func<string?, string?> readAumidName)
    {
        if (isPackaged)
        {
            try
            {
                string? packagedName = readAumidName(targetPath);
                if (!string.IsNullOrWhiteSpace(packagedName))
                {
                    return packagedName.Trim();
                }
            }
            catch
            {
            }
        }
        else
        {
            try
            {
                string? description = readFileDescription(processId);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }
            }
            catch
            {
            }
        }

        string fallback = RoutineTriggerPathHelper.GetTriggerDisplayName(targetPath);
        return string.IsNullOrWhiteSpace(fallback) ? targetPath : fallback;
    }

    private static bool IsSystemBackgroundExecutable(string normalizedExePath)
    {
        if (string.IsNullOrWhiteSpace(SystemRootPrefix))
        {
            return false;
        }

        if (!normalizedExePath.StartsWith(SystemRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(normalizedExePath);
        return !string.Equals(fileName, "explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSystemRootPrefix()
    {
        try
        {
            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windowsDir))
            {
                return string.Empty;
            }

            return windowsDir.EndsWith(Path.DirectorySeparatorChar)
                ? windowsDir
                : windowsDir + Path.DirectorySeparatorChar;
        }
        catch
        {
            return string.Empty;
        }
    }
}

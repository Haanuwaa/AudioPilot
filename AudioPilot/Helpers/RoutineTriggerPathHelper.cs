using System.IO;
using AudioPilot.Logging;

namespace AudioPilot.Helpers
{
    /// <summary>
    /// Normalizes and classifies routine trigger targets so executable paths and packaged app identifiers can be
    /// compared and displayed consistently across routine save, execution, and monitoring flows.
    /// </summary>
    internal static class RoutineTriggerPathHelper
    {
        /// <summary>
        /// Normalizes a routine trigger target into the canonical form used for persistence and comparisons.
        /// </summary>
        internal static string NormalizeTriggerTarget(string? rawValue)
        {
            if (LooksLikePackagedAppId(rawValue))
            {
                return NormalizePackagedAppId(rawValue);
            }

            return NormalizeExecutablePath(rawValue);
        }

        /// <summary>
        /// Produces a normalized executable path by trimming quotes, expanding environment variables, and resolving
        /// rooted paths to their full filesystem representation when possible.
        /// </summary>
        internal static string NormalizeExecutablePath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return string.Empty;
            }

            string trimmed = rawPath.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? candidateUri) && candidateUri.IsFile)
                {
                    trimmed = candidateUri.LocalPath;
                }

                string expanded = Environment.ExpandEnvironmentVariables(trimmed)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                if (!Path.IsPathRooted(expanded))
                {
                    return expanded;
                }

                return Path.GetFullPath(expanded);
            }
            catch (Exception ex)
            {
                Logger logger = Logger.Instance;
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace("RoutineTriggerPathHelper", () => $"normalize-executable-path-fallback | reason={ex.GetType().Name}", nameof(NormalizeExecutablePath));
                }

                return trimmed;
            }
        }

        internal static bool IsExecutablePathMatch(string? leftPath, string? rightPath)
        {
            string normalizedLeft = NormalizeExecutablePath(leftPath);
            string normalizedRight = NormalizeExecutablePath(rightPath);

            return !string.IsNullOrWhiteSpace(normalizedLeft)
                && !string.IsNullOrWhiteSpace(normalizedRight)
                && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsExecutableProcessMatch(string? executablePath, string? triggerPath, string? processName = null)
        {
            string normalizedExecutablePath = NormalizeExecutablePath(executablePath);
            string normalizedTriggerPath = NormalizeExecutablePath(triggerPath);
            if (string.IsNullOrWhiteSpace(normalizedTriggerPath))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(normalizedExecutablePath) &&
                IsExecutablePathMatch(normalizedExecutablePath, normalizedTriggerPath))
            {
                return true;
            }

            return IsKnownLauncherChildProcessMatch(normalizedExecutablePath, normalizedTriggerPath, processName);
        }

        internal static bool LooksLikeExecutablePath(string? rawPath)
        {
            string normalized = NormalizeExecutablePath(rawPath);
            return !string.IsNullOrWhiteSpace(normalized)
                && Path.IsPathRooted(normalized)
                && normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool LooksLikePackagedAppId(string? rawValue)
        {
            string normalized = NormalizePackagedAppId(rawValue);
            return !string.IsNullOrWhiteSpace(normalized)
                && normalized.Contains('!', StringComparison.Ordinal)
                && !Path.IsPathRooted(normalized);
        }

        internal static bool LooksLikeSupportedStartupTarget(string? rawValue)
        {
            return LooksLikeExecutablePath(rawValue) || LooksLikePackagedAppId(rawValue);
        }

        /// <summary>
        /// Returns a human-friendly display name for a trigger target for use in routine UI, diagnostics, and
        /// operation identifiers.
        /// </summary>
        internal static string GetTriggerDisplayName(string? rawValue)
        {
            if (LooksLikeExecutablePath(rawValue))
            {
                string normalizedPath = NormalizeExecutablePath(rawValue);
                string fileName = Path.GetFileNameWithoutExtension(normalizedPath);
                return string.IsNullOrWhiteSpace(fileName) ? "Application" : fileName;
            }

            if (LooksLikePackagedAppId(rawValue))
            {
                string normalized = NormalizePackagedAppId(rawValue);
                int exclamationIndex = normalized.IndexOf('!');
                string packageFamilyName = exclamationIndex > 0 ? normalized[..exclamationIndex] : normalized;
                int underscoreIndex = packageFamilyName.IndexOf('_');
                string cleaned = underscoreIndex > 0 ? packageFamilyName[..underscoreIndex] : packageFamilyName;
                cleaned = cleaned.Replace('.', ' ').Trim();
                return string.IsNullOrWhiteSpace(cleaned) ? "Packaged app" : cleaned;
            }

            return "Application";
        }

        internal static bool IsPackagedAppMatch(string? triggerTarget, string? appUserModelId)
        {
            string normalizedTrigger = NormalizePackagedAppId(triggerTarget);
            string normalizedAumid = NormalizePackagedAppId(appUserModelId);
            return !string.IsNullOrWhiteSpace(normalizedTrigger)
                && !string.IsNullOrWhiteSpace(normalizedAumid)
                && string.Equals(normalizedTrigger, normalizedAumid, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsPackagedAppExecutablePathMatch(string? triggerTarget, string? executablePath)
        {
            string packageFamilyName = GetPackagedAppPackageFamilyName(triggerTarget);
            if (string.IsNullOrWhiteSpace(packageFamilyName) ||
                !TryExtractWindowsAppsPackageFamilyName(executablePath, out string executablePackageFamilyName))
            {
                return false;
            }

            return string.Equals(packageFamilyName, executablePackageFamilyName, StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetPackagedAppPackageFamilyName(string? appUserModelId)
        {
            string normalized = NormalizePackagedAppId(appUserModelId);
            int separatorIndex = normalized.IndexOf('!');
            return separatorIndex > 0
                ? normalized[..separatorIndex]
                : string.Empty;
        }

        internal static bool TryExtractWindowsAppsPackageFamilyName(string? executablePath, out string packageFamilyName)
        {
            packageFamilyName = string.Empty;
            string normalizedPath = NormalizeExecutablePath(executablePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return false;
            }

            string[] segments = normalizedPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int index = 0; index < segments.Length - 1; index++)
            {
                if (!string.Equals(segments[index], "WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return TryConvertPackageFullNameToFamilyName(segments[index + 1], out packageFamilyName);
            }

            return false;
        }

        private static bool TryConvertPackageFullNameToFamilyName(string packageFullName, out string packageFamilyName)
        {
            packageFamilyName = string.Empty;
            if (string.IsNullOrWhiteSpace(packageFullName))
            {
                return false;
            }

            string[] parts = packageFullName.Split('_');
            if (parts.Length < 5 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                string.IsNullOrWhiteSpace(parts[^1]))
            {
                return false;
            }

            packageFamilyName = $"{parts[0]}_{parts[^1]}";
            return true;
        }

        private static string NormalizePackagedAppId(string? rawValue)
        {
            return string.IsNullOrWhiteSpace(rawValue)
                ? string.Empty
                : rawValue.Trim().Trim('"');
        }

        private static bool IsKnownLauncherChildProcessMatch(string executablePath, string triggerPath, string? processName)
        {
            string triggerFileName = Path.GetFileName(triggerPath);
            if (string.IsNullOrWhiteSpace(triggerFileName))
            {
                return false;
            }

            string executableProcessName = Path.GetFileNameWithoutExtension(executablePath);
            string candidateProcessName = !string.IsNullOrWhiteSpace(executableProcessName)
                ? executableProcessName
                : processName ?? string.Empty;

            if (IsSteamWebHelperMatch(executablePath, triggerPath, triggerFileName, candidateProcessName))
            {
                return true;
            }

            return IsSquirrelUpdateAppProcessMatch(executablePath, triggerPath, triggerFileName, candidateProcessName);
        }

        private static bool IsSteamWebHelperMatch(string executablePath, string triggerPath, string triggerFileName, string processName)
        {
            if (!string.Equals(triggerFileName, "steam.exe", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(processName, "steamwebhelper", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? steamDirectory = Path.GetDirectoryName(triggerPath);
            return IsPathUnderDirectory(executablePath, steamDirectory);
        }

        private static bool IsSquirrelUpdateAppProcessMatch(string executablePath, string triggerPath, string triggerFileName, string processName)
        {
            if (!string.Equals(triggerFileName, "Update.exe", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            string? appRootDirectory = Path.GetDirectoryName(triggerPath);
            string appRootName = Path.GetFileName(appRootDirectory) ?? string.Empty;
            string? executableDirectory = Path.GetDirectoryName(executablePath);
            string executableDirectoryName = Path.GetFileName(executableDirectory) ?? string.Empty;
            string? executableParentDirectory = Path.GetDirectoryName(executableDirectory ?? string.Empty);

            return !string.IsNullOrWhiteSpace(appRootName) &&
                !string.IsNullOrWhiteSpace(executableDirectoryName) &&
                executableDirectoryName.StartsWith("app-", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeExecutablePath(executableParentDirectory), NormalizeExecutablePath(appRootDirectory), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(processName, appRootName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPathUnderDirectory(string executablePath, string? parentDirectory)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(parentDirectory))
            {
                return false;
            }

            string normalizedExecutablePath = NormalizeExecutablePath(executablePath);
            string normalizedParentDirectory = NormalizeExecutablePath(parentDirectory);
            if (string.IsNullOrWhiteSpace(normalizedExecutablePath) || string.IsNullOrWhiteSpace(normalizedParentDirectory))
            {
                return false;
            }

            string parentWithSeparator = normalizedParentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return normalizedExecutablePath.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
        }
    }
}

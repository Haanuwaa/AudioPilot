using System.IO;

namespace AudioPilot.Cli
{
    public static class CliPathPolicy
    {
        public static bool TryResolveConfigPath(
            string rawPath,
            string settingsPath,
            bool allowAnyPath,
            out string fullPath,
            out string? error)
        {
            fullPath = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                error = "Path cannot be empty.";
                return false;
            }

            fullPath = Path.GetFullPath(rawPath);
            if (allowAnyPath)
            {
                return true;
            }

            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            string[] roots =
            [
                Path.GetFullPath(Environment.CurrentDirectory),
                Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),
                string.IsNullOrWhiteSpace(settingsDirectory)
                    ? string.Empty
                    : Path.GetFullPath(settingsDirectory),
            ];

            for (int index = 0; index < roots.Length; index++)
            {
                string root = roots[index];
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (IsPathUnderRoot(fullPath, root))
                {
                    return true;
                }
            }

            error = "Path is outside allowed roots. Use current directory or settings directory, or pass --allow-any-path.";
            return false;
        }

        private static bool IsPathUnderRoot(string candidatePath, string rootPath)
        {
            string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            {
                normalizedRoot += Path.DirectorySeparatorChar;
            }

            return candidatePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidatePath, rootPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}

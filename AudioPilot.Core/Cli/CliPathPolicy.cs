namespace AudioPilot.Cli
{
    public static partial class CliPathPolicy
    {
        private static readonly AsyncLocal<string?> CommandWorkingDirectory = new();

        /// <summary>Flows the caller's directory through asynchronous file operations without changing the UI process's current directory.</summary>
        internal static string? WorkingDirectory
        {
            get => CommandWorkingDirectory.Value;
            set => CommandWorkingDirectory.Value = value;
        }

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

            string workingDirectory = WorkingDirectory ?? Environment.CurrentDirectory;
            try
            {
                fullPath = Path.GetFullPath(rawPath, workingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = "Path is not valid.";
                return false;
            }

            if (allowAnyPath)
            {
                return true;
            }

            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            string[] roots =
            [
                Path.GetFullPath(workingDirectory),
                Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),
                string.IsNullOrWhiteSpace(settingsDirectory)
                    ? string.Empty
                    : Path.GetFullPath(settingsDirectory),
            ];

            string candidate = fullPath;
            if (roots.Any(root => root.Length != 0 && IsPathUnderRoot(candidate, root)))
            {
                string? resolved = ResolveExistingAncestors(fullPath);
                if (resolved == null)
                {
                    error = "The file path could not be verified.";
                    return false;
                }

                foreach (string root in roots)
                {
                    if (root.Length != 0 && ResolveExistingAncestors(root) is { } resolvedRoot
                        && IsPathUnderRoot(resolved, resolvedRoot))
                    {
                        // Use the verified path for I/O so a link in the caller's spelling is never followed again.
                        fullPath = resolved;
                        return true;
                    }
                }
            }

            error = "Path is outside allowed roots. Use current directory or settings directory, or pass --allow-any-path.";
            return false;
        }

        private static string? ResolveExistingAncestors(string path)
        {
            // Exports can create a new directory tree. Resolve the nearest existing ancestor and
            // append the absent names after verifying the physical location of that ancestor.
            var missing = new Stack<string>();
            string current = Path.TrimEndingDirectorySeparator(path);
            while (true)
            {
                try { _ = File.GetAttributes(current); break; }
                catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    string? parent = Path.GetDirectoryName(current);
                    if (parent == null) return null;
                    missing.Push(Path.GetFileName(current));
                    current = parent;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return null;
                }
            }

            string? physical = WindowsFilePath.ResolveExisting(current);
            if (physical == null) return null;
            while (missing.TryPop(out string? name)) physical = Path.Combine(physical, name);
            return physical;
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

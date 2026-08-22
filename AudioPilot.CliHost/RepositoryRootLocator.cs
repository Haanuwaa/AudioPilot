namespace AudioPilot.CliHost
{
    internal static class RepositoryRootLocator
    {
        private const string SolutionFileName = "AudioPilot.sln";
        private static readonly string CliGuideRelativePath = Path.Combine("docs", "CLI.md");

        internal static bool TryFind(out string repositoryRoot)
        {
            return TryFind(
                [Environment.CurrentDirectory, AppContext.BaseDirectory],
                out repositoryRoot);
        }

        internal static bool TryFind(IEnumerable<string?> startingDirectories, out string repositoryRoot)
        {
            ArgumentNullException.ThrowIfNull(startingDirectories);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? startingDirectory in startingDirectories)
            {
                if (!TryCreateDirectoryInfo(startingDirectory, out DirectoryInfo? current))
                {
                    continue;
                }

                while (current != null && visited.Add(current.FullName))
                {
                    if (IsRepositoryRoot(current.FullName))
                    {
                        repositoryRoot = current.FullName;
                        return true;
                    }

                    try
                    {
                        current = current.Parent;
                    }
                    catch (Exception ex) when (IsPathAccessException(ex))
                    {
                        break;
                    }
                }
            }

            repositoryRoot = string.Empty;
            return false;
        }

        private static bool TryCreateDirectoryInfo(string? path, out DirectoryInfo? directory)
        {
            directory = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                directory = new DirectoryInfo(Path.GetFullPath(path));
                return true;
            }
            catch (Exception ex) when (IsPathAccessException(ex))
            {
                return false;
            }
        }

        private static bool IsRepositoryRoot(string directory)
        {
            try
            {
                return File.Exists(Path.Combine(directory, SolutionFileName))
                    && File.Exists(Path.Combine(directory, CliGuideRelativePath));
            }
            catch (Exception ex) when (IsPathAccessException(ex))
            {
                return false;
            }
        }

        private static bool IsPathAccessException(Exception exception)
        {
            return exception is ArgumentException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException;
        }
    }
}

using System.IO.Compression;
using AudioPilot.Constants;

namespace AudioPilot.Logging
{
    internal readonly record struct LogFileInventory(
        string LogFilePath,
        bool LogFileExists,
        long LogFileBytes,
        string LogBackupDirectory,
        IReadOnlyList<string> LogBackupFiles,
        bool BackupDirectoryRedirected = false,
        bool RootDirectoryRedirected = false);

    internal sealed class UnavailableLogSourceException() : IOException("The log source is redirected or could not be verified.");

    internal readonly record struct LogArchiveExportResult(
        string ExportPath,
        int ExportedFileCount,
        long ExportedBytes,
        IReadOnlyList<LogArchiveExportEntryResult> Entries);

    internal readonly record struct LogArchiveExportEntryResult(
        string Status,
        string SourceKind,
        string SourcePath,
        string ArchiveEntry,
        long Bytes);

    internal readonly record struct LogArchiveExportCandidate(
        string SourceKind,
        string SourcePath,
        string ArchiveEntry);

    internal static class LogArchiveExportService
    {
        internal static LogFileInventory GetInventory(string? logRootDirectory)
        {
            if (logRootDirectory == null) return new(string.Empty, false, 0, string.Empty, []);
            ArgumentException.ThrowIfNullOrWhiteSpace(logRootDirectory);

            string fullRoot = Path.GetFullPath(logRootDirectory);
            string logFilePath = Path.Combine(fullRoot, AppConstants.Files.LogFileName);
            string logBackupDirectory = Path.Combine(fullRoot, AppConstants.Files.BackupFolderName);
            if (IsRedirected(fullRoot))
                return new(logFilePath, false, 0, logBackupDirectory, [], RootDirectoryRedirected: true);

            bool logFileRedirected = IsRedirected(logFilePath);
            bool logFileExists = logFileRedirected || File.Exists(logFilePath);
            long logFileBytes = logFileExists && !logFileRedirected ? new FileInfo(logFilePath).Length : 0;

            bool backupDirectoryRedirected = IsRedirected(logBackupDirectory);
            List<string> logBackupFiles = !backupDirectoryRedirected && Directory.Exists(logBackupDirectory)
                ? [.. Directory.GetFiles(logBackupDirectory, AppConstants.Files.LogFileName + ".bak*").Where(IsLogBackupFile).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]
                : [];

            return new LogFileInventory(logFilePath, logFileExists, logFileBytes, logBackupDirectory, logBackupFiles, backupDirectoryRedirected);
        }

        internal static FileStream OpenVerifiedSource(string sourcePath, string logRootDirectory, string? physicalRoot)
        {
            string parent = Path.GetDirectoryName(sourcePath)!;
            if (physicalRoot == null || IsRedirected(logRootDirectory) || IsRedirected(parent) || IsRedirected(sourcePath))
                throw new UnavailableLogSourceException();

            var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                string expectedPath = Path.Combine(physicalRoot, Path.GetRelativePath(logRootDirectory, sourcePath));
                string? actualPath = WindowsFilePath.GetFinalPath(source.SafeFileHandle);
                string rootPrefix = Path.EndsInDirectorySeparator(physicalRoot) ? physicalRoot : physicalRoot + Path.DirectorySeparatorChar;
                if (actualPath == null || !actualPath.StartsWith(rootPrefix, StringComparison.Ordinal) ||
                    !string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                    !WindowsFilePath.IsRegularFileWithSingleLink(source.SafeFileHandle))
                    throw new UnavailableLogSourceException();
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        private static bool IsRedirected(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        }

        private static bool IsLogBackupFile(string path)
        {
            ReadOnlySpan<char> name = Path.GetFileName(path);
            string prefix = AppConstants.Files.LogFileName + ".bak";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            ReadOnlySpan<char> suffix = name[prefix.Length..];
            if (suffix.IsEmpty) return true;
            if (suffix[0] == '.') suffix = suffix[1..];
            return !suffix.IsEmpty && suffix.IndexOfAnyExceptInRange('0', '9') < 0;
        }

        internal static LogArchiveExportResult ExportLogs(string? logRootDirectory, string destinationPath, bool redactContent = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string fullDestinationPath = Path.GetFullPath(destinationPath);
            if (!string.Equals(Path.GetExtension(fullDestinationPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("Only .zip log exports are supported.");
            }

            string? destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            LogFileInventory inventory = GetInventory(logRootDirectory);
            List<LogArchiveExportCandidate> candidates = logRootDirectory == null ? [] : BuildExportCandidates(inventory, logRootDirectory);
            if (candidates.Count == 0)
            {
                if (inventory.RootDirectoryRedirected) throw new InvalidOperationException("The log folder is redirected and unavailable for export.");
                if (inventory.BackupDirectoryRedirected) throw new InvalidOperationException("The log backup folder is redirected and unavailable for export.");
                throw new InvalidOperationException("No log files are available to export.");
            }
            string? physicalRoot = WindowsFilePath.ResolveExisting(logRootDirectory!);

            LogArchiveExportResult result = default;
            AtomicFileWriter.Write(fullDestinationPath, temporaryPath =>
            {
                int exportedFileCount = 0;
                long exportedBytes = 0;
                var entries = new List<LogArchiveExportEntryResult>(candidates.Count);
                using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
                RedactedLogExporter? redactor = redactContent ? new() : null;
                if (inventory.BackupDirectoryRedirected)
                    entries.Add(new("unavailable", "backup", inventory.LogBackupDirectory, AppConstants.Files.BackupFolderName + "/", 0));

                for (int index = 0; index < candidates.Count; index++)
                {
                    LogArchiveExportCandidate candidate = candidates[index];
                    string sourcePath = candidate.SourcePath;

                    FileStream source;
                    try
                    {
                        source = OpenVerifiedSource(sourcePath, logRootDirectory!, physicalRoot);
                    }
                    catch (UnavailableLogSourceException)
                    {
                        entries.Add(new("unavailable", candidate.SourceKind, sourcePath, candidate.ArchiveEntry, 0));
                        continue;
                    }
                    catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        entries.Add(new LogArchiveExportEntryResult("missing-at-export", candidate.SourceKind, sourcePath, candidate.ArchiveEntry, 0));
                        continue;
                    }

                    using (source)
                    using (Stream destination = archive.CreateEntry(candidate.ArchiveEntry, CompressionLevel.Optimal).Open())
                    {
                        long entryBytes = redactor?.Copy(source, destination) ?? CopyRawLog(source, destination);
                        exportedFileCount++;
                        exportedBytes += entryBytes;
                        entries.Add(new LogArchiveExportEntryResult("exported", candidate.SourceKind, sourcePath, candidate.ArchiveEntry, entryBytes));
                    }
                }

                if (exportedFileCount == 0)
                {
                    if (entries.Any(static entry => entry.Status == "unavailable")) throw new InvalidOperationException("The log sources are redirected or unavailable for export.");
                    throw new InvalidOperationException("No log files were available to export.");
                }

                result = new LogArchiveExportResult(fullDestinationPath, exportedFileCount, exportedBytes, entries);
            });

            return result;
        }

        private static long CopyRawLog(Stream source, Stream destination)
        {
            source.CopyTo(destination);
            return source.Length;
        }

        private static List<LogArchiveExportCandidate> BuildExportCandidates(LogFileInventory inventory, string logRootDirectory)
        {
            var candidates = new List<LogArchiveExportCandidate>(inventory.LogBackupFiles.Count + 1);
            if (inventory.LogFileExists)
            {
                candidates.Add(new LogArchiveExportCandidate("current", inventory.LogFilePath, GetArchiveEntryName(inventory.LogFilePath, logRootDirectory)));
            }

            for (int index = 0; index < inventory.LogBackupFiles.Count; index++)
            {
                string backupPath = inventory.LogBackupFiles[index];
                candidates.Add(new LogArchiveExportCandidate("backup", backupPath, GetArchiveEntryName(backupPath, logRootDirectory)));
            }

            return candidates;
        }

        private static string GetArchiveEntryName(string sourcePath, string logRootDirectory)
        {
            string fullRoot = Path.GetFullPath(logRootDirectory);
            string relativePath = Path.GetRelativePath(fullRoot, sourcePath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                return Path.GetFileName(sourcePath);
            }

            return relativePath.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}

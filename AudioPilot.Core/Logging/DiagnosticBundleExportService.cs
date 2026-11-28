using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AudioPilot.Constants;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot.Logging
{
    internal readonly record struct DiagnosticBundlePayloads(
        string StatusJson,
        string HistoryJson,
        string MediaStatusJson,
        string ConfigValidationJson,
        IReadOnlyList<DiagnosticBundleCollectionIssue>? CollectionIssues = null);

    internal sealed record DiagnosticBundleCollectionIssue(string ArchiveEntry, string DiagCode, string ErrorType);

    internal readonly record struct DiagnosticBundleExportResult(
        string ExportPath,
        bool IncludeSensitive,
        bool PartialExport,
        int ExportedFileCount,
        long ExportedBytes,
        IReadOnlyList<DiagnosticBundleExportEntryResult> Entries,
        IReadOnlyList<DiagnosticBundleCollectionIssue>? CollectionIssues = null);

    internal readonly record struct DiagnosticBundleExportEntryResult(
        string Status,
        string SourceKind,
        string ArchiveEntry,
        long Bytes,
        string? ErrorType = null,
        string? HResult = null);

    internal static class DiagnosticBundleExportService
    {
        private const string BundleSchemaVersion = "1.0";
        private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

        internal static DiagnosticBundleExportResult ExportBundle(
            string? logRootDirectory,
            string destinationPath,
            DiagnosticBundlePayloads payloads,
            bool includeSensitive)
        {
            if (logRootDirectory != null) ArgumentException.ThrowIfNullOrWhiteSpace(logRootDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            string fullDestinationPath = Path.GetFullPath(destinationPath);
            if (!string.Equals(Path.GetExtension(fullDestinationPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("Only .zip diagnostic bundle exports are supported.");
            }

            string? destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            DiagnosticBundleExportResult result = default;
            DiagnosticBundleCollectionIssue[] collectionIssues = [.. payloads.CollectionIssues ?? []];
            AtomicFileWriter.Write(fullDestinationPath, temporaryPath =>
            {
                var entries = new List<DiagnosticBundleExportEntryResult>();
                using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

                AddTextEntry(archive, "diagnostics/status.json", payloads.StatusJson, "diagnostics", entries);
                AddTextEntry(archive, "diagnostics/history.json", payloads.HistoryJson, "diagnostics", entries);
                AddTextEntry(archive, "diagnostics/media-status.json", payloads.MediaStatusJson, "diagnostics", entries);
                AddTextEntry(archive, "diagnostics/config-validation.json", payloads.ConfigValidationJson, "diagnostics", entries);
                AddTextEntry(archive, "README.txt", BuildReadme(includeSensitive), "metadata", entries);

                AddLogEntries(archive, logRootDirectory, includeSensitive, entries);

                bool partialExport = collectionIssues.Length > 0 || entries.Any(static entry => !string.Equals(entry.Status, "exported", StringComparison.Ordinal));
                AddTextEntry(archive, "manifest.json", BuildManifestWithSelfEntry(fullDestinationPath, includeSensitive, partialExport, entries, collectionIssues), "metadata", entries);

                int exportedFileCount = entries.Count(static entry => string.Equals(entry.Status, "exported", StringComparison.Ordinal));
                long exportedBytes = entries.Where(static entry => string.Equals(entry.Status, "exported", StringComparison.Ordinal)).Sum(static entry => entry.Bytes);
                result = new DiagnosticBundleExportResult(fullDestinationPath, includeSensitive, partialExport, exportedFileCount, exportedBytes, entries, collectionIssues);
            });

            return result;
        }

        internal static void AddLogEntries(
            ZipArchive archive,
            string? logRootDirectory,
            bool includeSensitive,
            List<DiagnosticBundleExportEntryResult> entries)
        {
            if (logRootDirectory == null)
            {
                entries.Add(new DiagnosticBundleExportEntryResult("missing", "logs", "logs/", 0));
                return;
            }
            LogFileInventory inventory;
            try
            {
                inventory = LogArchiveExportService.GetInventory(logRootDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries.Add(new DiagnosticBundleExportEntryResult("unavailable", "logs", "logs/", 0, ex.GetType().Name, $"0x{ex.HResult:X8}"));
                return;
            }
            if (inventory.RootDirectoryRedirected)
            {
                entries.Add(new("unavailable", "logs", "logs/", 0, nameof(UnavailableLogSourceException)));
                return;
            }
            var candidates = new List<(string SourceKind, string SourcePath, string ArchiveEntry)>();
            if (inventory.LogFileExists)
            {
                candidates.Add(("current-log", inventory.LogFilePath, "logs/" + GetRelativeArchiveEntry(inventory.LogFilePath, logRootDirectory)));
            }

            foreach (string backupPath in inventory.LogBackupFiles)
            {
                candidates.Add(("backup-log", backupPath, "logs/" + GetRelativeArchiveEntry(backupPath, logRootDirectory)));
            }
            if (inventory.BackupDirectoryRedirected)
                entries.Add(new("unavailable", "backup-log", $"logs/{AppConstants.Files.BackupFolderName}/", 0, nameof(UnavailableLogSourceException)));

            if (candidates.Count == 0)
            {
                if (!inventory.BackupDirectoryRedirected) entries.Add(new DiagnosticBundleExportEntryResult("missing", "logs", "logs/", 0));
                return;
            }

            string? physicalRoot = WindowsFilePath.ResolveExisting(logRootDirectory);
            RedactedLogExporter? redactor = includeSensitive ? null : new();
            foreach (var (sourceKind, sourcePath, archiveEntry) in candidates.OrderBy(static candidate => candidate.ArchiveEntry, StringComparer.OrdinalIgnoreCase))
                AddLogEntry(archive, sourcePath, logRootDirectory, physicalRoot, archiveEntry, sourceKind, redactor, entries);
        }

        /// <summary>Streams decoded log text to UTF-8. Failures after entry creation abort the atomic export.</summary>
        private static void AddLogEntry(
            ZipArchive archive,
            string sourcePath,
            string logRootDirectory,
            string? physicalRoot,
            string archiveEntryName,
            string sourceKind,
            RedactedLogExporter? redactor,
            List<DiagnosticBundleExportEntryResult> entries)
        {
            FileStream source;
            try
            {
                source = LogArchiveExportService.OpenVerifiedSource(sourcePath, logRootDirectory, physicalRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string status = ex is FileNotFoundException or DirectoryNotFoundException ? "missing-at-export" : "unavailable";
                entries.Add(new DiagnosticBundleExportEntryResult(status, sourceKind, archiveEntryName, 0, ex.GetType().Name, $"0x{ex.HResult:X8}"));
                return;
            }

            using (source)
            using (Stream destination = archive.CreateEntry(archiveEntryName, CompressionLevel.Optimal).Open())
            {
                long totalBytes = redactor?.Copy(source, destination) ?? CopyUnredactedLog(source, destination);
                entries.Add(new DiagnosticBundleExportEntryResult("exported", sourceKind, archiveEntryName, totalBytes));
            }
        }

        private static long CopyUnredactedLog(Stream source, Stream destination)
        {
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            const int characterBufferSize = 4096;
            char[] characters = ArrayPool<char>.Shared.Rent(characterBufferSize);
            byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(characterBufferSize));
            long totalBytes = 0;
            try
            {
                Encoder encoder = Encoding.UTF8.GetEncoder();
                int read;
                do
                {
                    read = reader.Read(characters.AsSpan(0, characterBufferSize));
                    encoder.Convert(characters.AsSpan(0, read), bytes, flush: read == 0, out _, out int written, out _);
                    destination.Write(bytes.AsSpan(0, written));
                    totalBytes += written;
                } while (read != 0);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(characters, clearArray: true);
                ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            }
            return totalBytes;
        }

        private static void AddTextEntry(
            ZipArchive archive,
            string archiveEntryName,
            string content,
            string sourceKind,
            List<DiagnosticBundleExportEntryResult> entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(archiveEntryName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write(content);
            entries.Add(new DiagnosticBundleExportEntryResult("exported", sourceKind, archiveEntryName, Encoding.UTF8.GetByteCount(content)));
        }

        private static string BuildManifest(
            string exportPath,
            bool includeSensitive,
            bool partialExport,
            IReadOnlyList<DiagnosticBundleExportEntryResult> entries,
            IReadOnlyList<DiagnosticBundleCollectionIssue> collectionIssues,
            DateTimeOffset createdUtc)
        {
            return JsonSerializer.Serialize(
                new
                {
                    SchemaVersion = BundleSchemaVersion,
                    App = "AudioPilot",
                    AppVersion = AppBuildInformation.Current.InformationalVersion,
                    Environment = new
                    {
                        OS = AppBuildInformation.Current.OperatingSystem,
                        AppBuildInformation.Current.OSArchitecture,
                        AppBuildInformation.Current.ProcessArchitecture,
                        AppBuildInformation.Current.Framework,
                    },
                    CreatedUtc = createdUtc,
                    RedactionMode = includeSensitive ? "sensitive" : "redacted",
                    IncludeSensitive = includeSensitive,
                    PartialExport = partialExport,
                    CollectionIssues = collectionIssues,
                    ExportFileName = includeSensitive ? Path.GetFileName(exportPath) : "<redacted>.zip",
                    Entries = entries.Select(static entry => new
                    {
                        entry.Status,
                        entry.SourceKind,
                        entry.ArchiveEntry,
                        entry.Bytes,
                        entry.ErrorType,
                        entry.HResult,
                    }).ToArray(),
                },
                ManifestJsonOptions);
        }

        private static string BuildManifestWithSelfEntry(
            string exportPath,
            bool includeSensitive,
            bool partialExport,
            IReadOnlyList<DiagnosticBundleExportEntryResult> entries,
            IReadOnlyList<DiagnosticBundleCollectionIssue> collectionIssues)
        {
            DateTimeOffset createdUtc = DateTimeOffset.UtcNow;
            DiagnosticBundleExportEntryResult manifestEntry = new("exported", "metadata", "manifest.json", 0);
            string content = BuildManifest(exportPath, includeSensitive, partialExport, [.. entries, manifestEntry], collectionIssues, createdUtc);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                long byteCount = Encoding.UTF8.GetByteCount(content);
                if (byteCount == manifestEntry.Bytes)
                {
                    return content;
                }

                manifestEntry = manifestEntry with { Bytes = byteCount };
                content = BuildManifest(exportPath, includeSensitive, partialExport, [.. entries, manifestEntry], collectionIssues, createdUtc);
            }

            return content;
        }

        private static string BuildReadme(bool includeSensitive)
        {
            string privacy = includeSensitive
                ? "This bundle was exported with --include-sensitive and may contain raw paths, names, and log content."
                : "This bundle was exported in the default redacted mode. Names, paths, and user-specific values are anonymized where possible.";

            return "AudioPilot diagnostic bundle" + Environment.NewLine +
                   "============================" + Environment.NewLine +
                   privacy + Environment.NewLine +
                   "A partial export means some diagnostic information could not be collected or optional files were unavailable." + Environment.NewLine +
                   "CollectionIssues in manifest.json identifies failed collection; an ordinary no-player result is not a collection failure." + Environment.NewLine +
                   "For failed log reads, manifest.json records the exception type and HRESULT without exception messages or source paths." + Environment.NewLine;
        }

        private static string GetRelativeArchiveEntry(string sourcePath, string logRootDirectory)
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

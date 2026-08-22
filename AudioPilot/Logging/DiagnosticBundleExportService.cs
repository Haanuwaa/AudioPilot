using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AudioPilot.Logging
{
    internal readonly record struct DiagnosticBundlePayloads(
        string StatusJson,
        string HistoryJson,
        string MediaStatusJson,
        string ConfigValidationJson);

    internal readonly record struct DiagnosticBundleExportResult(
        string ExportPath,
        bool IncludeSensitive,
        bool PartialExport,
        int ExportedFileCount,
        long ExportedBytes,
        IReadOnlyList<DiagnosticBundleExportEntryResult> Entries);

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
            string logRootDirectory,
            string destinationPath,
            DiagnosticBundlePayloads payloads,
            bool includeSensitive)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logRootDirectory);
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

                bool partialExport = entries.Any(static entry => !string.Equals(entry.Status, "exported", StringComparison.Ordinal));
                AddTextEntry(archive, "manifest.json", BuildManifestWithSelfEntry(fullDestinationPath, includeSensitive, partialExport, entries), "metadata", entries);

                int exportedFileCount = entries.Count(static entry => string.Equals(entry.Status, "exported", StringComparison.Ordinal));
                long exportedBytes = entries.Where(static entry => string.Equals(entry.Status, "exported", StringComparison.Ordinal)).Sum(static entry => entry.Bytes);
                result = new DiagnosticBundleExportResult(fullDestinationPath, includeSensitive, partialExport, exportedFileCount, exportedBytes, entries);
            });

            return result;
        }

        internal static void AddLogEntries(
            ZipArchive archive,
            string logRootDirectory,
            bool includeSensitive,
            List<DiagnosticBundleExportEntryResult> entries)
        {
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
            var candidates = new List<(string SourceKind, string SourcePath, string ArchiveEntry)>();
            if (inventory.LogFileExists)
            {
                candidates.Add(("current-log", inventory.LogFilePath, "logs/" + GetRelativeArchiveEntry(inventory.LogFilePath, logRootDirectory)));
            }

            foreach (string backupPath in inventory.LogBackupFiles)
            {
                candidates.Add(("backup-log", backupPath, "logs/" + GetRelativeArchiveEntry(backupPath, logRootDirectory)));
            }

            if (candidates.Count == 0)
            {
                entries.Add(new DiagnosticBundleExportEntryResult("missing", "logs", "logs/", 0));
                return;
            }

            foreach (var (sourceKind, sourcePath, archiveEntry) in candidates.OrderBy(static candidate => candidate.ArchiveEntry, StringComparer.OrdinalIgnoreCase))
            {
                string content;
                try
                {
                    content = ReadSharedText(sourcePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string status = ex is FileNotFoundException or DirectoryNotFoundException ? "missing-at-export" : "unavailable";
                    entries.Add(new DiagnosticBundleExportEntryResult(status, sourceKind, archiveEntry, 0, ex.GetType().Name, $"0x{ex.HResult:X8}"));
                    continue;
                }

                if (!includeSensitive)
                {
                    content = LogContentRedactor.Sanitize(content);
                }

                AddTextEntry(archive, archiveEntry, content, sourceKind, entries);
            }
        }

        private static void AddTextEntry(
            ZipArchive archive,
            string archiveEntryName,
            string content,
            string sourceKind,
            List<DiagnosticBundleExportEntryResult> entries)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            ZipArchiveEntry entry = archive.CreateEntry(archiveEntryName, CompressionLevel.Optimal);
            using Stream entryStream = entry.Open();
            entryStream.Write(bytes);
            entries.Add(new DiagnosticBundleExportEntryResult("exported", sourceKind, archiveEntryName, bytes.Length));
        }

        private static string ReadSharedText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static string BuildManifest(
            string exportPath,
            bool includeSensitive,
            bool partialExport,
            IReadOnlyList<DiagnosticBundleExportEntryResult> entries,
            DateTimeOffset createdUtc)
        {
            return JsonSerializer.Serialize(
                new
                {
                    SchemaVersion = BundleSchemaVersion,
                    App = "AudioPilot",
                    AppVersion = typeof(DiagnosticBundleExportService).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    Environment = new
                    {
                        OS = RuntimeInformation.OSDescription,
                        OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        Framework = RuntimeInformation.FrameworkDescription,
                    },
                    CreatedUtc = createdUtc,
                    RedactionMode = includeSensitive ? "sensitive" : "redacted",
                    IncludeSensitive = includeSensitive,
                    PartialExport = partialExport,
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
            IReadOnlyList<DiagnosticBundleExportEntryResult> entries)
        {
            DateTimeOffset createdUtc = DateTimeOffset.UtcNow;
            DiagnosticBundleExportEntryResult manifestEntry = new("exported", "metadata", "manifest.json", 0);
            string content = BuildManifest(exportPath, includeSensitive, partialExport, [.. entries, manifestEntry], createdUtc);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                long byteCount = Encoding.UTF8.GetByteCount(content);
                if (byteCount == manifestEntry.Bytes)
                {
                    return content;
                }

                manifestEntry = manifestEntry with { Bytes = byteCount };
                content = BuildManifest(exportPath, includeSensitive, partialExport, [.. entries, manifestEntry], createdUtc);
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
                   "A partial export means one or more optional entries were unavailable while the bundle was being created." + Environment.NewLine +
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

using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Services.Configuration
{
    internal static class SettingsTransferService
    {
        internal const string SettingsArchiveEntryName = AppConstants.Files.SettingsFileName;
        private static readonly HashSet<string> AllowedTopLevelPropertyNames = typeof(Settings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() == null)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        private static readonly Version CurrentSchemaVersion = ParseSchemaVersion(Settings.CurrentSchemaVersion, nameof(Settings.CurrentSchemaVersion));

        internal static void ExportSettings(Settings settings, string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (IsZipPath(fullPath))
            {
                AtomicFileWriter.Write(fullPath, tempPath => ExportSettingsArchive(settings, tempPath));
                return;
            }

            if (IsJsonPath(fullPath))
            {
                AtomicFileWriter.WriteAllText(fullPath, SerializeSettings(settings));
                return;
            }

            throw new NotSupportedException("Only .json and .zip settings files are supported.");
        }

        internal static string ReadImportText(string path, Func<string, string>? textFileReader = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string fullPath = Path.GetFullPath(path);
            EnsureImportFileSizeAllowed(fullPath);

            if (IsZipPath(fullPath))
            {
                return ReadSettingsArchive(fullPath);
            }

            if (IsJsonPath(fullPath))
            {
                return (textFileReader ?? File.ReadAllText)(fullPath);
            }

            throw new NotSupportedException("Only .json and .zip settings files are supported.");
        }

        internal static Settings ParseImportedSettings(string importJson, Settings? current, bool replaceImport)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(importJson);

            using JsonDocument document = SettingsJson.ParseDocument(importJson);
            JsonElement importDocument = document.RootElement;
            ValidateImportDocument(importDocument, replaceImport);

            Settings imported;
            if (replaceImport)
            {
                imported = importDocument.Deserialize<Settings>(SettingsJson.ImportOptions) ?? new Settings();
            }
            else
            {
                Settings baseline = current ?? new Settings();
                JsonObject currentToken = JsonSerializer.SerializeToNode(baseline, SettingsJson.Options)!.AsObject();
                MergeSettings(currentToken, importDocument);
                imported = currentToken.Deserialize<Settings>(SettingsJson.ImportOptions) ?? new Settings();
            }

            SettingsValidationService.Normalize(imported);
            return imported;
        }

        internal static string SerializeSettings(Settings settings)
        {
            return JsonSerializer.Serialize(settings, SettingsJson.Options);
        }

        private static void MergeSettings(JsonObject target, JsonElement imported)
        {
            foreach (JsonProperty property in imported.EnumerateObject())
            {
                string name = property.Name;
                JsonElement value = property.Value;
                string targetName = target.ContainsKey(name)
                    ? name
                    : target.FirstOrDefault(candidate => string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
                if (value.ValueKind == JsonValueKind.Null && target.ContainsKey(targetName))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Object && target[targetName] is JsonObject targetObject)
                {
                    MergeSettings(targetObject, value);
                }
                else
                {
                    target[targetName] = value.Deserialize<JsonNode>();
                }
            }
        }

        private static bool IsJsonPath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsZipPath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
        }

        private static void ExportSettingsArchive(Settings settings, string fullPath)
        {
            using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            ZipArchiveEntry entry = archive.CreateEntry(SettingsArchiveEntryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(SerializeSettings(settings));
        }

        private static string ReadSettingsArchive(string fullPath)
        {
            using var archive = ZipFile.OpenRead(fullPath);
            List<ZipArchiveEntry> entries = [.. archive.Entries
                .Where(entry =>
                    !string.IsNullOrEmpty(entry.Name)
                    && string.Equals(entry.FullName, SettingsArchiveEntryName, StringComparison.OrdinalIgnoreCase))];

            if (entries.Count == 0)
            {
                throw new InvalidDataException($"The selected archive does not contain {SettingsArchiveEntryName} at the archive root.");
            }

            if (entries.Count > 1)
            {
                throw new InvalidDataException($"The selected archive contains multiple {SettingsArchiveEntryName} entries.");
            }

            if (entries[0].Length > AppConstants.Limits.MaxSettingsImportArchiveEntryBytes)
            {
                throw new InvalidDataException($"The settings.json entry exceeds the {AppConstants.Limits.MaxSettingsImportArchiveEntryBytes / 1024} KB import limit.");
            }

            using var reader = new StreamReader(entries[0].Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static void EnsureImportFileSizeAllowed(string fullPath)
        {
            long fileBytes = new FileInfo(fullPath).Length;
            if (fileBytes > AppConstants.Limits.MaxSettingsImportFileBytes)
            {
                throw new InvalidDataException($"The selected file exceeds the {AppConstants.Limits.MaxSettingsImportFileBytes / 1024} KB import limit.");
            }
        }

        private static void ValidateImportDocument(JsonElement document, bool replaceImport)
        {
            if (document.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Imported settings must be a JSON object.");
            }

            ValidateTopLevelProperties(document);
            ValidateSchema(document, replaceImport);
        }

        private static void ValidateTopLevelProperties(JsonElement document)
        {
            foreach (JsonProperty property in document.EnumerateObject())
            {
                if (!AllowedTopLevelPropertyNames.Contains(property.Name))
                {
                    throw new InvalidDataException($"Imported settings contain an unsupported top-level property: {property.Name}.");
                }
            }
        }

        private static void ValidateSchema(JsonElement document, bool replaceImport)
        {
            if (!SettingsJson.TryGetProperty(document, nameof(Settings.SchemaVersion), out JsonElement schemaToken))
            {
                if (replaceImport)
                {
                    throw new InvalidDataException("Imported settings must include a valid SchemaVersion.");
                }

                return;
            }

            if (schemaToken.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Imported settings SchemaVersion must be a string.");
            }

            string? schemaValue = schemaToken.GetString();
            if (string.IsNullOrWhiteSpace(schemaValue))
            {
                throw new InvalidDataException("Imported settings SchemaVersion cannot be empty.");
            }

            Version importedSchemaVersion = ParseSchemaVersion(schemaValue, nameof(Settings.SchemaVersion));
            if (importedSchemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Imported settings use unsupported schema version {schemaValue}. Current supported version is {Settings.CurrentSchemaVersion}.");
            }

            if (importedSchemaVersion < CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Imported settings use unsupported schema version {schemaValue}. Expected {Settings.CurrentSchemaVersion}.");
            }
        }

        private static Version ParseSchemaVersion(string value, string fieldName)
        {
            if (!Version.TryParse(value, out Version? parsed))
            {
                throw new InvalidDataException($"Imported settings {fieldName} must be a valid version string.");
            }

            return parsed;
        }
    }
}

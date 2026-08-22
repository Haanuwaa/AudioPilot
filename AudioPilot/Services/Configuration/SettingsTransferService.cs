using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AudioPilot.Constants;
using AudioPilot.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Services.Configuration
{
    internal static class SettingsTransferService
    {
        internal const string SettingsArchiveEntryName = AppConstants.Files.SettingsFileName;
        private static readonly HashSet<string> AllowedTopLevelPropertyNames = typeof(Settings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
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

            JObject importDocument = ParseAndValidateImportDocument(importJson, replaceImport);

            Settings imported;
            if (replaceImport)
            {
                imported = JsonConvert.DeserializeObject<Settings>(
                    importDocument.ToString(Formatting.None),
                    new JsonSerializerSettings
                    {
                        ObjectCreationHandling = ObjectCreationHandling.Replace,
                    }) ?? new Settings();
            }
            else
            {
                Settings baseline = current ?? new Settings();
                var currentToken = JObject.Parse(JsonConvert.SerializeObject(baseline, Formatting.None));
                currentToken.Merge(importDocument, new JsonMergeSettings
                {
                    MergeArrayHandling = MergeArrayHandling.Replace,
                    MergeNullValueHandling = MergeNullValueHandling.Ignore,
                });

                imported = currentToken.ToObject<Settings>() ?? new Settings();
            }

            SettingsValidationService.Normalize(imported);
            return imported;
        }

        internal static string SerializeSettings(Settings settings)
        {
            return JsonConvert.SerializeObject(settings, Formatting.Indented);
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

        private static JObject ParseAndValidateImportDocument(string importJson, bool replaceImport)
        {
            JToken token = JToken.Parse(importJson);
            if (token is not JObject document)
            {
                throw new InvalidDataException("Imported settings must be a JSON object.");
            }

            ValidateTopLevelProperties(document);
            ValidateSchema(document, replaceImport);
            return document;
        }

        private static void ValidateTopLevelProperties(JObject document)
        {
            foreach (JProperty property in document.Properties())
            {
                if (!AllowedTopLevelPropertyNames.Contains(property.Name))
                {
                    throw new InvalidDataException($"Imported settings contain an unsupported top-level property: {property.Name}.");
                }
            }
        }

        private static void ValidateSchema(JObject document, bool replaceImport)
        {
            if (!document.TryGetValue(nameof(Settings.SchemaVersion), StringComparison.OrdinalIgnoreCase, out JToken? schemaToken))
            {
                if (replaceImport)
                {
                    throw new InvalidDataException("Imported settings must include a valid SchemaVersion.");
                }

                return;
            }

            if (schemaToken.Type != JTokenType.String)
            {
                throw new InvalidDataException("Imported settings SchemaVersion must be a string.");
            }

            string? schemaValue = schemaToken.Value<string>();
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

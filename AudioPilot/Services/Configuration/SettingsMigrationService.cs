using System.IO;
using AudioPilot.Models;

namespace AudioPilot.Services.Configuration
{
    public readonly record struct SettingsMigrationResult(
        string OriginalSchemaVersion,
        string FinalSchemaVersion,
        IReadOnlyList<string> AppliedMigrations,
        bool IsSourceSchemaNewerThanCurrent)
    {
        public bool HasChanges => AppliedMigrations.Count > 0;
    }

    public static class SettingsMigrationService
    {
        public static SettingsMigrationResult MigrateToCurrent(Settings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            string originalVersion = settings.SchemaVersion ?? string.Empty;

            Version parsedOriginalVersion = ParseSchemaVersion(settings.SchemaVersion, nameof(Settings.SchemaVersion));
            Version currentSchemaVersion = ParseSchemaVersion(Settings.CurrentSchemaVersion, nameof(Settings.CurrentSchemaVersion));
            bool sourceSchemaNewerThanCurrent = parsedOriginalVersion > currentSchemaVersion;

            if (parsedOriginalVersion < currentSchemaVersion)
            {
                throw new InvalidDataException($"Settings schema version {settings.SchemaVersion} is unsupported. Expected {Settings.CurrentSchemaVersion}.");
            }

            return new SettingsMigrationResult(
                OriginalSchemaVersion: originalVersion,
                FinalSchemaVersion: settings.SchemaVersion ?? string.Empty,
                AppliedMigrations: [],
                IsSourceSchemaNewerThanCurrent: sourceSchemaNewerThanCurrent);
        }

        internal static void EnsureWritableVersion(string? schemaVersion)
        {
            Version sourceVersion = ParseSchemaVersion(schemaVersion, nameof(Settings.SchemaVersion));
            Version currentVersion = ParseSchemaVersion(Settings.CurrentSchemaVersion, nameof(Settings.CurrentSchemaVersion));
            if (sourceVersion > currentVersion)
            {
                throw new InvalidDataException($"Settings schema {schemaVersion} belongs to a newer AudioPilot version. Update AudioPilot before saving these settings. This version supports schema {Settings.CurrentSchemaVersion}.");
            }
        }

        private static Version ParseSchemaVersion(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value, out Version? parsed))
            {
                throw new InvalidDataException($"Settings {fieldName} must be a valid version string.");
            }

            return parsed;
        }
    }
}

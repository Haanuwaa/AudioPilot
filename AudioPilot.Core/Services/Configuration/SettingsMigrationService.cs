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
        private static readonly Version CurrentVersion = Version.Parse(Settings.CurrentSchemaVersion);
        private static readonly Version InitialVersion = new(1, 0, 0);

        internal static bool SupportsVersion(Version version) => version == InitialVersion || version == CurrentVersion;

        internal static bool IsNewerVersion(string? schemaVersion) =>
            Version.TryParse(schemaVersion, out Version? version) && version > CurrentVersion;

        public static SettingsMigrationResult MigrateToCurrent(Settings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            string originalVersion = settings.SchemaVersion ?? string.Empty;

            Version parsedOriginalVersion = ParseSchemaVersion(settings.SchemaVersion, nameof(Settings.SchemaVersion));
            bool sourceSchemaNewerThanCurrent = parsedOriginalVersion > CurrentVersion;

            if (parsedOriginalVersion < CurrentVersion)
            {
                if (!SupportsVersion(parsedOriginalVersion))
                {
                    throw SettingsFailure.WithPresentation(
                        new InvalidDataException($"Settings schema version {settings.SchemaVersion} is unsupported. Expected {Settings.CurrentSchemaVersion}."),
                        "Validation_SettingsSchemaUnsupported", settings.SchemaVersion, Settings.CurrentSchemaVersion);
                }

                // Whole-number settings deserialize directly into the widened numeric fields.
                settings.SchemaVersion = Settings.CurrentSchemaVersion;
                return new(originalVersion, settings.SchemaVersion, ["fractional-audio-controls"], false);
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
            if (sourceVersion > CurrentVersion)
            {
                throw SettingsFailure.WithPresentation(
                    new InvalidDataException($"Settings schema {schemaVersion} belongs to a newer AudioPilot version. Update AudioPilot before saving these settings. This version supports schema {Settings.CurrentSchemaVersion}."),
                    "Validation_SettingsSchemaNewer", schemaVersion, Settings.CurrentSchemaVersion);
            }
        }

        private static Version ParseSchemaVersion(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value, out Version? parsed))
            {
                throw SettingsFailure.WithPresentation(new InvalidDataException($"Settings {fieldName} must be a valid version string."),
                    "Validation_SettingsVersionInvalid", fieldName);
            }

            return parsed;
        }
    }
}

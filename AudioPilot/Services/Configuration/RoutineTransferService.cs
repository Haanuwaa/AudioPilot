using System.IO;
using System.Text.Json;
using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Services.Configuration
{
    internal static class RoutineTransferService
    {
        private static readonly HashSet<string> PersistedRoutinePropertyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(AudioRoutine.Id),
            nameof(AudioRoutine.Name),
            nameof(AudioRoutine.Enabled),
            nameof(AudioRoutine.OutputDeviceId),
            nameof(AudioRoutine.OutputDeviceName),
            nameof(AudioRoutine.InputDeviceId),
            nameof(AudioRoutine.InputDeviceName),
            nameof(AudioRoutine.MasterVolumePercent),
            nameof(AudioRoutine.MicVolumePercent),
            nameof(AudioRoutine.Hotkey),
            nameof(AudioRoutine.TriggerKind),
            nameof(AudioRoutine.TriggerAppPath),
            nameof(AudioRoutine.SwitchOutputPerApp),
            nameof(AudioRoutine.ApplicationTriggerMode),
            nameof(AudioRoutine.ApplicationTriggerTitlePattern),
            nameof(AudioRoutine.ApplicationTriggerTitleMatchMode),
            nameof(AudioRoutine.ShowInTrayMenu),
            nameof(AudioRoutine.RestorePreviousAudioOnDeactivate),
            nameof(AudioRoutine.EnforceTargetsOnDeviceChange),
            nameof(AudioRoutine.ScheduleTime),
            nameof(AudioRoutine.ScheduleDays),
            nameof(AudioRoutine.ScheduleTimeZoneId),
            nameof(AudioRoutine.NotifyBeforeScheduledRun),
            nameof(AudioRoutine.TriggerNetworkName),
            nameof(AudioRoutine.NetworkTriggerDirection),
        };

        private static readonly Version CurrentSchemaVersion = ParseSchemaVersion(Settings.CurrentSchemaVersion, nameof(Settings.CurrentSchemaVersion));

        internal static string ReadImportText(string path, Func<string, string>? textFileReader = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string fullPath = Path.GetFullPath(path);
            EnsureImportFileSizeAllowed(fullPath);

            if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("Only .json routine files are supported.");
            }

            return (textFileReader ?? File.ReadAllText)(fullPath);
        }

        internal static AudioRoutine ParseSingleRoutine(string importJson)
        {
            List<AudioRoutine> routines = ParseRoutineCollection(importJson);
            if (routines.Count != 1)
            {
                throw new InvalidDataException("Routine payload must contain exactly one routine.");
            }

            return routines[0];
        }

        internal static List<AudioRoutine> ParseRoutineCollection(string importJson)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(importJson);

            using JsonDocument document = SettingsJson.ParseDocument(importJson);
            JsonElement token = document.RootElement;
            return token.ValueKind switch
            {
                JsonValueKind.Object => ParseRoutineObject(token),
                JsonValueKind.Array => ParseRoutineArray(token),
                _ => throw new InvalidDataException("Imported routines must be a JSON object or array."),
            };
        }

        private static List<AudioRoutine> ParseRoutineObject(JsonElement document)
        {
            if (SettingsJson.TryGetProperty(document, "Routines", out JsonElement routinesToken))
            {
                ValidateSchema(document);
                if (routinesToken.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Imported routines document must contain a Routines array.");
                }

                return ParseRoutineArray(routinesToken);
            }

            if (SettingsJson.TryGetProperty(document, "Routine", out JsonElement routineToken))
            {
                ValidateSchema(document);
                return [ParseRoutineToken(routineToken)];
            }

            return [ParseRoutineToken(document)];
        }

        private static List<AudioRoutine> ParseRoutineArray(JsonElement routinesArray)
        {
            var routines = new List<AudioRoutine>(routinesArray.GetArrayLength());
            foreach (JsonElement token in routinesArray.EnumerateArray())
            {
                routines.Add(ParseRoutineToken(token));
            }

            return routines;
        }

        private static AudioRoutine ParseRoutineToken(JsonElement token)
        {
            if (token.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Each imported routine must be a JSON object.");
            }

            ValidateRoutineProperties(token);

            AudioRoutine? routine = token.Deserialize<AudioRoutine>(SettingsJson.ImportOptions);

            return routine ?? throw new InvalidDataException("Failed to parse routine payload.");
        }

        private static void ValidateRoutineProperties(JsonElement document)
        {
            foreach (JsonProperty property in document.EnumerateObject())
            {
                if (!PersistedRoutinePropertyNames.Contains(property.Name))
                {
                    throw new InvalidDataException($"Imported routine contains an unsupported property: {property.Name}.");
                }
            }
        }

        private static void EnsureImportFileSizeAllowed(string fullPath)
        {
            long fileBytes = new FileInfo(fullPath).Length;
            if (fileBytes > AppConstants.Limits.MaxSettingsImportFileBytes)
            {
                throw new InvalidDataException($"The selected file exceeds the {AppConstants.Limits.MaxSettingsImportFileBytes / 1024} KB import limit.");
            }
        }

        private static void ValidateSchema(JsonElement document)
        {
            if (!SettingsJson.TryGetProperty(document, nameof(Settings.SchemaVersion), out JsonElement schemaToken))
            {
                return;
            }

            if (schemaToken.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Imported routines SchemaVersion must be a string.");
            }

            string? schemaValue = schemaToken.GetString();
            if (string.IsNullOrWhiteSpace(schemaValue))
            {
                throw new InvalidDataException("Imported routines SchemaVersion cannot be empty.");
            }

            Version importedSchemaVersion = ParseSchemaVersion(schemaValue, nameof(Settings.SchemaVersion));
            if (importedSchemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Imported routines use unsupported schema version {schemaValue}. Current supported version is {Settings.CurrentSchemaVersion}.");
            }
        }

        private static Version ParseSchemaVersion(string value, string fieldName)
        {
            if (!Version.TryParse(value, out Version? parsed))
            {
                throw new InvalidDataException($"Imported routines {fieldName} must be a valid version string.");
            }

            return parsed;
        }
    }
}

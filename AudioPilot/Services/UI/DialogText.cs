using System.IO;
using System.Text;

namespace AudioPilot.Services.UI
{
    public static class DialogText
    {
        public static class Captions
        {
            public const string Error = "Error";
            public const string Warning = "Warning";
            public const string Information = "Information";
            public const string Success = "Success";
            public const string Confirm = "Confirm";

            public const string InvalidSettings = "Invalid Settings";
            public const string SettingsWarnings = "Settings Warnings";
            public const string InvalidOverlayDuration = "Invalid Overlay Duration";
            public const string ExternalSettingsChangeDetected = "External Settings Change Detected";
            public const string UnsavedChanges = "Unsaved Changes";
            public const string NothingToReset = "Nothing to Reset";
            public const string ResetToDefaults = "Reset to Defaults";
            public const string ResetPerAppAudio = "Reset Per-App Audio";
            public const string ImportSettings = "Import Settings";
            public const string ExportSettings = "Export Settings";
            public const string OutputDevicesMissing = "Output Devices Missing";
            public const string InputDevicesMissing = "Input Devices Missing";
            public const string DeviceSwitchFailed = "Device Switch Failed";
            public const string StartupError = "Startup Error";
            public const string FatalError = "Fatal Error";
        }

        public static class Messages
        {
            public const string InvalidOverlayDuration = "Please enter a valid overlay duration in seconds (0.5 to 10).";

            public static string BuildInvalidSettingsBeforeApplying(IEnumerable<string> issues)
            {
                return BuildBulletedMessage("Please fix these settings before applying:", issues);
            }

            public static string BuildInvalidSettingsBeforeSaving(IEnumerable<string> issues)
            {
                return BuildBulletedMessage("Please fix these settings before saving:", issues);
            }

            public static string BuildSettingsAppliedWithWarnings(IEnumerable<string> warnings)
            {
                return BuildBulletedMessage("Settings applied with warnings:", warnings);
            }

            public static string BuildSettingsSavedWithWarnings(IEnumerable<string> warnings, IEnumerable<string>? notes = null)
            {
                string message = BuildBulletedMessage("Settings saved with warnings:", warnings);

                string notesBlock = BuildBulletList(notes ?? []);
                if (string.IsNullOrWhiteSpace(notesBlock))
                {
                    return message;
                }

                return message + "\n\n" + notesBlock;
            }

            public static string BuildSettingsSavedSuccessfully(IEnumerable<string>? notes = null)
            {
                string notesBlock = BuildBulletList(notes ?? []);
                if (string.IsNullOrWhiteSpace(notesBlock))
                {
                    return "Settings saved successfully.";
                }

                return "Settings saved successfully.\n\n" + notesBlock;
            }

            public static string BuildImportSettingsReplaceConfirmation(string path, bool discardUnsavedEdits)
            {
                string fileName = Path.GetFileName(path);
                string message = $"Importing '{fileName}' will replace your current saved settings.";

                if (discardUnsavedEdits)
                {
                    message += "\n\nYou also have unsaved local edits. Continuing will discard those edits.";
                }

                return message + "\n\nChoose Yes to continue, or No to cancel.";
            }

            public static string BuildSettingsImportedSuccessfully(string path)
            {
                return $"Imported settings from {path}.";
            }

            public static string BuildSettingsExportedSuccessfully(string path)
            {
                return $"Exported settings to {path}.";
            }

            public static string BuildSettingsImportFailed(string path)
            {
                return $"Failed to import settings from {path}.";
            }

            public static string BuildSettingsExportFailed(string path)
            {
                return $"Failed to export settings to {path}.";
            }

            public static string BuildRoutineConflictSaveConfirmation(IEnumerable<string> conflicts)
            {
                return BuildBulletedMessage(
                           "These enabled routines can conflict at runtime:",
                           conflicts) +
                       "\n\nChoose Yes to save anyway, or No to go back and adjust them.";
            }

            private static string BuildBulletedMessage(string heading, IEnumerable<string> lines)
            {
                string bulletList = BuildBulletList(lines);
                if (string.IsNullOrWhiteSpace(bulletList))
                {
                    return heading;
                }

                return heading + "\n\n" + bulletList;
            }

            private static string BuildBulletList(IEnumerable<string> lines)
            {
                var builder = new StringBuilder();
                bool hasAny = false;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (hasAny)
                    {
                        builder.Append('\n');
                    }

                    builder.Append("- ");
                    builder.Append(line);
                    hasAny = true;
                }

                return hasAny ? builder.ToString() : string.Empty;
            }
        }
    }
}

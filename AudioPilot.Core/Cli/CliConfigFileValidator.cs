using System.Text.Json;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.Cli;

/// <summary>Validates configuration files and previews imports without writing settings or querying hardware.</summary>
internal static class CliConfigFileValidator
{
    internal static (bool Success, string Output) PreviewImport(string path, string settingsPath, Func<Settings> readSavedSettings,
        bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CliPathPolicy.TryResolveConfigPath(path, settingsPath, allowAnyPath, out string fullPath, out string? pathError))
            return Failure("config-import-path-blocked", pathError ?? "Import path is not allowed.");

        try
        {
            string json = SettingsTransferService.ReadImportText(fullPath);
            cancellationToken.ThrowIfCancellationRequested();
            Settings current = readSavedSettings();
            Settings candidate = AppSettingsTransferCoordinator.PrepareImport(json, current, replaceImport);
            var summary = AppSettingsTransferCoordinator.DescribeImport(current, candidate, out string[] sections, includeDetails: !redactOutput);
            cancellationToken.ThrowIfCancellationRequested();
            string mode = replaceImport ? "replace" : "merge";
            string displayPath = CliOutputFormatter.FormatPath(fullPath, redactOutput);
            string? details = redactOutput ? null : summary(LocalizationService.English);
            if (jsonOutput)
                return (true, CliOutputFormatter.SerializeCliJson(new
                {
                    Success = true,
                    DryRun = true,
                    Mode = mode,
                    Path = displayPath,
                    ChangedSections = sections,
                    Summary = details,
                    DiagCode = "config-import-preview",
                }));

            details ??= sections.Length == 0 ? "No saved settings will change." : $"Changed sections: {string.Join(", ", sections)}.";
            return (true, $"[diag-code:config-import-preview] {mode} preview for {displayPath}. No settings were saved.\n{details}");
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException or ArgumentException)
        {
            return Failure("config-import-invalid-data", redactOutput ? "The file is not a valid settings import." : ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure("config-import-file-unavailable", "The settings file could not be read.");
        }

        (bool Success, string Output) Failure(string code, string error) => (false,
            CliOutputFormatter.FormatCommandError(new { DryRun = true }, 3, code, error, jsonOutput));
    }

    internal static Task<CliExecutionResult> ValidateAsync(string path, string settingsPath, bool allowAnyPath,
        bool jsonOutput, bool redactOutput, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CliPathPolicy.TryResolveConfigPath(path, settingsPath, allowAnyPath, out string fullPath, out _))
            return Failure(5, "config-validation-path-blocked", "The file path is not allowed. Use an allowed directory or --allow-any-path.");
        try
        {
            string text = SettingsTransferService.ReadImportText(fullPath);
            cancellationToken.ThrowIfCancellationRequested();
            var settings = SettingsTransferService.ParseImportedSettings(text, current: null, replaceImport: true);
            var diagnostics = SettingsValidationService.EvaluateDiagnostics(settings, null, null, includeDeviceAvailability: false);
            List<string> warnings = [.. diagnostics.Warnings.Select(static warning => $"[{warning.Code}] {warning.Message} {warning.SuggestedAction}")];
            AppendDuplicateHotkeyWarnings(settings, warnings);
            return new CliExecutionResult(warnings.Count == 0 ? 0 : 5,
                CliOutputFormatter.FormatConfigFileValidation(fullPath, warnings, jsonOutput, redactOutput));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException or ArgumentException)
        {
            return Failure(5, "config-validation-file-invalid", redactOutput ? "The file is not a supported replacement settings import." : ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(3, "config-validation-file-unavailable", "The settings file could not be read.");
        }

        CliExecutionResult Failure(int exitCode, string code, string message) => new(exitCode,
            CliOutputFormatter.FormatCommandError(new { IsValid = false, ReadOnly = true, Scope = "offline" }, exitCode, code, message, jsonOutput));
    }, cancellationToken);

    internal static void AppendDuplicateHotkeyWarnings(Settings settings, List<string> warnings)
    {
        foreach (string duplicate in SettingsCommitValidator.BuildDuplicateHotkeyConflicts(settings))
        {
            warnings.Add($"[duplicate-hotkey] Duplicate hotkey: {duplicate} Choose distinct hotkeys for each action.");
        }
    }
}

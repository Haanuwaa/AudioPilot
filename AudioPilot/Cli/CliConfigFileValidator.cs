using System.IO;
using System.Text.Json;

namespace AudioPilot.Cli;

/// <summary>Validates replacement imports without loading saved settings, writing files, or querying hardware.</summary>
internal static class CliConfigFileValidator
{
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
            string[] warnings = [.. diagnostics.Warnings.Select(static warning => $"[{warning.Code}] {warning.Message} {warning.SuggestedAction}")];
            return new CliExecutionResult(warnings.Length == 0 ? 0 : 5,
                CliOutputFormatter.FormatConfigFileValidation(fullPath, warnings, jsonOutput, redactOutput));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException or ArgumentException)
        {
            return Failure(5, "config-validation-file-invalid", "The file is not a supported replacement settings import.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(3, "config-validation-file-unavailable", "The settings file could not be read.");
        }

        CliExecutionResult Failure(int exitCode, string code, string message) => new(exitCode,
            jsonOutput ? CliOutputFormatter.SerializeCliJson(new { IsValid = false, ReadOnly = true, Scope = "offline", DiagCode = code, Error = message })
                : $"[diag-code:{code}] {message}");
    }, cancellationToken);
}

using System.IO;
using AudioPilot.Models;
using Newtonsoft.Json;

namespace AudioPilot.Cli
{
    internal static class CliRoutineTransferHelper
    {
        internal static bool TryLoadRoutineDraft(
            string path,
            string settingsPath,
            bool allowAnyPath,
            out string? fullPath,
            out AudioRoutine? draft,
            out string? errorCode,
            out string? errorMessage)
        {
            draft = null;
            if (!TryResolveRoutinePath(path, settingsPath, allowAnyPath, out fullPath, out errorCode, out errorMessage))
            {
                return false;
            }

            try
            {
                string importJson = RoutineTransferService.ReadImportText(fullPath!);
                draft = RoutineTransferService.ParseSingleRoutine(importJson);
                errorCode = null;
                errorMessage = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or JsonException)
            {
                errorCode = "routine-import-invalid";
                errorMessage = ex.Message;
                return false;
            }
        }

        internal static bool TryLoadRoutineCollection(
            string path,
            string settingsPath,
            bool allowAnyPath,
            out string? fullPath,
            out List<AudioRoutine>? routines,
            out string? errorCode,
            out string? errorMessage)
        {
            routines = null;
            if (!TryResolveRoutinePath(path, settingsPath, allowAnyPath, out fullPath, out errorCode, out errorMessage))
            {
                return false;
            }

            try
            {
                string importJson = RoutineTransferService.ReadImportText(fullPath!);
                routines = RoutineTransferService.ParseRoutineCollection(importJson);
                errorCode = null;
                errorMessage = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or JsonException)
            {
                errorCode = "routine-import-invalid";
                errorMessage = ex.Message;
                return false;
            }
        }

        internal static bool TryResolveRoutinePath(
            string path,
            string settingsPath,
            bool allowAnyPath,
            out string? fullPath,
            out string? errorCode,
            out string? errorMessage)
        {
            if (!CliPathPolicy.TryResolveConfigPath(path, settingsPath, allowAnyPath, out string resolvedPath, out string? pathError))
            {
                fullPath = null;
                errorCode = "routine-path-blocked";
                errorMessage = pathError ?? "Routine path is not allowed.";
                return false;
            }

            if (!File.Exists(resolvedPath))
            {
                fullPath = resolvedPath;
                errorCode = "routine-file-missing";
                errorMessage = $"Routine file not found: {resolvedPath}";
                return false;
            }

            fullPath = resolvedPath;
            errorCode = null;
            errorMessage = null;
            return true;
        }
    }
}

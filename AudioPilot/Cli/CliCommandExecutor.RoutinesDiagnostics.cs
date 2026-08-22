namespace AudioPilot.Cli
{
    public static partial class CliCommandExecutor
    {
        private static async Task<CliExecutionResult> ExecuteRoutinesAndDiagnosticsAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            return command.Action switch
            {
                CliAction.RoutineList => new CliExecutionResult(0, runtime.GetRoutineList(command.JsonOutput, command.RedactOutput)),
                CliAction.RoutineRun => await ExecuteRoutineRunAsync(command, runtime),
                CliAction.RoutineEnable => await ExecuteRoutineToggleAsync(command, runtime, enabled: true),
                CliAction.RoutineDisable => await ExecuteRoutineToggleAsync(command, runtime, enabled: false),
                CliAction.RoutineCreate => await ExecuteRoutineCreateAsync(command, runtime),
                CliAction.RoutineUpdate => await ExecuteRoutineUpdateAsync(command, runtime),
                CliAction.RoutineDelete => await ExecuteRoutineDeleteAsync(command, runtime),
                CliAction.RoutineImport => await ExecuteRoutineImportAsync(command, runtime),
                CliAction.RoutineExport => await ExecuteRoutineExportAsync(command, runtime),
                CliAction.DiagnosticsStatus => new CliExecutionResult(0, runtime.GetDiagnosticsStatus(command.JsonOutput, command.ShowPaths, command.RedactOutput)),
                CliAction.DiagnosticsHistory => new CliExecutionResult(0, runtime.GetDiagnosticsHistory(command.JsonOutput, command.Limit, command.Key, command.RedactOutput)),
                CliAction.DiagnosticsHistoryDetail => ExecuteDiagnosticsHistoryDetail(command, runtime),
                CliAction.DiagnosticsExportLogs => ExecuteDiagnosticsExport(command, runtime),
                CliAction.DiagnosticsExportBundle => await ExecuteDiagnosticsBundleExportAsync(command, runtime),
                CliAction.DiagnosticsResetPerAppAudio => ExecuteDiagnosticsReset(command, runtime),
                _ => BuildErrorResult(2, "unsupported-routine-diagnostics-command", "Unsupported routine or diagnostics command.", command.JsonOutput),
            };
        }

        private static async Task<CliExecutionResult> ExecuteRoutineRunAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-routine-selector", "Missing routine selector.", command.JsonOutput);
            }

            return await runtime.RunRoutineAsync(command.Key, command.JsonOutput, command.RedactOutput);
        }

        private static async Task<CliExecutionResult> ExecuteRoutineToggleAsync(CliCommand command, ICliCommandRuntime runtime, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-routine-selector", "Missing routine selector.", command.JsonOutput);
            }

            return await runtime.SetRoutineEnabledAsync(command.Key, enabled, command.JsonOutput, command.RedactOutput);
        }

        private static async Task<CliExecutionResult> ExecuteRoutineCreateAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-routine-create-path", "Missing routine create path.", command.JsonOutput);
            }

            return await runtime.CreateRoutineAsync(command.Key, command.AllowAnyPath, command.JsonOutput, command.RedactOutput);
        }

        private static async Task<CliExecutionResult> ExecuteRoutineUpdateAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-routine-selector", "Missing routine selector.", command.JsonOutput);
            }

            if (string.IsNullOrWhiteSpace(command.Value))
            {
                return BuildErrorResult(2, "missing-routine-update-path", "Missing routine update path.", command.JsonOutput);
            }

            return await runtime.UpdateRoutineAsync(command.Key, command.Value, command.AllowAnyPath, command.JsonOutput, command.RedactOutput);
        }

        private static async Task<CliExecutionResult> ExecuteRoutineDeleteAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-routine-selector", "Missing routine selector.", command.JsonOutput);
            }

            return await runtime.DeleteRoutineAsync(command.Key, command.JsonOutput, command.RedactOutput);
        }

        private static async Task<CliExecutionResult> ExecuteRoutineImportAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-routine-import-path", "Missing routine import path.", command.JsonOutput);
            }

            return await runtime.ImportRoutinesAsync(command.Key, command.ReplaceImport, command.AllowAnyPath, command.JsonOutput, command.RedactOutput);
        }

        private static async Task<CliExecutionResult> ExecuteRoutineExportAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-routine-export-path", "Missing routine export path.", command.JsonOutput);
            }

            var (success, output) = await runtime.ExportRoutinesAsync(command.Key, command.AllowAnyPath, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(success ? 0 : 3, output);
        }

        private static CliExecutionResult ExecuteDiagnosticsHistoryDetail(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-history-opid", "Missing diagnostics history operation id.", command.JsonOutput);
            }

            var (found, output) = runtime.GetDiagnosticsHistoryDetail(command.Key, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(found ? 0 : 5, output);
        }

        private static CliExecutionResult ExecuteDiagnosticsExport(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-log-export-path", "Missing diagnostics log export path.", command.JsonOutput);
            }

            var (success, output) = runtime.ExportLogs(command.Key, command.AllowAnyPath, command.DiagnosticsExportDetailLevel, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(success ? 0 : 3, output);
        }

        private static async Task<CliExecutionResult> ExecuteDiagnosticsBundleExportAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-diagnostic-bundle-export-path", "Missing diagnostics bundle export path.", command.JsonOutput);
            }

            var (success, output) = await runtime.ExportDiagnosticBundleAsync(
                command.Key,
                command.AllowAnyPath,
                command.DiagnosticsExportDetailLevel,
                command.IncludeSensitive,
                command.JsonOutput);
            return new CliExecutionResult(success ? 0 : 3, output);
        }

        private static CliExecutionResult ExecuteDiagnosticsReset(CliCommand command, ICliCommandRuntime runtime)
        {
            var (success, output) = runtime.ResetPerAppAudioRouting(command.JsonOutput);
            return new CliExecutionResult(success ? 0 : 3, output);
        }
    }
}

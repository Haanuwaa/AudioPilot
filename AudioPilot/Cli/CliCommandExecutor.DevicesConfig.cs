namespace AudioPilot.Cli
{
    public static partial class CliCommandExecutor
    {
        private static async Task<CliExecutionResult> ExecuteDevicesAndConfigAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            return command.Action switch
            {
                CliAction.Refresh => await ExecuteRefreshAsync(command, runtime),
                CliAction.StartupEnable => runtime.SetStartupEnabled(true)
                    ? new CliExecutionResult(0)
                    : BuildErrorResult(3, "startup-enable-failed", "Failed to enable startup.", command.JsonOutput),
                CliAction.StartupDisable => runtime.SetStartupEnabled(false)
                    ? new CliExecutionResult(0)
                    : BuildErrorResult(3, "startup-disable-failed", "Failed to disable startup.", command.JsonOutput),
                CliAction.StartupStatus => new CliExecutionResult(0, runtime.GetStartupStatus(command.JsonOutput)),
                CliAction.StartupOpen => runtime.OpenStartupSettings()
                    ? new CliExecutionResult(0, "Opened startup settings.")
                    : BuildErrorResult(3, "startup-open-failed", "Failed to open startup settings.", command.JsonOutput),
                CliAction.Status => new CliExecutionResult(0, runtime.GetStatus(command.JsonOutput, command.RedactOutput)),
                CliAction.WaitForDevice => await ExecuteWaitForDeviceAsync(command, runtime),
                CliAction.DevicesListOutput => ExecuteDeviceList(command, runtime, output: true),
                CliAction.DevicesListInput => ExecuteDeviceList(command, runtime, output: false),
                CliAction.DevicesGetOutput => ExecuteDeviceQuery(command, runtime, output: true, find: false),
                CliAction.DevicesGetInput => ExecuteDeviceQuery(command, runtime, output: false, find: false),
                CliAction.DevicesFindOutput => ExecuteDeviceQuery(command, runtime, output: true, find: true),
                CliAction.DevicesFindInput => ExecuteDeviceQuery(command, runtime, output: false, find: true),
                CliAction.CycleShowOutput => ExecuteCycleRead(command, runtime, output: true),
                CliAction.CycleShowInput => ExecuteCycleRead(command, runtime, output: false),
                CliAction.CycleValidateOutput => ExecuteCycleValidation(command, runtime, output: true),
                CliAction.CycleValidateInput => ExecuteCycleValidation(command, runtime, output: false),
                CliAction.CycleTestOutput => ExecuteCycleTest(command, runtime, output: true),
                CliAction.CycleTestInput => ExecuteCycleTest(command, runtime, output: false),
                CliAction.CycleAddOutput => await ExecuteCycleMutationAsync(command, runtime, output: true, remove: false),
                CliAction.CycleAddInput => await ExecuteCycleMutationAsync(command, runtime, output: false, remove: false),
                CliAction.CycleRemoveOutput => await ExecuteCycleMutationAsync(command, runtime, output: true, remove: true),
                CliAction.CycleRemoveInput => await ExecuteCycleMutationAsync(command, runtime, output: false, remove: true),
                CliAction.CycleReorderOutput => await ExecuteCycleReorderAsync(command, runtime, output: true),
                CliAction.CycleReorderInput => await ExecuteCycleReorderAsync(command, runtime, output: false),
                CliAction.ConfigGet => ExecuteConfigRead(command, runtime.GetConfig, "config-key-not-found", "missing-config-key", "Missing config key."),
                CliAction.ConfigList => new CliExecutionResult(0, runtime.GetConfigList(command.JsonOutput)),
                CliAction.ConfigSet => await ExecuteConfigWriteAsync(command, runtime.SetConfigAsync, "missing-config-key", "Missing config key.", "missing-config-value", "Missing config value.", "config-set-failed", "Failed to update config value.", successLabel: "Config updated."),
                CliAction.RuntimeGet => ExecuteConfigRead(command, runtime.GetRuntime, "runtime-key-not-found", "missing-runtime-key", "Missing runtime key."),
                CliAction.RuntimeList => new CliExecutionResult(0, runtime.GetRuntimeList(command.JsonOutput)),
                CliAction.RuntimeSet => await ExecuteConfigWriteAsync(command, (key, value) => Task.FromResult(runtime.SetRuntime(key, value)), "missing-runtime-key", "Missing runtime key.", "missing-runtime-value", "Missing runtime value.", "runtime-set-failed", "Failed to update runtime value.", successLabel: "Runtime updated."),
                CliAction.ConfigValidate => ExecuteConfigValidation(command, runtime),
                CliAction.ConfigExport => await ExecuteExportConfigAsync(command, runtime),
                CliAction.ConfigImport => await ExecuteImportConfigAsync(command, runtime),
                _ => BuildErrorResult(2, "unsupported-device-config-command", "Unsupported device/config command.", command.JsonOutput),
            };
        }

        private static async Task<CliExecutionResult> ExecuteRefreshAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            await runtime.RefreshAsync();
            return command.JsonOutput
                ? new CliExecutionResult(0, CliOutputFormatter.SerializeCliJson(new
                {
                    Success = true,
                    DiagCode = "refresh-success",
                }))
                : new CliExecutionResult(0);
        }

        private static async Task<CliExecutionResult> ExecuteWaitForDeviceAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-wait-device-id", "Missing wait device id.", command.JsonOutput);
            }

            if (!int.TryParse(command.Value, out int timeoutMs) || timeoutMs < 0)
            {
                timeoutMs = 30000;
            }

            var (found, output) = await runtime.WaitForDeviceAsync(
                command.Key,
                timeoutMs,
                command.WaitOutputOnly,
                command.WaitInputOnly,
                command.JsonOutput,
                command.RedactOutput);

            return new CliExecutionResult(found ? 0 : 5, output);
        }

        private static CliExecutionResult ExecuteDeviceList(CliCommand command, ICliCommandRuntime runtime, bool output)
            => new(0, runtime.GetDeviceList(output, command.JsonOutput, command.RedactOutput));

        private static CliExecutionResult ExecuteDeviceQuery(CliCommand command, ICliCommandRuntime runtime, bool output, bool find)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, find ? "missing-device-query" : "missing-device-selector", find ? "Missing device search query." : "Missing device selector.", command.JsonOutput);
            }

            var (found, resultOutput) = find
                ? runtime.FindDevices(output, command.Key, command.JsonOutput, command.RedactOutput)
                : runtime.GetDevice(output, command.Key, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(found ? 0 : 5, resultOutput);
        }

        private static CliExecutionResult ExecuteCycleRead(CliCommand command, ICliCommandRuntime runtime, bool output)
            => new(0, runtime.GetCycle(output, command.JsonOutput, command.RedactOutput));

        private static CliExecutionResult ExecuteCycleValidation(CliCommand command, ICliCommandRuntime runtime, bool output)
        {
            var (isValid, resultOutput) = runtime.GetCycleValidation(output, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(isValid ? 0 : 5, resultOutput);
        }

        private static CliExecutionResult ExecuteCycleTest(CliCommand command, ICliCommandRuntime runtime, bool output)
        {
            var (canSwitch, resultOutput) = runtime.GetCycleTest(output, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(canSwitch ? 0 : 5, resultOutput);
        }

        private static async Task<CliExecutionResult> ExecuteCycleMutationAsync(CliCommand command, ICliCommandRuntime runtime, bool output, bool remove)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-cycle-device-id", "Missing cycle device id.", command.JsonOutput);
            }

            var (success, resultOutput) = remove
                ? await runtime.RemoveCycleDeviceAsync(output, command.Key, command.JsonOutput, command.RedactOutput)
                : await runtime.AddCycleDeviceAsync(output, command.Key, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(success ? 0 : 3, resultOutput);
        }

        private static async Task<CliExecutionResult> ExecuteCycleReorderAsync(CliCommand command, ICliCommandRuntime runtime, bool output)
        {
            IReadOnlyList<string> deviceIds = SplitDeviceIds(command.Value);
            if (deviceIds.Count == 0)
            {
                return BuildErrorResult(2, "missing-cycle-device-ids", "Missing cycle reorder device ids.", command.JsonOutput);
            }

            var (success, resultOutput) = await runtime.ReorderCycleAsync(output, deviceIds, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(success ? 0 : 3, resultOutput);
        }

        private static CliExecutionResult ExecuteConfigRead(
            CliCommand command,
            Func<string, (bool Found, string? Value, string? Error)> reader,
            string notFoundErrorCode,
            string missingKeyErrorCode,
            string missingKeyMessage)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, missingKeyErrorCode, missingKeyMessage, command.JsonOutput);
            }

            var (found, value, error) = reader(command.Key);
            if (!found)
            {
                return BuildErrorResult(5, notFoundErrorCode, error ?? $"Unknown key '{command.Key}'.", command.JsonOutput);
            }

            if (!command.JsonOutput)
            {
                return new CliExecutionResult(0, value ?? string.Empty);
            }

            string json = CliOutputFormatter.SerializeCliJson(new
            {
                command.Key,
                Value = value ?? string.Empty,
            });
            return new CliExecutionResult(0, json);
        }

        private static async Task<CliExecutionResult> ExecuteConfigWriteAsync(
            CliCommand command,
            Func<string, string, Task<(bool Updated, string? Error)>> writer,
            string missingKeyErrorCode,
            string missingKeyMessage,
            string missingValueErrorCode,
            string missingValueMessage,
            string updateErrorCode,
            string updateErrorMessage,
            string successLabel)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, missingKeyErrorCode, missingKeyMessage, command.JsonOutput);
            }

            if (command.Value is null)
            {
                return BuildErrorResult(2, missingValueErrorCode, missingValueMessage, command.JsonOutput);
            }

            var (updated, error) = await writer(command.Key, command.Value);
            if (!updated)
            {
                return BuildErrorResult(5, updateErrorCode, error ?? updateErrorMessage, command.JsonOutput);
            }

            if (!command.JsonOutput)
            {
                return new CliExecutionResult(0, successLabel);
            }

            string json = CliOutputFormatter.SerializeCliJson(new
            {
                command.Key,
                command.Value,
                Updated = true,
            });
            return new CliExecutionResult(0, json);
        }

        private static CliExecutionResult ExecuteConfigValidation(CliCommand command, ICliCommandRuntime runtime)
        {
            var (isValid, output) = runtime.GetConfigValidation(command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(isValid ? 0 : 5, output);
        }

        private static async Task<CliExecutionResult> ExecuteExportConfigAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-export-path", "Missing export path.", command.JsonOutput);
            }

            var (success, output) = await runtime.ExportConfigAsync(command.Key, command.AllowAnyPath, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(success ? 0 : 3, output);
        }

        private static async Task<CliExecutionResult> ExecuteImportConfigAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(command.Key))
            {
                return BuildErrorResult(2, "missing-import-path", "Missing import path.", command.JsonOutput);
            }

            var (success, output) = await runtime.ImportConfigAsync(command.Key, command.ReplaceImport, command.AllowAnyPath, command.JsonOutput, command.RedactOutput);
            return new CliExecutionResult(success ? 0 : 3, output);
        }
    }
}

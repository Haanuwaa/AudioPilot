using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Cli;

internal static class CliDirectDeviceSwitch
{
    internal static async Task<CliExecutionResult> ExecuteAsync(
        CliCommand command,
        Func<IReadOnlyList<CycleDevice>> getDevices,
        Func<string?> getCurrentDeviceId,
        Func<CycleDevice, Task<bool>> switchAsync)
    {
        string kind = command.Action == CliAction.SwitchOutputToDevice ? "output" : "input";
        try
        {
            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(getDevices(), command.Value);
            string? currentDeviceId = getCurrentDeviceId();
            if (!string.IsNullOrWhiteSpace(command.Key) && !string.Equals(command.Key, currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return CliCommandExecutor.BuildExecutionFailureResult(5, "require-current-mismatch", $"Current {kind} device does not match --require-current value.", command.JsonOutput);
            }
            if (!resolution.Success || resolution.Device is not { } target)
            {
                string selector = CliOutputFormatter.FormatDeviceName(resolution.Selector, command.RedactOutput);
                string message = resolution.Ambiguous
                    ? $"{kind} device selector '{selector}' is ambiguous. Matching IDs: {string.Join(", ", resolution.Matches.Select(device => CliOutputFormatter.FormatDeviceId(device.Id, command.RedactOutput)))}."
                    : CliDeviceSelectorResolver.BuildNotFoundMessage(kind, selector);
                return CliCommandExecutor.BuildExecutionFailureResult(5, resolution.Ambiguous ? "device-selector-ambiguous" : "device-not-found", message, command.JsonOutput);
            }

            if (command.DryRun)
            {
                return new(0, CliOutputFormatter.FormatSwitchPreview(kind, currentDeviceId, target, command.JsonOutput, command.RedactOutput));
            }

            bool success = await switchAsync(target);
            if (!success)
            {
                return CliCommandExecutor.BuildExecutionFailureResult(3, $"{kind}-switch-failed", $"Failed to switch the {kind} device. It may have disconnected or another switch may be in progress.", command.JsonOutput);
            }

            string name = CliOutputFormatter.FormatDeviceName(target.Name, command.RedactOutput);
            string id = CliOutputFormatter.FormatDeviceId(target.Id, command.RedactOutput);
            return new(0, command.JsonOutput
                ? CliOutputFormatter.SerializeCliJson(new { Success = true, Kind = kind, TargetDeviceId = id, TargetDeviceName = name, DryRun = false, DiagCode = "switch-success" })
                : $"[diag-code:switch-success] {kind} device set to '{name}' ({id}).");
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("CliDirectDeviceSwitch", $"cli-direct-switch-failed | kind={kind}", nameof(ExecuteAsync), ex);
            return CliCommandExecutor.BuildExecutionFailureResult(7, $"{kind}-switch-failed", $"Failed to switch the {kind} device.", command.JsonOutput);
        }
    }
}

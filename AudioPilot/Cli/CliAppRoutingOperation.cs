using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Cli;

/// <summary>Applies the mixer's process-scoped device preferences in either CLI host.</summary>
internal static class CliAppRoutingOperation
{
    internal sealed record Target(uint ProcessId, string ProcessName, long StartedTicks);

    internal static async Task<CliExecutionResult> ExecuteAsync(CliCommand command, ApplicationRoutingOperations routing,
        CancellationToken token, Func<uint, bool, Func<Task>, CancellationToken, Task>? manualScope = null,
        Func<string, Target>? resolveProcess = null)
    {
        var (operation, direction) = command.Action switch
        {
            CliAction.AppRouteGetOutput => ("get", "output"),
            CliAction.AppRouteGetInput => ("get", "input"),
            CliAction.AppRouteSetOutput => ("set", "output"),
            CliAction.AppRouteSetInput => ("set", "input"),
            CliAction.AppRouteClearOutput => ("clear", "output"),
            CliAction.AppRouteClearInput => ("clear", "input"),
            _ => (string.Empty, string.Empty),
        };
        if (!Validate(command, operation, direction))
            return Error(command, 2, "app-route-invalid-command", "Invalid application routing command. See help app.");

        bool submitted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            Target target = await Task.Run(() => (resolveProcess ?? ResolveProcess)(command.Key!), token);
            bool output = direction == "output";
            ApplicationRoutingInventory inventory = await routing.Read(target.ProcessId, output, target.StartedTicks, token);
            if (operation == "get")
                return Result(command, target, direction, inventory.Devices, inventory.Preference, null, null);

            string? deviceId = null;
            if (operation == "set")
            {
                var resolved = CliDeviceSelectorResolver.ResolveExact(inventory.Devices, command.Value);
                if (!resolved.Success)
                    return Error(command, 5, resolved.Ambiguous ? "app-route-device-ambiguous" : "app-route-device-unavailable",
                        resolved.Ambiguous ? "Several active devices match that name. Use --device-id."
                            : "No active device matches the requested direction and selector. Use devices list.");
                deviceId = resolved.Device!.Id;
            }

            ApplicationRoutingChange change = default;
            async Task Apply()
            {
                token.ThrowIfCancellationRequested();
                submitted = true;
                change = await routing.Change(target.ProcessId, output, target.StartedTicks, deviceId, token);
            }
            if (manualScope != null) await manualScope(target.ProcessId, output, Apply, token);
            else await Apply();
            return Result(command, target, direction, inventory.Devices, change.Preference, change.Result, deviceId);
        }
        catch (OperationCanceledException) when (!submitted) { throw; }
        catch (AmbiguousMatchException) when (!submitted)
        {
            return Error(command, 5, "app-route-process-ambiguous", "Several running processes match. Select one --pid.");
        }
        catch (ArgumentException) when (!submitted)
        {
            return Error(command, 5, "app-route-process-unavailable", "The selected process is no longer running.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("AppRouting", $"app-route-unavailable | submitted={submitted} error={ex.GetType().Name} hresult=0x{ex.HResult:X8}");
            return Error(command, 3, submitted ? "app-route-result-unknown" : "app-route-unavailable",
                submitted ? "The routing request may have applied. Read its preference before retrying."
                    : "Could not inspect the application or its device preferences. No routing change was sent.");
        }
    }

    private static bool Validate(CliCommand command, string operation, string direction)
    {
        if (operation.Length == 0) return false;
        string[] args = ["app", "route", operation, direction];
        if (command.Key?.StartsWith("pid:", StringComparison.Ordinal) == true) args = [.. args, "--pid", command.Key[4..]];
        else if (command.Key?.StartsWith("name:", StringComparison.Ordinal) == true) args = [.. args, "--process", command.Key[5..]];
        else return false;
        if (operation == "set")
        {
            var query = CliDeviceSelectorResolver.Decode(command.Value);
            if (query.Kind == CliDeviceSelectorKind.Raw) return false;
            args = [.. args, query.Kind == CliDeviceSelectorKind.ExactId ? "--device-id" : "--device", query.Value];
        }
        else if (command.Value != null) return false;
        return CliCommand.TryParse(args, out var parsed, out _) && parsed.Action == command.Action
            && parsed.Key == command.Key && parsed.Value == command.Value;
    }

    private static Target ResolveProcess(string selector)
    {
        if (selector.StartsWith("pid:", StringComparison.Ordinal))
        {
            using var process = Process.GetProcessById(int.Parse(selector.AsSpan(4), CultureInfo.InvariantCulture));
            return Capture(process);
        }
        Process[] matches = Process.GetProcessesByName(selector[5..]);
        try
        {
            if (matches.Length == 0) throw new ArgumentException("Process not found.", nameof(selector));
            if (matches.Length != 1) throw new AmbiguousMatchException();
            return Capture(matches[0]);
        }
        finally { foreach (var process in matches) process.Dispose(); }
    }

    private static Target Capture(Process process)
    {
        long started = process.StartTime.ToUniversalTime().Ticks;
        if (process.HasExited) throw new ArgumentException("Process exited.", nameof(process));
        return new(checked((uint)process.Id), process.ProcessName, started);
    }

    private static CliExecutionResult Result(CliCommand command, Target target, string direction, IReadOnlyList<CycleDevice> devices,
        ApplicationDevicePreference preference, ProcessAudioRoutingResult? applied, string? requestedDevice)
    {
        bool confirmed = preference.Available && !preference.Mixed && string.Equals(preference.DeviceId, requestedDevice, StringComparison.Ordinal);
        bool success = applied.HasValue ? applied != ProcessAudioRoutingResult.Failed && confirmed : preference.Available;
        string code = applied == ProcessAudioRoutingResult.Failed ? "app-route-partial"
            : !success ? "app-route-unconfirmed" : applied.HasValue ? "app-route-saved" : "app-route-read";
        string state = !preference.Available ? "unknown" : preference.Mixed ? "mixed" : preference.DeviceId == null ? "default" : "device";
        string? deviceId = state == "device" ? preference.DeviceId : null;
        CycleDevice? device = deviceId == null ? null : devices.FirstOrDefault(item => string.Equals(item.Id, deviceId, StringComparison.Ordinal));
        string message = applied == ProcessAudioRoutingResult.Failed ? "Could not apply all audio roles. Some changes may have applied; read the preference before retrying."
            : !success ? "The preference could not be confirmed. Read it again before retrying a change."
            : !applied.HasValue ? "Current application device preference."
            : applied == ProcessAudioRoutingResult.DeferredNoAudio ? "Preference saved; waiting for app audio."
            : "Preference saved. Some apps need to restart audio.";
        string? Display(string? value) => value == null ? null : command.RedactOutput ? LogPrivacy.RedactedLabel(value) : value;
        string processName = Display(target.ProcessName)!;
        string? displayedId = Display(deviceId), deviceName = Display(device?.Name);
        string displayedPreference = state switch
        {
            "default" => "Windows default",
            "device" => deviceName == null ? displayedId! : $"{deviceName} ({displayedId})",
            "mixed" => "different devices across audio roles",
            _ => "unknown",
        };
        string text = command.JsonOutput ? CliOutputFormatter.SerializeCliJson(new
        {
            Success = success,
            DiagCode = code,
            ProcessId = command.RedactOutput ? (uint?)null : target.ProcessId,
            ProcessName = processName,
            Direction = direction,
            PreferenceState = state,
            DeviceId = displayedId,
            DeviceName = deviceName,
            DeviceAvailable = deviceId == null ? (bool?)null : device != null,
            Deferred = applied == ProcessAudioRoutingResult.DeferredNoAudio,
            Message = message,
        }) : $"[diag-code:{code}] {processName}: {direction} preference = {displayedPreference}. {message}";
        return new(success ? 0 : 3, text);
    }

    private static CliExecutionResult Error(CliCommand command, int exitCode, string code, string message) =>
        CliCommandExecutor.BuildExecutionFailureResult(exitCode, code, message, command.JsonOutput);
}

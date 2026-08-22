using System.Diagnostics;
using System.Globalization;
using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.UI;
using AudioPilot.Services.UI.MediaOverlay;
using NAudio.CoreAudioApi;

namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner
    {
        public Task<CliExecutionResult> SwitchToDeviceAsync(CliCommand command)
        {
            bool output = command.Action == CliAction.SwitchOutputToDevice;
            Settings settings = SettingsService.LoadSettings();
            return CliDirectDeviceSwitch.ExecuteAsync(command,
                () =>
                {
                    AudioService.UpdateRoleConfiguration(settings.DeviceSwitching.Output.SwitchRoles, settings.DeviceSwitching.Input.SwitchRoles);
                    return output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
                },
                () => GetCurrentDeviceId(output),
                async target =>
                {
                    string opId = $"cli-direct:{Guid.NewGuid():N}";
                    bool success;
                    if (output)
                    {
                        bool muteMic = command.MuteMic || (TryGetDefaultCaptureMute() ?? false);
                        bool muteSound = command.MuteSound || (TryGetDefaultPlaybackMute() ?? false);
                        bool deafen = command.Deafen || (muteMic && muteSound);
                        (success, _) = _audioOverrides?.SwitchAudioDeviceAsync != null
                            ? _audioOverrides.SwitchAudioDeviceAsync(target.Id, muteMic, muteSound, deafen, settings.DeviceSwitching.PreserveAudioLevels, opId)
                            : await AudioService.SwitchAudioDeviceAsync(target.Id, muteMic, muteSound, deafen, settings.DeviceSwitching.PreserveAudioLevels, opId: opId);
                    }
                    else
                    {
                        (success, _) = _audioOverrides?.SwitchInputDeviceToAsync != null
                            ? _audioOverrides.SwitchInputDeviceToAsync(target.Id, target.Name, opId)
                            : await AudioService.SwitchInputDeviceToAsync(target.Id, target.Name, settings.DeviceSwitching.PreserveAudioLevels, showOverlay: null, opId: opId);
                    }

                    RecordCliActionHistory(ExecutionHistoryKind.Switch, output ? "switch-output-device" : "switch-input-device", success, skipped: false,
                        success ? $"Device set to '{target.Name}'." : "Direct device switch failed.", success ? null : "Direct device switch failed.",
                        outputDeviceName: output ? target.Name : null, inputDeviceName: output ? null : target.Name);
                    return success;
                });
        }

        public string GetDeviceList(bool output, bool jsonOutput, bool redactOutput)
        {
            string kind = output ? "output" : "input";
            var devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            return CliOutputFormatter.FormatDeviceList(kind, devices, jsonOutput, redactOutput);
        }

        public (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput)
        {
            string kind = output ? "output" : "input";
            List<CycleDevice> devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selector);
            if (resolution.Success && resolution.Device != null)
            {
                return (true, CliOutputFormatter.FormatDeviceGetResult(kind, resolution.Device, jsonOutput, redactOutput));
            }

            string message = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return (false, CliOutputFormatter.FormatDeviceGetError(kind, resolution.Ambiguous ? "device-selector-ambiguous" : "device-not-found", message, jsonOutput, redactOutput));
        }

        public (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput)
        {
            string kind = output ? "output" : "input";
            List<CycleDevice> devices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            IReadOnlyList<CycleDevice> matches = CliDeviceSelectorResolver.FindMatches(devices, query);
            return (matches.Count > 0, CliOutputFormatter.FormatDeviceFindResult(kind, query, matches, jsonOutput, redactOutput));
        }

        public string GetCycle(bool output, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycle = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            return CliOutputFormatter.FormatCycleList(kind, cycle, jsonOutput, redactOutput);
        }

        public (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var result = SettingsValidationService.ValidateCycle(cycleDevices, activeDevices);
            string formatted = CliOutputFormatter.FormatCycleValidation(kind, result.DuplicateDeviceNames, result.DisconnectedDeviceNames, jsonOutput, redactOutput);
            return (result.IsValid, formatted);
        }

        public (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                cycleDevices,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            return (
                preflight.CanSwitch,
                CliOutputFormatter.FormatCycleTest(
                    kind,
                    preflight.ConfiguredCount,
                    preflight.ConnectedConfiguredCount,
                    preflight.HasDefaultInputDevice,
                    preflight.Reasons,
                    jsonOutput,
                    redactOutput));
        }

        public (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput)
        {
            return UpdateCycle(output, "add", deviceId, null, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput)
        {
            return UpdateCycle(output, "remove", deviceId, null, jsonOutput, redactOutput);
        }

        public (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput)
        {
            return UpdateCycle(output, "reorder", null, deviceIds, jsonOutput, redactOutput);
        }

        public (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput)
        {
            Settings settings = SettingsService.LoadSettings();
            var cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            string kind = output ? "output" : "input";

            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                cycleDevices,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            if (!preflight.CanSwitch)
            {
                return (false, CliOutputFormatter.FormatCycleTest(kind, preflight.ConfiguredCount, preflight.ConnectedConfiguredCount, preflight.HasDefaultInputDevice, preflight.Reasons, jsonOutput, redactOutput));
            }

            string? currentId = GetCurrentDeviceId(output);
            int currentIndex = cycleDevices.FindIndex(device => string.Equals(device.Id, currentId, StringComparison.OrdinalIgnoreCase));
            int targetIndex = ResolveCycleTargetIndex(currentIndex, cycleDevices.Count, reverse);
            return (true, CliOutputFormatter.FormatSwitchPreview(kind, currentId, cycleDevices[targetIndex], jsonOutput, redactOutput));
        }

        public string? GetCurrentDeviceId(bool output)
        {
            try
            {
                if (output)
                {
                    if (_audioOverrides?.GetCurrentOutputDeviceId != null)
                    {
                        return _audioOverrides.GetCurrentOutputDeviceId();
                    }

                    using var device = AudioService.GetDefaultPlaybackDevice();
                    return device?.ID;
                }

                if (_audioOverrides?.GetCurrentInputDeviceId != null)
                {
                    return _audioOverrides.GetCurrentInputDeviceId();
                }

                using var input = AudioService.GetDefaultRecordingDevice();
                return input?.ID;
            }
            catch
            {
                return null;
            }
        }

        public async Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool found = await WaitForConditionAsync(
                () => IsDeviceAvailable(deviceId, outputOnly, inputOnly),
                timeoutMs,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                usePollingFallbackWhenOverridesPresent: true);

            return (found, CliOutputFormatter.FormatDeviceWaitResult(deviceId, found, outputOnly ? "output" : inputOnly ? "input" : "any", sw.ElapsedMilliseconds, timeoutMs, jsonOutput, redactOutput));
        }

        private bool IsDeviceAvailable(string deviceId, bool outputOnly, bool inputOnly)
        {
            bool outputFound = (outputOnly || (!outputOnly && !inputOnly)) &&
                GetActiveOutputDeviceInfos().Any(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            bool inputFound = (inputOnly || (!outputOnly && !inputOnly)) &&
                GetActiveInputDeviceInfos().Any(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));

            return outputFound || inputFound;
        }

        /// <summary>Waits for endpoint readiness after a driver accepts reconnect, rather than treating request acceptance as availability.</summary>
        internal async Task<bool> WaitForReconnectReadinessAsync(
            Func<bool> isReady, BluetoothReconnectAttemptResult result, string opId)
        {
            if (!result.Attempted) return false;
            int timeoutMs = result.Connected
                ? RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs
                : RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs;
            var clock = Stopwatch.StartNew();
            bool ready = await WaitForConditionAsync(isReady, timeoutMs,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                usePollingFallbackWhenOverridesPresent: true);
            Logger.Instance.Info("BluetoothReconnect", () => $"bluetooth-reconnect-cli-readiness | opId={opId} requestAccepted={result.Connected} ready={ready} elapsedMs={clock.ElapsedMilliseconds} timeoutMs={timeoutMs}");
            return ready;
        }

        private async Task<bool> WaitForConditionAsync(
            Func<bool> condition,
            int timeoutMs,
            int pollingFallbackDelayMs,
            bool usePollingFallbackWhenOverridesPresent)
        {
            CancellationToken cancellationToken;
            try
            {
                cancellationToken = _lifetimeCts.Token;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (condition())
            {
                return true;
            }

            if (timeoutMs <= 0)
            {
                return false;
            }

            if (CanSubscribeToDeviceStateChanged())
            {
                using var signal = new SemaphoreSlim(0, 1);
                using IDisposable signalSubscription = SubscribeDeviceStateChanged(() =>
                {
                    try
                    {
                        signal.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
                if (condition())
                {
                    return true;
                }

                var signalWaitStopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (!condition())
                {
                    int remainingMs = timeoutMs - (int)signalWaitStopwatch.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        return false;
                    }

                    try
                    {
                        int recheckMs = Math.Min(remainingMs, Math.Max(1, pollingFallbackDelayMs));
                        await signal.WaitAsync(TimeSpan.FromMilliseconds(recheckMs), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }

                return true;
            }

            if (_audioOverrides != null && !usePollingFallbackWhenOverridesPresent)
            {
                return false;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (condition())
                {
                    return true;
                }

                int nextDelayMs = CliWaitTiming.ResolveRemainingDelayMs(sw.ElapsedMilliseconds, timeoutMs, pollingFallbackDelayMs);
                if (nextDelayMs <= 0)
                {
                    return false;
                }

                try
                {
                    await DelayAsync(nextDelayMs).WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        private Task DelayAsync(int delayMs)
        {
            return _audioOverrides?.DelayAsync?.Invoke(delayMs) ?? Task.Delay(delayMs);
        }

        private bool CanSubscribeToDeviceStateChanged()
        {
            if (_audioOverrides?.SubscribeDeviceStateChanged != null)
            {
                return true;
            }

            if (_audioOverrides != null)
            {
                return false;
            }

            return true;
        }

        private IDisposable SubscribeDeviceStateChanged(Action onChanged)
        {
            if (_audioOverrides?.SubscribeDeviceStateChanged != null)
            {
                return _audioOverrides.SubscribeDeviceStateChanged(onChanged);
            }

            AudioDeviceService audioService = AudioService;
            audioService.DeviceStateChanged += onChanged;
            return new DelegateDisposable(() => audioService.DeviceStateChanged -= onChanged);
        }

        private (bool Success, string Output) UpdateCycle(
            bool output,
            string action,
            string? deviceId,
            IReadOnlyList<string>? orderedDeviceIds,
            bool jsonOutput,
            bool redactOutput)
        {
            try
            {
                Settings settings = SettingsService.LoadSettings();
                List<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
                List<CycleDevice> activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
                string kind = output ? "output" : "input";
                string? deviceName = FindDeviceName(cycleDevices, activeDevices, deviceId);

                bool updated;
                string? error;
                switch (action)
                {
                    case "add":
                        if (!TryResolveCycleMutationSelector(activeDevices, kind, deviceId ?? string.Empty, out string resolvedAddDeviceId, out string? addSelectorError))
                        {
                            return (false, FormatCycleFailure(action, addSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                        }

                        deviceId = resolvedAddDeviceId;
                        deviceName = FindDeviceName(cycleDevices, activeDevices, deviceId);
                        updated = CliCycleManager.TryAddDevice(cycleDevices, activeDevices, resolvedAddDeviceId, out _, out _, out string addMessage);
                        error = addMessage;
                        break;
                    case "remove":
                        if (!TryResolveCycleMutationSelector(cycleDevices, kind, deviceId ?? string.Empty, out string resolvedRemoveDeviceId, out string? removeSelectorError, selector => $"Device '{selector}' is not configured in the cycle."))
                        {
                            return (false, FormatCycleFailure(action, removeSelectorError ?? "Failed to resolve device selector.", jsonOutput));
                        }

                        deviceId = resolvedRemoveDeviceId;
                        deviceName = FindDeviceName(cycleDevices, activeDevices, deviceId);
                        updated = CliCycleManager.TryRemoveDevice(cycleDevices, resolvedRemoveDeviceId, out _, out _, out string removeMessage);
                        error = removeMessage;
                        break;
                    case "reorder":
                        updated = CliCycleManager.TryReorder(cycleDevices, orderedDeviceIds ?? [], out _, out string reorderMessage);
                        error = reorderMessage;
                        break;
                    default:
                        updated = false;
                        error = "Unsupported cycle action.";
                        break;
                }

                if (!updated)
                {
                    return (false, FormatCycleFailure(action, error ?? "Failed to update cycle.", jsonOutput));
                }

                SettingsService.SaveSettings(settings);
                return (true, CliOutputFormatter.FormatCycleMutationResult(kind, action, GetCycleDiagCode(action), cycleDevices, deviceId, deviceName, jsonOutput, redactOutput));
            }
            catch
            {
                return (false, FormatCycleFailure(action, "Failed to persist cycle changes.", jsonOutput));
            }
        }

        private static string? FindDeviceName(List<CycleDevice> cycleDevices, List<CycleDevice> activeDevices, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            for (int index = 0; index < cycleDevices.Count; index++)
            {
                if (string.Equals(cycleDevices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return cycleDevices[index].Name;
                }
            }

            for (int index = 0; index < activeDevices.Count; index++)
            {
                if (string.Equals(activeDevices[index].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return activeDevices[index].Name;
                }
            }

            return null;
        }

        private static bool TryResolveCycleMutationSelector(IReadOnlyList<CycleDevice> devices, string kind, string selectorSpec, out string resolvedDeviceId, out string? error, Func<string, string>? notFoundMessageFactory = null)
        {
            resolvedDeviceId = selectorSpec;
            error = null;

            CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices, selectorSpec);
            if (resolution.Success && resolution.Device != null)
            {
                resolvedDeviceId = resolution.Device.Id;
                return true;
            }

            error = resolution.Ambiguous
                ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
                : notFoundMessageFactory?.Invoke(resolution.Selector) ?? CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
            return false;
        }

        private static string GetCycleDiagCode(string action)
        {
            return action switch
            {
                "add" => "cycle-add-success",
                "remove" => "cycle-remove-success",
                "reorder" => "cycle-reorder-success",
                _ => "cycle-update-success",
            };
        }

        private static string FormatCycleFailure(string action, string message, bool jsonOutput)
        {
            string diagCode = action switch
            {
                "add" => "cycle-add-failed",
                "remove" => "cycle-remove-failed",
                "reorder" => "cycle-reorder-failed",
                _ => "cycle-update-failed",
            };

            return jsonOutput
                ? CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message })
                : $"[diag-code:{diagCode}] {message}";
        }

        public async ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse)
        {
            string? beforeDeviceName = TryGetCurrentDefaultDeviceName(output: true);
            Settings settings = SettingsService.LoadSettings();
            List<CycleDevice> configuredCycle =
            [
                .. settings.DeviceSwitching.Output.CycleDevices
                    .Where(device => device != null && !string.IsNullOrWhiteSpace(device.Id))
                    .Select(device => new CycleDevice { Id = device.Id, Name = device.Name, StableId = device.StableId })
            ];

            if (configuredCycle.Count == 0)
            {
                return false;
            }

            List<MMDevice> activeDevices = [];
            MMDevice? currentDevice = null;

            try
            {
                activeDevices = [.. AudioService.GetActivePlaybackDevices().Cast<MMDevice>()];
                var activeById = activeDevices.ToDictionary(d => d.ID, d => d, StringComparer.OrdinalIgnoreCase);
                List<CycleDevice> availableIdentities = [.. activeDevices.Select(device => new CycleDevice
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    StableId = AudioEndpointIdentity.TryGetStableId(device),
                })];
                configuredCycle = [.. configuredCycle.Select(device => PersistedAudioDeviceResolver.TryResolveMatch(device, availableIdentities) ?? device)];
                List<CycleDevice> connectedCycle =
                [
                    .. configuredCycle
                        .Where(d => activeById.ContainsKey(d.Id))
                        .Select(d => new CycleDevice { Id = d.Id, Name = activeById[d.Id].FriendlyName, StableId = d.StableId })
                ];

                List<CycleDevice> skippedCycle =
                [
                    .. configuredCycle
                        .Where(d => !activeById.ContainsKey(d.Id))
                        .Select(d => new CycleDevice { Id = d.Id, Name = d.Name, StableId = d.StableId })
                ];

                if (connectedCycle.Count <= 1)
                {
                    BluetoothReconnectOptions reconnectOptions = BluetoothReconnectOptions.FromSettings(settings);
                    BluetoothReconnectAttemptResult reconnectResult = await BluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                        configuredCycle,
                        new HashSet<string>(activeById.Keys, StringComparer.OrdinalIgnoreCase),
                        BluetoothReconnectDeviceKind.Output,
                        reconnectOptions,
                        opId: "cli");

                    if (reconnectResult.Attempted)
                    {
                        var disconnectedIds = new HashSet<string>(skippedCycle.Select(device => device.Id), StringComparer.OrdinalIgnoreCase);
                        await WaitForReconnectReadinessAsync(
                            () => GetActiveOutputDeviceInfos().Any(device => disconnectedIds.Contains(device.Id)),
                            reconnectResult,
                            "cli-output");
                        RefreshActivePlaybackDevices(ref activeDevices, ref activeById, ref connectedCycle, configuredCycle);
                        skippedCycle =
                        [
                            .. configuredCycle
                                .Where(d => !activeById.ContainsKey(d.Id))
                                .Select(d => new CycleDevice { Id = d.Id, Name = d.Name, StableId = d.StableId })
                        ];
                    }
                }

                if (connectedCycle.Count <= 1)
                {
                    return false;
                }

                currentDevice = AudioService.GetDefaultPlaybackDevice();
                if (currentDevice == null)
                {
                    return false;
                }

                int currentIndex = connectedCycle.FindIndex(d => d.Id.Equals(currentDevice.ID, StringComparison.OrdinalIgnoreCase));
                int targetIndex = ResolveCycleTargetIndex(currentIndex, connectedCycle.Count, reverse);
                if (targetIndex < 0)
                {
                    return false;
                }

                (bool success, _) = await AudioService.SwitchAudioDeviceAsync(
                    connectedCycle[targetIndex].Id,
                    muteMic,
                    muteSound,
                    deafen,
                    settings.DeviceSwitching.PreserveAudioLevels,
                    opId: "cli");

                string? afterDeviceName = TryGetCurrentDefaultDeviceName(output: true);
                RecordCliActionHistory(ExecutionHistoryKind.Switch, reverse ? "switch-output-reverse" : "switch-output", success, skipped: false, success ? $"Output switch completed{(string.IsNullOrWhiteSpace(afterDeviceName) ? string.Empty : $" to '{afterDeviceName}'")}." : "Output switch failed or was rejected.", success ? null : "Output switch failed or was rejected.", target: beforeDeviceName, outputDeviceName: afterDeviceName);
                return success;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SwitchOutputAsync), "cli-switch-output-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Switch, reverse ? "switch-output-reverse" : "switch-output", success: false, skipped: false, summary: "Output switch failed or was rejected.", reason: "Output switch threw an exception.", target: beforeDeviceName);
                return false;
            }
            finally
            {
                DisposeForCleanup(currentDevice, nameof(SwitchOutputAsync), "switch-output-current-device");
                foreach (var device in activeDevices)
                {
                    DisposeForCleanup(device, nameof(SwitchOutputAsync), "switch-output-active-device");
                }
            }
        }

        public async ValueTask<bool> SwitchInputAsync(bool reverse)
        {
            string? beforeDeviceName = TryGetCurrentDefaultDeviceName(output: false);
            Settings settings = SettingsService.LoadSettings();
            List<CycleDevice> configuredCycle =
            [
                .. settings.DeviceSwitching.Input.CycleDevices
                    .Where(device => device != null && !string.IsNullOrWhiteSpace(device.Id))
                    .Select(device => new CycleDevice { Id = device.Id, Name = device.Name, StableId = device.StableId })
            ];

            if (configuredCycle.Count == 0)
            {
                return false;
            }

            List<MMDevice> activeDevices = [];
            MMDevice? currentDevice = null;

            try
            {
                activeDevices = [.. AudioService.GetActiveCaptureDevices().Cast<MMDevice>()];
                var activeById = activeDevices.ToDictionary(d => d.ID, d => d, StringComparer.OrdinalIgnoreCase);
                List<CycleDevice> availableIdentities = [.. activeDevices.Select(device => new CycleDevice
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    StableId = AudioEndpointIdentity.TryGetStableId(device),
                })];
                configuredCycle = [.. configuredCycle.Select(device => PersistedAudioDeviceResolver.TryResolveMatch(device, availableIdentities) ?? device)];
                List<CycleDevice> connectedCycle =
                [
                    .. configuredCycle
                        .Where(d => activeById.ContainsKey(d.Id))
                        .Select(d => new CycleDevice { Id = d.Id, Name = activeById[d.Id].FriendlyName, StableId = d.StableId })
                ];

                List<CycleDevice> skippedCycle =
                [
                    .. configuredCycle
                        .Where(d => !activeById.ContainsKey(d.Id))
                        .Select(d => new CycleDevice { Id = d.Id, Name = d.Name, StableId = d.StableId })
                ];

                if (connectedCycle.Count <= 1)
                {
                    BluetoothReconnectOptions reconnectOptions = BluetoothReconnectOptions.FromSettings(settings);
                    BluetoothReconnectAttemptResult reconnectResult = await BluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                        configuredCycle,
                        new HashSet<string>(activeById.Keys, StringComparer.OrdinalIgnoreCase),
                        BluetoothReconnectDeviceKind.Input,
                        reconnectOptions,
                        opId: "cli");

                    if (reconnectResult.Attempted)
                    {
                        var disconnectedIds = new HashSet<string>(skippedCycle.Select(device => device.Id), StringComparer.OrdinalIgnoreCase);
                        await WaitForReconnectReadinessAsync(
                            () => GetActiveInputDeviceInfos().Any(device => disconnectedIds.Contains(device.Id)),
                            reconnectResult,
                            "cli-input");
                        RefreshActiveCaptureDevices(ref activeDevices, ref activeById, ref connectedCycle, configuredCycle);
                        skippedCycle =
                        [
                            .. configuredCycle
                                .Where(d => !activeById.ContainsKey(d.Id))
                                .Select(d => new CycleDevice { Id = d.Id, Name = d.Name, StableId = d.StableId })
                        ];
                    }
                }

                if (connectedCycle.Count <= 1)
                {
                    return false;
                }

                currentDevice = AudioService.GetDefaultRecordingDevice();
                if (currentDevice == null)
                {
                    return false;
                }

                int currentIndex = connectedCycle.FindIndex(d => d.Id.Equals(currentDevice.ID, StringComparison.OrdinalIgnoreCase));
                int targetIndex = ResolveCycleTargetIndex(currentIndex, connectedCycle.Count, reverse);
                if (targetIndex < 0)
                {
                    return false;
                }

                var targetDevice = connectedCycle[targetIndex];
                var (switchSuccess, _) = await AudioService.SwitchInputDeviceToAsync(targetDevice.Id, targetDevice.Name, settings.DeviceSwitching.PreserveAudioLevels, showOverlay: null, opId: "cli");
                string? afterDeviceName = TryGetCurrentDefaultDeviceName(output: false);
                RecordCliActionHistory(ExecutionHistoryKind.Switch, reverse ? "switch-input-reverse" : "switch-input", switchSuccess, skipped: false, switchSuccess ? $"Input switch completed{(string.IsNullOrWhiteSpace(afterDeviceName) ? string.Empty : $" to '{afterDeviceName}'")}." : "Input switch failed or was rejected.", switchSuccess ? null : "Input switch failed or was rejected.", target: beforeDeviceName, inputDeviceName: afterDeviceName);
                return switchSuccess;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SwitchInputAsync), "cli-switch-input-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Switch, reverse ? "switch-input-reverse" : "switch-input", success: false, skipped: false, summary: "Input switch failed or was rejected.", reason: "Input switch threw an exception.", target: beforeDeviceName);
                return false;
            }
            finally
            {
                DisposeForCleanup(currentDevice, nameof(SwitchInputAsync), "switch-input-current-device");
                foreach (var device in activeDevices)
                {
                    DisposeForCleanup(device, nameof(SwitchInputAsync), "switch-input-active-device");
                }
            }
        }

        private List<CycleDevice> GetActiveOutputDeviceInfos()
        {
            if (_audioOverrides?.GetActiveOutputDeviceInfos != null)
            {
                return _audioOverrides.GetActiveOutputDeviceInfos();
            }

            return GetActiveDeviceInfos(AudioService.GetActivePlaybackDevices);
        }

        private List<CycleDevice> GetActiveInputDeviceInfos()
        {
            if (_audioOverrides?.GetActiveInputDeviceInfos != null)
            {
                return _audioOverrides.GetActiveInputDeviceInfos();
            }

            return GetActiveDeviceInfos(AudioService.GetActiveCaptureDevices);
        }

        private static List<CycleDevice> GetActiveDeviceInfos(Func<MMDeviceCollection> getDevices)
        {
            MMDeviceCollection? devices = null;
            try
            {
                devices = getDevices();
                return
                [
                    .. devices.Cast<MMDevice>()
                        .Select(d => new CycleDevice { Id = d.ID, Name = d.FriendlyName, StableId = AudioEndpointIdentity.TryGetStableId(d) })
                ];
            }
            finally
            {
                if (devices != null)
                {
                    foreach (var device in devices.Cast<MMDevice>())
                    {
                        DisposeForCleanup(device, nameof(GetActiveDeviceInfos), "active-device-info-collection");
                    }
                }
            }
        }

        private string? TryGetCurrentDefaultDeviceName(bool output)
        {
            try
            {
                if (output && _audioOverrides?.GetDefaultPlaybackDeviceSnapshot != null)
                {
                    return _audioOverrides.GetDefaultPlaybackDeviceSnapshot().DeviceName;
                }

                using var device = output ? AudioService.GetDefaultPlaybackDevice() : AudioService.GetDefaultRecordingDevice();
                return device?.FriendlyName;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(GetCurrentDeviceId), output ? "cli-get-current-output-device-failed" : "cli-get-current-input-device-failed", ex);
                return null;
            }
        }

        private void RefreshActivePlaybackDevices(
            ref List<MMDevice> activeDevices,
            ref Dictionary<string, MMDevice> activeById,
            ref List<CycleDevice> connectedCycle,
            IReadOnlyList<CycleDevice> configuredCycle)
        {
            DisposeDevices(activeDevices);
            activeDevices = [.. AudioService.GetActivePlaybackDevices().Cast<MMDevice>()];
            Dictionary<string, MMDevice> updatedActiveById = activeDevices.ToDictionary(d => d.ID, d => d, StringComparer.OrdinalIgnoreCase);
            activeById = updatedActiveById;
            connectedCycle =
            [
                .. configuredCycle
                    .Where(d => updatedActiveById.ContainsKey(d.Id))
                    .Select(d => new CycleDevice { Id = d.Id, Name = updatedActiveById[d.Id].FriendlyName, StableId = d.StableId })
            ];
        }

        private void RefreshActiveCaptureDevices(
            ref List<MMDevice> activeDevices,
            ref Dictionary<string, MMDevice> activeById,
            ref List<CycleDevice> connectedCycle,
            IReadOnlyList<CycleDevice> configuredCycle)
        {
            DisposeDevices(activeDevices);
            activeDevices = [.. AudioService.GetActiveCaptureDevices().Cast<MMDevice>()];
            Dictionary<string, MMDevice> updatedActiveById = activeDevices.ToDictionary(d => d.ID, d => d, StringComparer.OrdinalIgnoreCase);
            activeById = updatedActiveById;
            connectedCycle =
            [
                .. configuredCycle
                    .Where(d => updatedActiveById.ContainsKey(d.Id))
                    .Select(d => new CycleDevice { Id = d.Id, Name = updatedActiveById[d.Id].FriendlyName, StableId = d.StableId })
            ];
        }

        private static void DisposeDevices(IEnumerable<MMDevice> activeDevices)
        {
            foreach (MMDevice device in activeDevices)
            {
                DisposeForCleanup(device, nameof(DisposeDevices), "active-device-refresh");
            }
        }

        private static int ResolveCycleTargetIndex(int currentIndex, int cycleCount, bool reverse)
        {
            if (cycleCount <= 0)
            {
                return -1;
            }

            if (currentIndex < 0)
            {
                return reverse ? cycleCount - 1 : 0;
            }

            return reverse
                ? (currentIndex - 1 + cycleCount) % cycleCount
                : (currentIndex + 1) % cycleCount;
        }

        public void MediaPlayPause()
        {
            long started = Stopwatch.GetTimestamp();
            bool success = MediaKeyHelper.TryPressPlayPause();
            RecordCliActionHistory(
                ExecutionHistoryKind.Media,
                "media-play-pause",
                success,
                skipped: false,
                success ? "Dispatched Play/Pause media command." : "Failed to dispatch Play/Pause media command.",
                success ? null : "Media command send failed.",
                target: "PlayPause",
                diagCode: success ? "media-command-dispatched" : "media-command-send-failed",
                elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                details: new Dictionary<string, string>
                {
                    ["command"] = "PlayPause",
                    ["mode"] = "headless-send-input",
                });
        }

        public void MediaNextTrack()
        {
            long started = Stopwatch.GetTimestamp();
            bool success = MediaKeyHelper.TryPressNextTrack();
            RecordCliActionHistory(
                ExecutionHistoryKind.Media,
                "media-next-track",
                success,
                skipped: false,
                success ? "Dispatched Next Track media command." : "Failed to dispatch Next Track media command.",
                success ? null : "Media command send failed.",
                target: "NextTrack",
                diagCode: success ? "media-command-dispatched" : "media-command-send-failed",
                elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                details: new Dictionary<string, string>
                {
                    ["command"] = "NextTrack",
                    ["mode"] = "headless-send-input",
                });
        }

        public void MediaPreviousTrack()
        {
            long started = Stopwatch.GetTimestamp();
            bool success = MediaKeyHelper.TryPressPreviousTrack();
            RecordCliActionHistory(
                ExecutionHistoryKind.Media,
                "media-previous-track",
                success,
                skipped: false,
                success ? "Dispatched Previous Track media command." : "Failed to dispatch Previous Track media command.",
                success ? null : "Media command send failed.",
                target: "PreviousTrack",
                diagCode: success ? "media-command-dispatched" : "media-command-send-failed",
                elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                details: new Dictionary<string, string>
                {
                    ["command"] = "PreviousTrack",
                    ["mode"] = "headless-send-input",
                });
        }

        private readonly MediaSeekService _mediaSeekService = new(logger: Logger.Instance);
        private readonly MediaPlaybackService _mediaPlaybackService = new(logger: Logger.Instance);

        public async Task<MediaPlaybackResult> MediaSetPlayingAsync(bool playing)
        {
            long started = Stopwatch.GetTimestamp();
            MediaPlaybackResult result = await _mediaPlaybackService.SetPlayingAsync(playing).ConfigureAwait(false);
            if (result.RequestSent) _mediaSeekService.InvalidatePosition();
            Logger.Instance.Log(result.Success ? LogLevel.Debug : LogLevel.Info, "MediaPlaybackService",
                () => FormattableString.Invariant($"media-playback-result | code={result.Code} requested={result.RequestedState} observed={result.ObservedState ?? "unknown"} requestSent={result.RequestSent} confirmed={result.Confirmed} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} source=cli-headless"));
            RecordCliActionHistory(ExecutionHistoryKind.Media, playing ? "media-play" : "media-pause",
                result.Success, skipped: result.Success && !result.RequestSent, result.Message, result.Success ? null : result.Message,
                diagCode: result.Code, elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }

        public async Task<MediaSeekResult> MediaSeekAsync(bool backward, int? seconds)
        {
            int step = seconds ?? MediaSeekStep.Normalize(SettingsService.LoadSettings().Hotkeys.Media.SeekStepSeconds);
            if (step is < 1 or > MediaSeekStep.MaximumSeconds)
            {
                return new(false, "media-seek-invalid-step", "Use a seek step between 1 and 3600 seconds.");
            }

            long started = Stopwatch.GetTimestamp();
            MediaSeekResult result = await _mediaSeekService.SeekAsync(backward ? -step : step).ConfigureAwait(false);
            Logger.Instance.Log(result.Success ? LogLevel.Debug : LogLevel.Info, "MediaSeekService",
                () => FormattableString.Invariant($"media-seek-result | code={result.Code} offsetSeconds={(backward ? -step : step)} previousSeconds={result.PreviousPositionSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "none"} targetSeconds={result.TargetPositionSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "none"} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} source=cli-headless"));
            RecordCliActionHistory(ExecutionHistoryKind.Media, backward ? "media-seek-backward" : "media-seek-forward",
                result.Success, skipped: false, result.Message, result.Success ? null : result.Message,
                diagCode: result.Code, elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }

        public async Task<string> GetMediaStatusAsync(bool jsonOutput, bool redactOutput)
        {
            MediaOverlaySessionSnapshot snapshot = await new MediaOverlayCommandService()
                .GetCurrentMediaSnapshotAsync();

            return CliOutputFormatter.FormatMediaStatus(snapshot, jsonOutput, redactOutput);
        }

        public bool ToggleMuteMic()
        {
            bool? current = TryGetDefaultCaptureMute();
            if (!current.HasValue)
            {
                return false;
            }

            return SetMuteMic(!current.Value);
        }

        public bool SetMuteMic(bool enabled)
        {
            try
            {
                bool success = SetMuteMicCore(enabled);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-mic-on" : "mute-mic-off", success, skipped: false, success ? $"Microphone mute {(enabled ? "enabled" : "disabled")}." : "Failed to set microphone mute.", success ? null : "Failed to set microphone mute.", target: "mic", enabled: success ? enabled : null);
                return success;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetMuteMic), "cli-mute-mic-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-mic-on" : "mute-mic-off", success: false, skipped: false, summary: "Failed to set microphone mute.", reason: "Failed to set microphone mute.", target: "mic");
                return false;
            }
        }

        public bool ToggleMuteSound()
        {
            bool? current = TryGetDefaultPlaybackMute();
            if (!current.HasValue)
            {
                return false;
            }

            return SetMuteSound(!current.Value);
        }

        public bool SetMuteSound(bool enabled)
        {
            try
            {
                bool success = SetMuteSoundCore(enabled);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-sound-on" : "mute-sound-off", success, skipped: false, success ? $"Playback mute {(enabled ? "enabled" : "disabled")}." : "Failed to set playback mute.", success ? null : "Failed to set playback mute.", target: "sound", enabled: success ? enabled : null);
                return success;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetMuteSound), "cli-mute-sound-failed", ex);
                RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "mute-sound-on" : "mute-sound-off", success: false, skipped: false, summary: "Failed to set playback mute.", reason: "Failed to set playback mute.", target: "sound");
                return false;
            }
        }

        public bool ToggleDeafen()
        {
            bool? playbackMuted = TryGetDefaultPlaybackMute();
            bool? micMuted = TryGetDefaultCaptureMute();

            if (!playbackMuted.HasValue || !micMuted.HasValue)
            {
                return false;
            }

            bool next = !(playbackMuted.Value && micMuted.Value);
            return SetDeafen(next);
        }

        public bool SetDeafen(bool enabled)
        {
            bool success = SetMuteMicCore(enabled) && SetMuteSoundCore(enabled);
            RecordCliActionHistory(ExecutionHistoryKind.Mute, enabled ? "deafen-on" : "deafen-off", success, skipped: false, success ? $"Deafen {(enabled ? "enabled" : "disabled")}." : "Failed to set deafen.", success ? null : "Failed to set deafen.", target: "deafen", enabled: success ? enabled : null);
            return success;
        }

        /// <summary>
        /// Toggles the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        public bool ToggleListenToInput()
        {
            try
            {
                if (_audioOverrides?.TryToggleListenToInput != null)
                {
                    return _audioOverrides.TryToggleListenToInput();
                }

                return AudioService.TryToggleCurrentInputListenState(out _, out _);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(ToggleListenToInput), "cli-listen-toggle-failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Sets the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        /// <remarks>
        /// This call is idempotent for CLI use: if the state is already applied, the operation still succeeds.
        /// </remarks>
        public bool SetListenToInput(bool enabled)
        {
            try
            {
                if (_audioOverrides?.TrySetListenToInput != null)
                {
                    return _audioOverrides.TrySetListenToInput(enabled);
                }

                return AudioService.TrySetCurrentInputListenState(enabled, out _, out _);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(SetListenToInput), "cli-listen-set-failed", ex);
                return false;
            }
        }

        public (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput, bool redactOutput = false) =>
            ExecuteVolume(playback, deviceId, null, jsonOutput, redactOutput, false);

        public (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput, bool redactOutput = false, bool relative = false) =>
            ExecuteVolume(playback, deviceId, percent, jsonOutput, redactOutput, relative);

        private (bool Success, string Output) ExecuteVolume(bool playback, string? selector, float? percent, bool json, bool redact, bool relative)
        {
            string kind = playback ? "master" : "mic";
            string failure = percent == null ? "volume-get-failed" : relative ? "volume-adjust-failed" : "volume-set-failed";
            string normalized = selector?.Trim() ?? string.Empty;
            if (!CliVolumeOperation.TryResolve(playback, normalized, () => playback ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos(), out string? id, out string? error))
                return (false, CliOutputFormatter.FormatVolumeError(kind, failure, error ?? "Failed to select volume endpoint.", json, normalized, redact));
            if (relative) id ??= GetCurrentDeviceId(playback);
            return CliVolumeOperation.Execute(playback, id, percent, relative, json, redact,
                (out value, out muted) => TryGetEndpointVolume(playback, id, out value, out muted),
                (value, out applied, out muted) => TrySetEndpointVolume(playback, id, value, out applied, out muted),
                () =>
                {
                    string description = playback ? "playback" : "recording";
                    if (HasDefaultEndpoint(playback, id)) return $"Failed to {(percent == null ? "read" : "set")} {description} volume.";
                    return string.IsNullOrWhiteSpace(id) ? $"No default {description} device is available."
                        : $"{char.ToUpperInvariant(description[0])}{description[1..]} device '{id}' is not available.";
                });
        }

        public string GetListenStatus(bool jsonOutput, bool redactOutput)
        {
            (bool? Enabled, string? MonitorTargetOutputDeviceName)? statusSnapshot = _audioOverrides?.GetListenStatusSnapshot?.Invoke();

            bool? currentInputListenEnabled = statusSnapshot?.Enabled;
            if (!currentInputListenEnabled.HasValue && AudioService.TryGetCurrentInputListenState(out bool enabled, out _))
            {
                currentInputListenEnabled = enabled;
            }

            string? listenMonitorTargetOutputDeviceName = statusSnapshot?.MonitorTargetOutputDeviceName;
            if (listenMonitorTargetOutputDeviceName == null)
            {
                AudioService.TryGetCurrentInputListenTargetOutputDeviceName(out listenMonitorTargetOutputDeviceName, out _);
            }

            return CliOutputFormatter.FormatListenStatus(currentInputListenEnabled, listenMonitorTargetOutputDeviceName, jsonOutput, redactOutput);
        }

        public string GetMuteStatus(string target, bool jsonOutput)
        {
            bool enabled = target switch
            {
                "mic" => TryGetDefaultCaptureMute() ?? false,
                "sound" => TryGetDefaultPlaybackMute() ?? false,
                "deafen" => (TryGetDefaultPlaybackMute() ?? false) && (TryGetDefaultCaptureMute() ?? false),
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown mute target."),
            };

            return CliOutputFormatter.FormatMuteStatus(target, enabled, jsonOutput);
        }

        private bool SetMuteMicCore(bool enabled)
        {
            if (_audioOverrides?.TrySetMicrophoneMute != null)
            {
                return _audioOverrides.TrySetMicrophoneMute(enabled);
            }

            return AudioService.SetMicrophoneMute(enabled).FullySucceeded;
        }

        private bool SetMuteSoundCore(bool enabled)
        {
            if (_audioOverrides?.TrySetPlaybackMute != null)
            {
                return _audioOverrides.TrySetPlaybackMute(enabled);
            }

            return AudioService.SetPlaybackMute(enabled).FullySucceeded;
        }

        private bool HasDefaultInputDevice()
        {
            try
            {
                if (_audioOverrides?.HasDefaultInputDevice != null)
                {
                    return _audioOverrides.HasDefaultInputDevice();
                }

                using var device = AudioService.GetDefaultRecordingDevice();
                return device != null;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(HasDefaultInputDevice), "cli-default-input-device-check-failed", ex);
                return false;
            }
        }

        private bool HasDefaultEndpoint(bool playback, string? deviceId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        return targetDevice != null;
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    return targetInputDevice != null;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    return device != null;
                }

                if (_audioOverrides?.HasDefaultInputDevice != null)
                {
                    return _audioOverrides.HasDefaultInputDevice();
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                return inputDevice != null;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(HasDefaultEndpoint), playback ? "cli-default-playback-endpoint-check-failed" : "cli-default-capture-endpoint-check-failed", ex);
                return false;
            }
        }

        private bool TryGetEndpointVolume(bool playback, string? deviceId, out float percent, out bool muted)
        {
            percent = 0f;
            muted = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback && _audioOverrides?.GetPlaybackVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.GetPlaybackVolumeByDeviceId(deviceId);
                        percent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (!playback && _audioOverrides?.GetCaptureVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.GetCaptureVolumeByDeviceId(deviceId);
                        percent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        return AppCliOverlayCoordinator.TryGetEndpointVolumeState(Logger.Instance, targetDevice, "cli-volume-get:master:device", out percent, out muted);
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    return AppCliOverlayCoordinator.TryGetEndpointVolumeState(Logger.Instance, targetInputDevice, "cli-volume-get:mic:device", out percent, out muted);
                }

                if (playback && _audioOverrides?.GetDefaultPlaybackVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.GetDefaultPlaybackVolume();
                    percent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (!playback && _audioOverrides?.GetDefaultCaptureVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.GetDefaultCaptureVolume();
                    percent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    return AppCliOverlayCoordinator.TryGetEndpointVolumeState(Logger.Instance, device, "cli-volume-get:master", out percent, out muted);
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                return AppCliOverlayCoordinator.TryGetEndpointVolumeState(Logger.Instance, inputDevice, "cli-volume-get:mic", out percent, out muted);
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetEndpointVolume), playback ? "cli-volume-get-playback-failed" : "cli-volume-get-capture-failed", ex);
                return false;
            }
        }

        private bool TrySetEndpointVolume(bool playback, string? deviceId, float targetPercent, out float appliedPercent, out bool muted)
        {
            appliedPercent = 0f;
            muted = false;

            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (playback && _audioOverrides?.TrySetPlaybackVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.TrySetPlaybackVolumeByDeviceId(deviceId, targetPercent);
                        appliedPercent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (!playback && _audioOverrides?.TrySetCaptureVolumeByDeviceId != null)
                    {
                        var (Success, Percent, Muted) = _audioOverrides.TrySetCaptureVolumeByDeviceId(deviceId, targetPercent);
                        appliedPercent = Percent;
                        muted = Muted;
                        return Success;
                    }

                    if (playback)
                    {
                        using var targetDevice = AudioService.TryGetPlaybackDeviceById(deviceId);
                        if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(Logger.Instance, targetDevice, targetPercent, "cli-volume-set:master:device", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                        {
                            return false;
                        }

                        muted = appliedPercent <= 0f;
                        return true;
                    }

                    using var targetInputDevice = AudioService.TryGetCaptureDeviceById(deviceId);
                    if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(Logger.Instance, targetInputDevice, targetPercent, "cli-volume-set:mic:device", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                    {
                        return false;
                    }

                    muted = appliedPercent <= 0f;
                    return true;
                }

                if (playback && _audioOverrides?.TrySetPlaybackVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.TrySetPlaybackVolume(targetPercent);
                    appliedPercent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (!playback && _audioOverrides?.TrySetCaptureVolume != null)
                {
                    var (Success, Percent, Muted) = _audioOverrides.TrySetCaptureVolume(targetPercent);
                    appliedPercent = Percent;
                    muted = Muted;
                    return Success;
                }

                if (playback)
                {
                    using var device = AudioService.GetDefaultPlaybackDevice();
                    if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(Logger.Instance, device, targetPercent, "cli-volume-set:master", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                    {
                        return false;
                    }

                    muted = appliedPercent <= 0f;
                    return true;
                }

                using var inputDevice = AudioService.GetDefaultRecordingDevice();
                if (!AppCliOverlayCoordinator.TryApplyEndpointVolume(Logger.Instance, inputDevice, targetPercent, "cli-volume-set:mic", muteAtZero: true, unmuteAboveZero: true, out appliedPercent))
                {
                    return false;
                }

                muted = appliedPercent <= 0f;
                return true;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TrySetEndpointVolume), playback ? "cli-volume-set-playback-failed" : "cli-volume-set-capture-failed", ex);
                return false;
            }
        }

        private bool? TryGetDefaultPlaybackMute()
        {
            try
            {
                if (_audioOverrides?.GetDefaultPlaybackMute != null)
                {
                    return _audioOverrides.GetDefaultPlaybackMute();
                }

                using var device = AudioService.GetDefaultPlaybackDevice();
                return device?.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetDefaultPlaybackMute), "cli-default-playback-mute-read-failed", ex);
                return null;
            }
        }

        private bool? TryGetDefaultCaptureMute()
        {
            try
            {
                if (_audioOverrides?.GetDefaultCaptureMute != null)
                {
                    return _audioOverrides.GetDefaultCaptureMute();
                }

                using var device = AudioService.GetDefaultRecordingDevice();
                return device?.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                LogAudioOperationFailure(nameof(TryGetDefaultCaptureMute), "cli-default-capture-mute-read-failed", ex);
                return null;
            }
        }

        private static void LogAudioOperationFailure(string operation, string eventName, Exception ex)
        {
            Logger.Instance?.Debug(
                "LocalHeadlessCommandRunner",
                () => $"{eventName} | exceptionType={ex.GetType().Name}",
                operation);
        }
    }
}

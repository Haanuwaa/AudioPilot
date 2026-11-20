using System.Diagnostics;
using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner
    {
        public Task<CliExecutionResult> SwitchToDeviceAsync(CliCommand command)
        {
            bool output = command.Action == CliAction.SwitchOutputToDevice;
            Settings settings = LoadSettings();
            return CliDirectDeviceSwitch.ExecuteAsync(command,
                () =>
                {
                    AudioService.KeepMuteStateWhenSwitching = settings.DeviceSwitching.KeepMuteStateWhenSwitching;
                    return output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
                },
                () => GetCurrentDeviceId(output),
                async target =>
                {
                    string opId = $"cli-direct:{Guid.NewGuid():N}";
                    bool success;
                    if (output)
                    {
                        bool muteMic = command.MuteMic;
                        bool muteSound = command.MuteSound;
                        bool deafen = command.Deafen;
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
            Settings settings = LoadSettings();
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycle = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            return CliOutputFormatter.FormatCycleList(kind, cycle, jsonOutput, redactOutput);
        }

        public (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput)
        {
            Settings settings = LoadSettings();
            string kind = output ? "output" : "input";
            IReadOnlyList<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            var result = SettingsValidationService.ValidateCycle(cycleDevices, activeDevices);
            string formatted = CliOutputFormatter.FormatCycleValidation(kind, result.DuplicateDeviceNames, result.DisconnectedDeviceNames, jsonOutput, redactOutput);
            return (result.IsValid, formatted);
        }

        public (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput)
        {
            Settings settings = LoadSettings();
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
            Settings settings = LoadSettings();
            var cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
            var activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
            string kind = output ? "output" : "input";

            var snapshot = AppSwitchCycleStateResolver.BuildCycleSnapshotState([.. cycleDevices], activeDevices);
            var resolvedCycle = snapshot.ConnectedCycle.Concat(snapshot.SkippedDevices).ToList();
            var preflight = SettingsValidationService.EvaluateCycleSwitchPreflight(
                resolvedCycle,
                activeDevices,
                hasDefaultInputDevice: HasDefaultInputDevice(),
                output);

            if (!preflight.CanSwitch)
            {
                return (false, CliOutputFormatter.FormatCycleTest(kind, preflight.ConfiguredCount, preflight.ConnectedConfiguredCount, preflight.HasDefaultInputDevice, preflight.Reasons, jsonOutput, redactOutput));
            }

            string? currentId = GetCurrentDeviceId(output);
            int currentIndex = snapshot.ConnectedCycle.FindIndex(device => string.Equals(device.Id, currentId, StringComparison.OrdinalIgnoreCase));
            int targetIndex = AppSwitchCycleStateResolver.ResolveCycleTargetIndex(currentIndex, snapshot.ConnectedCycle.Count, reverse);
            return (true, CliOutputFormatter.FormatSwitchPreview(kind, currentId, snapshot.ConnectedCycle[targetIndex], jsonOutput, redactOutput));
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
            int timeoutMs = result.ShouldWaitForReadiness
                ? RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs
                : RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs;
            var clock = Stopwatch.StartNew();
            bool ready = await WaitForConditionAsync(isReady, timeoutMs,
                RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs,
                usePollingFallbackWhenOverridesPresent: true);
            Logger.Instance.Info("BluetoothReconnect", () => $"bluetooth-reconnect-cli-readiness | opId={opId} requestAccepted={result.RequestAccepted} connected={result.Connected} ready={ready} elapsedMs={clock.ElapsedMilliseconds} timeoutMs={timeoutMs}");
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
                Settings settings = LoadSettings();
                List<CycleDevice> cycleDevices = output ? settings.DeviceSwitching.Output.CycleDevices : settings.DeviceSwitching.Input.CycleDevices;
                List<CycleDevice> activeDevices = output ? GetActiveOutputDeviceInfos() : GetActiveInputDeviceInfos();
                string kind = output ? "output" : "input";
                var result = CliCycleManager.ApplyMutation(cycleDevices, activeDevices, kind, action, deviceId, orderedDeviceIds, jsonOutput, redactOutput);
                if (!result.Success)
                {
                    return result;
                }

                SettingsService.SaveSettings(settings);
                return result;
            }
            catch
            {
                return (false, CliCycleManager.FormatMutationFailure(action, "Failed to persist cycle changes.", jsonOutput));
            }
        }

        public async ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse)
        {
            string? beforeDeviceName = TryGetCurrentDefaultDeviceName(output: true);
            Settings settings = LoadSettings();
            AudioService.KeepMuteStateWhenSwitching = settings.DeviceSwitching.KeepMuteStateWhenSwitching;
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
                var activeIds = new HashSet<string>(activeDevices.Select(device => device.ID), StringComparer.OrdinalIgnoreCase);
                List<CycleDevice> availableIdentities = [.. activeDevices.Select(device => new CycleDevice
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    StableId = AudioEndpointIdentity.TryGetStableId(device),
                })];
                var snapshot = AppSwitchCycleStateResolver.BuildCycleSnapshotState(configuredCycle, availableIdentities);
                List<CycleDevice> connectedCycle = snapshot.ConnectedCycle;
                List<CycleDevice> skippedCycle = snapshot.SkippedDevices;

                if (connectedCycle.Count <= 1)
                {
                    BluetoothReconnectOptions reconnectOptions = BluetoothReconnectOptions.FromSettings(settings);
                    BluetoothReconnectAttemptResult reconnectResult = await BluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                        skippedCycle,
                        activeIds,
                        BluetoothReconnectDeviceKind.Output,
                        reconnectOptions,
                        opId: "cli");

                    if (reconnectResult.Attempted)
                    {
                        await WaitForReconnectReadinessAsync(
                            () => AppSwitchCycleStateResolver.BuildCycleSnapshotState(configuredCycle, GetActiveOutputDeviceInfos()).ConnectedCycle.Count > 1,
                            reconnectResult,
                            "cli-output");
                        RefreshActiveCycleDevices(output: true, ref activeDevices, ref connectedCycle, configuredCycle);
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
                int targetIndex = AppSwitchCycleStateResolver.ResolveCycleTargetIndex(currentIndex, connectedCycle.Count, reverse);
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
            Settings settings = LoadSettings();
            AudioService.KeepMuteStateWhenSwitching = settings.DeviceSwitching.KeepMuteStateWhenSwitching;
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
                var activeIds = new HashSet<string>(activeDevices.Select(device => device.ID), StringComparer.OrdinalIgnoreCase);
                List<CycleDevice> availableIdentities = [.. activeDevices.Select(device => new CycleDevice
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    StableId = AudioEndpointIdentity.TryGetStableId(device),
                })];
                var snapshot = AppSwitchCycleStateResolver.BuildCycleSnapshotState(configuredCycle, availableIdentities);
                List<CycleDevice> connectedCycle = snapshot.ConnectedCycle;
                List<CycleDevice> skippedCycle = snapshot.SkippedDevices;

                if (connectedCycle.Count <= 1)
                {
                    BluetoothReconnectOptions reconnectOptions = BluetoothReconnectOptions.FromSettings(settings);
                    BluetoothReconnectAttemptResult reconnectResult = await BluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                        skippedCycle,
                        activeIds,
                        BluetoothReconnectDeviceKind.Input,
                        reconnectOptions,
                        opId: "cli");

                    if (reconnectResult.Attempted)
                    {
                        await WaitForReconnectReadinessAsync(
                            () => AppSwitchCycleStateResolver.BuildCycleSnapshotState(configuredCycle, GetActiveInputDeviceInfos()).ConnectedCycle.Count > 1,
                            reconnectResult,
                            "cli-input");
                        RefreshActiveCycleDevices(output: false, ref activeDevices, ref connectedCycle, configuredCycle);
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
                int targetIndex = AppSwitchCycleStateResolver.ResolveCycleTargetIndex(currentIndex, connectedCycle.Count, reverse);
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

        private void RefreshActiveCycleDevices(
            bool output,
            ref List<MMDevice> activeDevices,
            ref List<CycleDevice> connectedCycle,
            IReadOnlyList<CycleDevice> configuredCycle)
        {
            DisposeDevices(activeDevices);
            activeDevices = [.. (output ? AudioService.GetActivePlaybackDevices() : AudioService.GetActiveCaptureDevices()).Cast<MMDevice>()];
            connectedCycle = AppSwitchCycleStateResolver.BuildCycleSnapshotState(configuredCycle,
                [.. activeDevices.Select(device => new CycleDevice
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    StableId = AudioEndpointIdentity.TryGetStableId(device),
                })]).ConnectedCycle;
        }

        private static void DisposeDevices(IEnumerable<MMDevice> activeDevices)
        {
            foreach (MMDevice device in activeDevices)
            {
                DisposeForCleanup(device, nameof(DisposeDevices), "active-device-refresh");
            }
        }
    }
}

using System.Diagnostics;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators;

internal enum DirectDeviceSwitchOutcome { Succeeded, Failed, Unavailable, Canceled }

internal sealed partial class AppSwitchCommandCoordinator
{
    public ValueTask<DirectDeviceSwitchOutcome> SwitchOutputToDeviceAsync(
        CycleDevice target,
        bool muteMic,
        bool muteSound,
        bool deafen,
        bool preserveAudioLevels,
        BluetoothReconnectOptions reconnectOptions,
        Action<string> schedulePostSwitchRefresh)
    {
        return ExecuteDirectTargetAsync(
            target,
            output: true,
            reconnectOptions,
            _directOutputGate,
            _outputIntentTracker,
            async (resolvedTarget, opId, token) =>
            {
                (bool success, string? deviceName) = await _audio.SwitchAudioDeviceAsync(
                    resolvedTarget.Id,
                    muteMic,
                    muteSound,
                    deafen,
                    preserveAudioLevels,
                    opId: opId,
                    cancellationToken: token);
                token.ThrowIfCancellationRequested();
                if (success)
                {
                    string resolvedName = string.IsNullOrWhiteSpace(deviceName) ? resolvedTarget.Name : deviceName;
                    _overlay.Show(OverlayDeviceKind.Output, "Switched output device", resolvedName);
                    schedulePostSwitchRefresh(opId);
                }
                else
                {
                    _overlay.Show(OverlayDeviceKind.Error, "Failed to switch output device", resolvedTarget.Name);
                }
                return success;
            });
    }

    public ValueTask<DirectDeviceSwitchOutcome> SwitchInputToDeviceAsync(
        CycleDevice target,
        bool preserveAudioLevels,
        BluetoothReconnectOptions reconnectOptions)
    {
        return ExecuteDirectTargetAsync(
            target,
            output: false,
            reconnectOptions,
            _directInputGate,
            _inputIntentTracker,
            async (resolvedTarget, opId, token) =>
            {
                (bool success, _) = await _audio.SwitchInputDeviceToAsync(
                    resolvedTarget.Id,
                    resolvedTarget.Name,
                    preserveAudioLevels,
                    (kind, title, message) => { if (!token.IsCancellationRequested) _overlay.Show(kind, title, message); },
                    opId, cancellationToken: token);
                return success;
            });
    }

    private async ValueTask<DirectDeviceSwitchOutcome> ExecuteDirectTargetAsync(
        CycleDevice target,
        bool output,
        BluetoothReconnectOptions reconnectOptions,
        SemaphoreSlim gate,
        AppSwitchIntentTracker intentTracker,
        Func<CycleDevice, string, CancellationToken, ValueTask<bool>> switchAsync)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Volatile.Read(ref _disposeStarted) != 0) return DirectDeviceSwitchOutcome.Canceled;
        if (string.IsNullOrWhiteSpace(target.Id)) return DirectDeviceSwitchOutcome.Unavailable;

        string opId = $"direct:{Guid.NewGuid():N}";
        long started = Stopwatch.GetTimestamp();
        DirectDeviceSwitchOutcome outcome = DirectDeviceSwitchOutcome.Failed;
        using var traceContext = OperationTrace.Attach(OperationTrace.CurrentId ?? opId);
        (_, CancellationToken token) = intentTracker.Begin();
        try
        {
            await gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                CycleDevice? ResolveTarget() => (output
                    ? _audio.TryGetActivePlaybackCycleEntry(target.Id, target.Name)
                    : _audio.TryGetActiveRecordingCycleEntry(target.Id, target.Name))
                    ?? PersistedAudioDeviceResolver.TryResolveMatch(target,
                        output ? _audio.GetActivePlaybackCycleEntries() : _audio.GetActiveCaptureCycleEntries());
                CycleDevice? resolved = ResolveTarget();

                if (resolved == null)
                {
                    BluetoothReconnectAttemptResult reconnect = default;
                    if (reconnectOptions.Enabled && (!reconnectOptions.OnlyLikelyBluetoothEndpoints
                        || BluetoothReconnectCoordinator.IsLikelyBluetoothEndpoint(target)))
                    {
                        IReadOnlyCollection<string> activeIds = output
                            ? _audio.GetActivePlaybackCycleEntries().Select(static device => device.Id).ToArray()
                            : [.. _audio.GetActiveCaptureCycleEntries().Select(static device => device.Id)];
                        reconnect = await _bluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                            [target.Clone()],
                            activeIds,
                            output ? BluetoothReconnectDeviceKind.Output : BluetoothReconnectDeviceKind.Input,
                            reconnectOptions,
                            opId,
                            onAttemptProgress: (_, _, name) =>
                            {
                                if (!token.IsCancellationRequested)
                                    _overlay.Show(output ? OverlayDeviceKind.Output : OverlayDeviceKind.Input,
                                        output ? "Reconnecting output device" : "Reconnecting input device", name);
                            },
                            cancellationToken: token);
                    }

                    token.ThrowIfCancellationRequested();
                    if (reconnect.ShouldWaitForReadiness)
                    {
                        await BluetoothReconnectReadiness.WaitAsync(
                            () => (resolved = ResolveTarget()) != null,
                            reconnect,
                            handler => _audio.DeviceStateChanged += handler,
                            handler => _audio.DeviceStateChanged -= handler,
                            token);
                    }
                    else
                    {
                        resolved = ResolveTarget();
                    }
                }

                token.ThrowIfCancellationRequested();
                if (resolved == null)
                {
                    _overlay.Show(
                        OverlayDeviceKind.Error,
                        output ? "Output device unavailable" : "Input device unavailable",
                        target.Name);
                    return outcome = DirectDeviceSwitchOutcome.Unavailable;
                }

                bool success = await switchAsync(resolved, opId, token);
                token.ThrowIfCancellationRequested();
                return outcome = success ? DirectDeviceSwitchOutcome.Succeeded : DirectDeviceSwitchOutcome.Failed;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return outcome = DirectDeviceSwitchOutcome.Canceled;
        }
        finally
        {
            _logger.Log(outcome is DirectDeviceSwitchOutcome.Failed or DirectDeviceSwitchOutcome.Unavailable ? LogLevel.Warning : LogLevel.Info,
                "AppViewModel", () => $"direct-device-switch-completed | opId={opId} kind={(output ? "output" : "input")} outcome={outcome.ToString().ToLowerInvariant()} success={outcome == DirectDeviceSwitchOutcome.Succeeded} target={LogPrivacy.Device(target.Name)} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }
    }
}

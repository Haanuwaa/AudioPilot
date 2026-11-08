using System.Diagnostics;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators;

internal sealed partial class AppSwitchCommandCoordinator
{
    public ValueTask<bool> SwitchOutputToDeviceAsync(
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

    public ValueTask<bool> SwitchInputToDeviceAsync(
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

    private async ValueTask<bool> ExecuteDirectTargetAsync(
        CycleDevice target,
        bool output,
        BluetoothReconnectOptions reconnectOptions,
        SemaphoreSlim gate,
        AppSwitchIntentTracker intentTracker,
        Func<CycleDevice, string, CancellationToken, ValueTask<bool>> switchAsync)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.Id) || Volatile.Read(ref _disposeStarted) != 0)
        {
            return false;
        }

        (_, CancellationToken token) = intentTracker.Begin();
        try
        {
            await gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                string opId = $"direct:{Guid.NewGuid():N}";
                using var traceContext = OperationTrace.Attach(OperationTrace.CurrentId ?? opId);
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
                    resolved = ResolveTarget();
                    if (reconnect.ShouldWaitForReadiness)
                    {
                        long started = Stopwatch.GetTimestamp();
                        while (resolved == null && Stopwatch.GetElapsedTime(started).TotalMilliseconds
                            < RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs)
                        {
                            await Task.Delay(RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs, token);
                            resolved = ResolveTarget();
                        }
                    }
                }

                token.ThrowIfCancellationRequested();
                if (resolved == null)
                {
                    _overlay.Show(
                        OverlayDeviceKind.Error,
                        output ? "Output device unavailable" : "Input device unavailable",
                        target.Name);
                    return false;
                }

                bool success = await switchAsync(resolved, opId, token);
                token.ThrowIfCancellationRequested();
                _logger.Info("AppViewModel", () =>
                    $"direct-device-switch-completed | opId={opId} kind={(output ? "output" : "input")} success={success} target={LogPrivacy.Device(resolved.Name)}");
                return success;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }
}

using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

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
            next => ReplaceDirectRequest(ref _directOutputCts, next),
            async (resolvedTarget, opId) =>
            {
                using MMDevice? current = _audio.GetDefaultPlaybackDevice();
                string currentId = current?.ID ?? resolvedTarget.Id;
                (bool success, string? deviceName) = await _audio.SwitchAudioDeviceAsync(
                    currentId,
                    resolvedTarget.Id,
                    muteMic,
                    muteSound,
                    deafen,
                    preserveAudioLevels,
                    opId: opId);
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
            next => ReplaceDirectRequest(ref _directInputCts, next),
            async (resolvedTarget, opId) =>
            {
                (bool success, _) = await _audio.SwitchInputDeviceToAsync(
                    resolvedTarget.Id,
                    resolvedTarget.Name,
                    preserveAudioLevels,
                    (kind, title, message) => _overlay.Show(kind, title, message),
                    opId);
                return success;
            });
    }

    private async ValueTask<bool> ExecuteDirectTargetAsync(
        CycleDevice target,
        bool output,
        BluetoothReconnectOptions reconnectOptions,
        SemaphoreSlim gate,
        Action<CancellationTokenSource> publishRequest,
        Func<CycleDevice, string, ValueTask<bool>> switchAsync)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.Id) || Volatile.Read(ref _disposeStarted) != 0)
        {
            return false;
        }

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(GetLifetimeCancellationToken());
        publishRequest(requestCts);
        CancellationToken token = requestCts.Token;
        try
        {
            await gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                string opId = $"direct:{Guid.NewGuid():N}";
                CycleDevice? resolved = output
                    ? _audio.TryGetActivePlaybackCycleEntry(target.Id, target.Name)
                    : _audio.TryGetActiveRecordingCycleEntry(target.Id, target.Name);

                if (resolved == null)
                {
                    _overlay.Show(
                        output ? OverlayDeviceKind.Output : OverlayDeviceKind.Input,
                        output ? "Reconnecting output device" : "Reconnecting input device",
                        target.Name);
                    IReadOnlyCollection<string> activeIds = output
                        ? _audio.GetActivePlaybackCycleEntries().Select(static device => device.Id).ToArray()
                        : [.. _audio.GetActiveCaptureCycleEntries().Select(static device => device.Id)];
                    await _bluetoothReconnectCoordinator.TryReconnectDetailedAsync(
                        [target.Clone()],
                        activeIds,
                        output ? BluetoothReconnectDeviceKind.Output : BluetoothReconnectDeviceKind.Input,
                        reconnectOptions,
                        opId,
                        cancellationToken: token);

                    DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(
                        Math.Max(500, RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs));
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        resolved = output
                            ? _audio.TryGetActivePlaybackCycleEntry(target.Id, target.Name)
                            : _audio.TryGetActiveRecordingCycleEntry(target.Id, target.Name);
                        if (resolved != null)
                        {
                            break;
                        }
                        await Task.Delay(RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs, token);
                    }
                    while (DateTime.UtcNow < deadlineUtc);
                }

                if (resolved == null)
                {
                    _overlay.Show(
                        OverlayDeviceKind.Error,
                        output ? "Failed to switch output device" : "Failed to switch input device",
                        target.Name);
                    return false;
                }

                token.ThrowIfCancellationRequested();
                bool success = await switchAsync(resolved, opId);
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
        finally
        {
            if (output)
            {
                Interlocked.CompareExchange(ref _directOutputCts, null, requestCts);
            }
            else
            {
                Interlocked.CompareExchange(ref _directInputCts, null, requestCts);
            }
        }
    }

    internal static void ReplaceDirectRequest(
        ref CancellationTokenSource? field,
        CancellationTokenSource next)
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref field, next);
        if (ReferenceEquals(previous, next))
        {
            return;
        }
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

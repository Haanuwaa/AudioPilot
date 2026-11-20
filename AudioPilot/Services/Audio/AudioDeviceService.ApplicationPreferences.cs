using System.Diagnostics;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio;

internal readonly record struct ApplicationDevicePreference(bool Available, bool Mixed, string? DeviceId);
internal sealed record ApplicationRoutingInventory(ApplicationDevicePreference Preference, IReadOnlyList<CycleDevice> Devices, long ProcessStartedTicks)
{
    internal bool IsComplete => Devices is not AudioDeviceInventory { IsComplete: false };
}

internal readonly record struct ApplicationRoutingChange(ProcessAudioRoutingResult Result, ApplicationDevicePreference Preference, bool? MayHaveWritten = null, bool TargetUnavailable = false)
{
    internal bool OverridesRoutine => MayHaveWritten ?? Result != ProcessAudioRoutingResult.Failed;
}

internal sealed record ApplicationRoutingOperations(
    Func<uint, bool, long?, CancellationToken, Task<ApplicationRoutingInventory>> Read,
    Func<uint, bool, long, string?, CancellationToken, Task<ApplicationRoutingChange>> Change);

public partial class AudioDeviceService
{
    internal event Action<DataFlow>? ApplicationRoutingChanged;

    private void NotifyApplicationRoutingChanged(DataFlow flow)
    {
        try { ApplicationRoutingChanged?.Invoke(flow); }
        catch (Exception ex) { _logger.Warning("AudioDeviceService", $"routing-notification-failed | error={ex.GetType().Name}"); }
    }

    private static readonly Role[] ApplicationPreferenceRoles = [Role.Console, Role.Multimedia, Role.Communications];

    internal Task<ApplicationRoutingInventory> ReadApplicationRoutingAsync(uint processId, bool playback, long? expectedStartTicks,
        CancellationToken cancellationToken) => RunApplicationPreferenceWorkAsync(() =>
        {
            long started = ValidateApplicationProcess(processId, expectedStartTicks);
            var devices = ReadActiveDeviceInventory(playback);
            return new ApplicationRoutingInventory(ReadApplicationPreference(processId, playback), devices, started);
        }, cancellationToken);

    internal Task<ApplicationRoutingChange> ChangeApplicationRoutingAsync(uint processId, bool playback, long expectedStartTicks,
        string? deviceId, CancellationToken cancellationToken) => RunApplicationPreferenceWorkAsync(() =>
        {
            ValidateApplicationProcess(processId, expectedStartTicks);
            DataFlow flow = playback ? DataFlow.Render : DataFlow.Capture;
            if (deviceId != null)
            {
                using var target = playback ? TryGetPlaybackDeviceById(deviceId) : TryGetCaptureDeviceById(deviceId);
                if (target?.State != DeviceState.Active)
                    return new ApplicationRoutingChange(ProcessAudioRoutingResult.Failed, default, MayHaveWritten: false, TargetUnavailable: true);
                deviceId = target.ID;
            }
            var write = _processAudioRouter.WriteProcessPreference(processId, flow, ApplicationPreferenceRoles, deviceId);
            _logger.Info("AudioDeviceService", $"application-routing-change | flow={flow} result={write.Result}");
            if (write.MayHaveWritten) _sessionService.InvalidateRecentMixerSnapshotState();
            var change = new ApplicationRoutingChange(write.Result, ReadApplicationPreference(processId, playback), write.MayHaveWritten);
            if (write.MayHaveWritten) NotifyApplicationRoutingChanged(flow);
            return change;
        }, cancellationToken);

    internal ApplicationDevicePreference ReadApplicationPreference(uint processId, bool playback) =>
        _processAudioRouter.ReadProcessPreference(processId, playback ? DataFlow.Render : DataFlow.Capture, ApplicationPreferenceRoles);

    private static long ValidateApplicationProcess(uint processId, long? expectedStartTicks)
    {
        using var process = Process.GetProcessById(checked((int)processId));
        long started = process.StartTime.ToUniversalTime().Ticks;
        if (process.HasExited || (expectedStartTicks.HasValue && expectedStartTicks.Value != started))
            throw new InvalidOperationException("The application has exited or restarted. Refresh the mixer and try again.");
        return started;
    }

    /// <summary>Keeps on-demand preference reads and writes within the audio service's shutdown ownership.</summary>
    private Task<T> RunApplicationPreferenceWorkAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = TryRunBackgroundWork(async shutdownToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, cancellationToken);
            try
            {
                T result = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return operation();
                }, linked.Token).ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, nameof(RunApplicationPreferenceWorkAsync));
        if (!queued) completion.TrySetException(new ObjectDisposedException(nameof(AudioDeviceService)));
        return completion.Task;
    }
}

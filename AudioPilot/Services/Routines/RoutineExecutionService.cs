using System.Diagnostics;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Routines;

/// <summary>Executes a snapshot of a routine without owning UI state or application-trigger lifetimes.</summary>
internal sealed class RoutineExecutionService(RoutineExecutionOperations operations, Logger logger)
{
    private readonly record struct DeviceSwitchResult(
        bool Success, string? DeviceName, bool AwaitingAppCompletion = false, bool AppRouteApplied = false,
        string? FailureDetail = null, bool ReconnectAttempted = false, bool ReconnectSucceeded = false, IRoutineAudioChange? Change = null);

    internal async Task<RoutineExecutionResult> ExecuteAsync(
        AudioRoutine routine, RoutineExecutionOptions options, int? processId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routine);
        cancellationToken.ThrowIfCancellationRequested();
        AudioRoutine snapshot = routine.Clone();
        // Device lookup and COM-backed actions can block even when a switch completes synchronously.
        // Capture the caller's draft before dispatch; keep all execution stages off its UI context.
        return await Task.Run(() => ExecuteCoreAsync(snapshot, options, processId, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RoutineExecutionResult> ExecuteCoreAsync(
        AudioRoutine snapshot, RoutineExecutionOptions options, int? processId, CancellationToken cancellationToken)
    {
        using var trace = OperationTrace.Start("routine-execution", logger);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        if (snapshot.ValidateCommunicationsTargets() is { } targetError)
            return new(false, null, null, CommunicationsFailureDetail: targetError);
        if (!Enum.IsDefined(snapshot.OutputMuteAction) || !Enum.IsDefined(snapshot.InputMuteAction))
            return new(false, null, null, MuteFailureDetail: "Unknown routine mute action.");
        if (snapshot.Conditions.HasRequirements)
        {
            (string Code, string Reason)? condition;
            try
            {
                condition = RoutineConditionEvaluator.Evaluate(snapshot.Conditions, operations, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.Warning("RoutineExecutionService", "routine-condition-check-failed", nameof(ExecuteAsync), ex);
                condition = ("routine-condition-unavailable", "A required condition could not be checked.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (condition is { } failure)
            {
                logger.Info("RoutineExecutionService", () => $"routine-condition-skipped | routineId={LogPrivacy.Id(snapshot.Id)} code={failure.Code}");
                trace.Complete(failure.Code);
                return new(false, null, null, Skipped: true, SkipCode: failure.Code, SkipReason: failure.Reason);
            }
        }
        if (snapshot.SwitchOutputPerApp && processId is not > 0)
        {
            trace.Complete("target-app-unavailable");
            return new(false, null, null, Skipped: true,
                OutputSucceeded: snapshot.HasOutputTarget ? false : null,
                InputSucceeded: snapshot.HasInputTarget ? false : null,
                OutputFailureDetail: snapshot.HasOutputTarget ? "Target application is not running." : null,
                InputFailureDetail: snapshot.HasInputTarget ? "Target application is not running." : null);
        }
        var changes = new List<IRoutineAudioChange>(6);
        bool handedOff = false;
        try
        {
            DeviceSwitchResult? output = null;
            DeviceSwitchResult? input = null;
            if (snapshot.HasOutputTarget)
                output = await ExecuteDeviceAsync(snapshot, true, options, processId, false, cancellationToken);
            if (output?.Change != null) changes.Add(output.Value.Change);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.HasInputTarget)
                input = await ExecuteDeviceAsync(snapshot, false, options, processId, false, cancellationToken);
            if (input?.Change != null) changes.Add(input.Value.Change);
            cancellationToken.ThrowIfCancellationRequested();
            DeviceSwitchResult? communicationsOutput = snapshot.CommunicationsOutput != null
                ? await ExecuteDeviceAsync(snapshot, true, options, null, true, cancellationToken) : null;
            if (communicationsOutput?.Change != null) changes.Add(communicationsOutput.Value.Change);
            cancellationToken.ThrowIfCancellationRequested();
            DeviceSwitchResult? communicationsInput = snapshot.CommunicationsInput != null
                ? await ExecuteDeviceAsync(snapshot, false, options, null, true, cancellationToken) : null;
            if (communicationsInput?.Change != null) changes.Add(communicationsInput.Value.Change);
            cancellationToken.ThrowIfCancellationRequested();
            bool? masterVolume = snapshot.MasterVolumePercent is int master
                ? ApplyVolume(true, snapshot.OutputDeviceId, master, options.CaptureAudioRestoration, changes) : null;
            cancellationToken.ThrowIfCancellationRequested();
            bool? micVolume = snapshot.MicVolumePercent is int mic
                ? ApplyVolume(false, snapshot.InputDeviceId, mic, options.CaptureAudioRestoration, changes) : null;

            cancellationToken.ThrowIfCancellationRequested();
            RoutineMuteApplicationResult? outputMute = null;
            RoutineMuteApplicationResult? inputMute = null;
            outputMute = ApplyMute(true, snapshot.OutputDeviceId, snapshot.OutputMuteAction, output?.Success);
            cancellationToken.ThrowIfCancellationRequested();
            inputMute = ApplyMute(false, snapshot.InputDeviceId, snapshot.InputMuteAction, input?.Success);
            cancellationToken.ThrowIfCancellationRequested();
            RoutineAudioRestoration? restoration = changes.Count > 0 ? new RoutineAudioRestoration(changes, logger) : null;
            var result = new RoutineExecutionResult(
                (communicationsOutput?.Success ?? true) && (communicationsInput?.Success ?? true) && (output?.Success ?? true) && (input?.Success ?? true) && (masterVolume ?? true) && (micVolume ?? true) && (outputMute?.Success ?? true) && (inputMute?.Success ?? true),
                output?.Success == true ? output.Value.DeviceName : null,
                input?.Success == true ? input.Value.DeviceName : null,
                AwaitingAppCompletion: (output?.AwaitingAppCompletion ?? false) || (input?.AwaitingAppCompletion ?? false),
                AppOutputApplied: output?.AppRouteApplied ?? false,
                AppInputApplied: input?.AppRouteApplied ?? false,
                OutputSucceeded: output?.Success, InputSucceeded: input?.Success,
                MasterVolumeSucceeded: masterVolume, MicVolumeSucceeded: micVolume,
                OutputFailureDetail: output?.FailureDetail, InputFailureDetail: input?.FailureDetail,
                ElapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                OutputReconnectAttempted: output?.ReconnectAttempted ?? false,
                OutputReconnectSucceeded: output?.ReconnectSucceeded ?? false,
                InputReconnectAttempted: input?.ReconnectAttempted ?? false,
                InputReconnectSucceeded: input?.ReconnectSucceeded ?? false,
                RoutingProcessId: snapshot.SwitchOutputPerApp ? processId : null,
                OutputMuteSucceeded: outputMute?.Success, InputMuteSucceeded: inputMute?.Success,
                MuteFailureDetail: outputMute?.FailureDetail ?? inputMute?.FailureDetail, AudioRestoration: restoration,
                CommunicationsOutputSucceeded: communicationsOutput?.Success, CommunicationsInputSucceeded: communicationsInput?.Success,
                CommunicationsFailureDetail: communicationsOutput?.FailureDetail ?? communicationsInput?.FailureDetail,
                OwnsRoleRestoration: operations.SwitchRolesAsync != null && !snapshot.SwitchOutputPerApp,
                OwnsVolumeRestoration: operations.ApplyOwnedVolume != null);
            handedOff = true;
            trace.Complete(result.Success ? "success" : "partial-or-failed");
            return result;

            RoutineMuteApplicationResult? ApplyMute(bool playback, string? id, RoutineMuteAction action, bool? switched)
            {
                if (action == RoutineMuteAction.Unchanged) return null;
                string opId = $"routine-mute:{Guid.NewGuid():N}";
                RoutineMuteApplicationResult applied;
                try
                {
                    applied = switched == false
                        ? new(false, FailureDetail: "The target device could not be selected.")
                        : options.Deafen && action == RoutineMuteAction.Unmute
                            ? new(false, FailureDetail: "Unmute is blocked while deafen is active.")
                            : operations.ApplyMute?.Invoke(playback, string.IsNullOrWhiteSpace(id) ? null : id,
                                action == RoutineMuteAction.Mute, options.CaptureAudioRestoration, opId)
                                ?? new(false, FailureDetail: "Endpoint mute control is unavailable.");
                }
                catch (Exception ex)
                {
                    logger.Warning("RoutineExecutionService", $"routine-mute-apply-failed | opId={opId}", nameof(ExecuteAsync), ex);
                    applied = new(false, FailureDetail: "The endpoint mute action failed.");
                }
                if (applied.Change != null) changes.Add(applied.Change);
                if (!applied.Success)
                    logger.Warning("RoutineExecutionService", () => $"routine-mute-action-blocked | flow={(playback ? "output" : "input")} action={action} reason={applied.FailureDetail} opId={opId}");
                return applied;
            }
        }
        finally { if (!handedOff) new RoutineAudioRestoration(changes, logger).Complete(options.CaptureAudioRestoration); }
    }

    private async Task<DeviceSwitchResult> ExecuteDeviceAsync(
        AudioRoutine routine, bool playback, RoutineExecutionOptions options, int? processId, bool communications, CancellationToken cancellationToken)
    {
        string flow = (communications ? "communications-" : string.Empty) + (playback ? "output" : "input");
        string opId = $"routine-{flow}:{Guid.NewGuid():N}";
        BluetoothReconnectAttemptResult reconnect = default;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoutineDeviceReference? communication = communications ? (playback ? routine.CommunicationsOutput : routine.CommunicationsInput) : null;
            CycleDevice target = new()
            {
                Id = communication?.Id ?? (playback ? routine.OutputDeviceId : routine.InputDeviceId),
                Name = communication?.Name ?? (playback ? routine.OutputDeviceName : routine.InputDeviceName),
                StableId = communication != null ? communication.StableId : (playback ? routine.OutputDeviceStableId : routine.InputDeviceStableId),
            };
            IReadOnlyList<CycleDevice> active = operations.GetActiveDevices(playback);
            target = ResolveTarget(active);
            bool perApp = !communications && routine.SwitchOutputPerApp;
            string Context() => $"routineId={LogPrivacy.Id(routine.Id)} routineName={LogPrivacy.Label(routine.Name)} flow={flow} opId={opId} applicationProcessId={(processId is > 0 ? LogPrivacy.Id(processId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : "none")} perAppRouting={perApp} targetDeviceId={LogPrivacy.Id(target.Id)} targetDevice={LogPrivacy.Device(target.Name)}";
            if (!string.IsNullOrWhiteSpace(target.Id) && operations.FindActiveDevice(playback, target) == null)
            {
                string reconnectOpId = $"routine-{flow}-reconnect:{Guid.NewGuid():N}";
                logger.Debug("RoutineExecutionService", () => $"routine-target-reconnect-started | {Context()} reconnectOpId={reconnectOpId}");
                reconnect = await operations.ReconnectAsync(playback, target, active, reconnectOpId, cancellationToken);
                logger.Debug("RoutineExecutionService", () => $"routine-target-reconnect-completed | {Context()} reconnectOpId={reconnectOpId} attempted={reconnect.Attempted} connected={reconnect.Connected} attempts={reconnect.Attempts} cooldownSkips={reconnect.CooldownSkips}");
                if (reconnect.Attempted)
                {
                    await operations.WaitForReconnectAsync(playback, target, reconnect, reconnectOpId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    target = ResolveTarget(operations.GetActiveDevices(playback));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            logger.Debug("RoutineExecutionService", () => $"routine-{flow}-switch-started | {Context()}");
            DeviceSwitchResult result;
            if (perApp)
            {
                ProcessAudioDeviceSwitchResult routed = await operations.SwitchApplicationAsync(playback, target, (uint)processId!.Value, opId);
                bool applied = routed.Result == ProcessAudioRoutingResult.Applied;
                bool deferred = routed.Result == ProcessAudioRoutingResult.DeferredNoAudio;
                result = new(applied || (options.AllowDeferredRouting && deferred && routed.Success), routed.DeviceName,
                    options.AllowDeferredRouting && deferred, applied,
                    applied ? null : deferred ? $"Per-app {flow} routing is pending until the application produces audio." : $"Failed to apply per-app {flow} routing.");
            }
            else
            {
                var request = new RoutineDeviceSwitchRequest(playback, target, communications ? options with { PreserveAudioLevels = false } : options,
                    playback && !communications && !routine.MasterVolumePercent.HasValue, !playback && !communications && !routine.MicVolumePercent.HasValue, opId, communications);
                RoutineRoleSwitchResult switched;
                if (operations.SwitchRolesAsync != null) switched = await operations.SwitchRolesAsync(request);
                else
                {
                    (bool success, string? name) = await operations.SwitchDefaultAsync(request);
                    switched = new(success, name);
                }
                result = new(switched.Success, switched.DeviceName, FailureDetail: switched.Success ? null : $"Failed to switch the default {flow} device.", Change: switched.Change);
            }
            logger.Debug("RoutineExecutionService", () => $"routine-{flow}-switch-completed | {Context()} success={result.Success} applied={result.AppRouteApplied} awaitingAudio={result.AwaitingAppCompletion} deviceName={LogPrivacy.Device(result.DeviceName)}");
            return result with { ReconnectAttempted = reconnect.Attempted, ReconnectSucceeded = reconnect.Connected };

            CycleDevice ResolveTarget(IReadOnlyList<CycleDevice> devices)
            {
                CycleDevice matched = PersistedAudioDeviceResolver.TryResolveMatch(target, devices) ?? target;
                var resolved = new CycleDevice { Id = matched.Id, Name = matched.Name, StableId = matched.StableId ?? target.StableId };
                if (communications) return resolved;
                if (playback)
                {
                    routine.OutputDeviceId = resolved.Id;
                    routine.OutputDeviceName = resolved.Name;
                }
                else
                {
                    routine.InputDeviceId = resolved.Id;
                    routine.InputDeviceName = resolved.Name;
                }
                return resolved;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.Error("RoutineExecutionService", $"routine-{flow}-switch-failed | opId={opId}", nameof(ExecuteDeviceAsync), ex);
            return new(false, null, FailureDetail: $"{char.ToUpperInvariant(flow[0])}{flow[1..]} switch threw {ex.GetType().Name}.",
                ReconnectAttempted: reconnect.Attempted, ReconnectSucceeded: reconnect.Connected);
        }
    }

    private bool ApplyVolume(bool playback, string? deviceId, int percent, bool captureRestore, List<IRoutineAudioChange> changes)
    {
        string flow = playback ? "master" : "recording";
        string opId = $"routine-volume-{flow}:{Guid.NewGuid():N}";
        try
        {
            string? targetId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
            RoutineVolumeApplicationResult result = operations.ApplyOwnedVolume != null
                ? operations.ApplyOwnedVolume(playback, targetId, percent, captureRestore, opId)
                : new(operations.ApplyVolume(playback, targetId, percent, opId));
            if (result.Change != null) changes.Add(result.Change);
            bool applied = result.Success;
            if (!applied)
                logger.Warning("RoutineExecutionService", () => $"routine-volume-apply-failed | flow={flow} targetId={LogPrivacy.Id(deviceId)} opId={opId}");
            return applied;
        }
        catch (Exception ex)
        {
            logger.Warning("RoutineExecutionService", $"routine-volume-apply-failed | flow={flow} opId={opId}", nameof(ApplyVolume), ex);
            return false;
        }
    }
}

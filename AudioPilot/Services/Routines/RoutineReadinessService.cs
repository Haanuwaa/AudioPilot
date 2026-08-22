using AudioPilot.Cli;
using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.Services.Routines;

internal sealed record RoutineReadinessItem(string Area, string Status, string Code, string Message, string? Target = null);

internal sealed record RoutineReadinessResult(DateTimeOffset CheckedUtc, bool Ready, string RoutingState,
    IReadOnlyList<RoutineReadinessItem> Checks)
{
    internal bool Unavailable => Checks.Any(static check => check.Status == "unknown");
}

/// <summary>Inspects current manual-run readiness using only read operations, without changing routine state.</summary>
internal sealed class RoutineReadinessService(RoutineReadOperations operations, IRoutineProcessSnapshotProvider processes)
{
    internal Task<RoutineReadinessResult> CheckAsync(AudioRoutine routine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routine);
        AudioRoutine snapshot = routine.Clone();
        return Task.Run(() => Check(snapshot, operations, cancellationToken), cancellationToken);
    }

    internal Task<IReadOnlyList<(AudioRoutine Routine, RoutineReadinessResult Result)>> CheckAllAsync(
        IReadOnlyList<AudioRoutine> routines, CancellationToken cancellationToken)
    {
        AudioRoutine[] snapshots = [.. routines.Select(static routine => routine.Clone())];
        return Task.Run<IReadOnlyList<(AudioRoutine Routine, RoutineReadinessResult Result)>>(() =>
        {
            DateTimeOffset now = operations.UtcNow();
            var devices = new Dictionary<bool, Lazy<IReadOnlyList<CycleDevice>>>();
            var defaults = new Dictionary<bool, Lazy<CycleDevice?>>();
            var apps = new Dictionary<string, Lazy<bool>>(StringComparer.Ordinal);
            var networks = new Lazy<IReadOnlyCollection<string>?>(() => operations.GetConnectedNetworks!());
            RoutineReadOperations reads = operations with
            {
                UtcNow = () => now,
                GetActiveDevices = playback =>
                {
                    if (!devices.TryGetValue(playback, out var value))
                        devices[playback] = value = new(() => [.. operations.GetActiveDevices(playback)]);
                    return value.Value;
                },
                GetDefaultDevice = operations.GetDefaultDevice == null ? null : playback =>
                {
                    if (!defaults.TryGetValue(playback, out var value))
                        defaults[playback] = value = new(() => operations.GetDefaultDevice(playback));
                    return value.Value;
                },
                GetConnectedNetworks = operations.GetConnectedNetworks == null ? null : () => networks.Value,
                IsApplicationRunning = operations.IsApplicationRunning == null ? null : target =>
                {
                    if (!apps.TryGetValue(target, out var value))
                        apps[target] = value = new(() => operations.IsApplicationRunning(target));
                    return value.Value;
                },
            };
            var results = new List<(AudioRoutine, RoutineReadinessResult)>(snapshots.Length);
            foreach (AudioRoutine routine in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add((routine, Check(routine, reads, cancellationToken)));
            }
            return results;
        }, cancellationToken);
    }

    private RoutineReadinessResult Check(AudioRoutine routine, RoutineReadOperations operations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DateTimeOffset now = operations.UtcNow();
        List<RoutineReadinessItem> checks = [];
        Dictionary<bool, IReadOnlyList<CycleDevice>> devices = [];
        IReadOnlyList<CycleDevice> Devices(bool playback)
        {
            token.ThrowIfCancellationRequested();
            if (!devices.TryGetValue(playback, out var result)) devices[playback] = result = operations.GetActiveDevices(playback);
            return result;
        }
        RoutineReadOperations reads = operations with { GetActiveDevices = Devices, UtcNow = () => now };
        string routingState = routine.RouteToApplication ? "unknown" : "not-required";
        checks.Add(new("enabled", routine.Enabled ? "ready" : "blocked", routine.Enabled ? "routine-enabled" : "routine-disabled",
            routine.Enabled ? "The routine is enabled." : "The routine is disabled."));
        if (!routine.HasExecutionTarget) checks.Add(new("actions", "blocked", "routine-has-no-targets", "The routine has no configured actions."));
        if (routine.ValidateCommunicationsTargets() is { } communicationsError)
            checks.Add(new("actions", "blocked", "routine-invalid-communications", communicationsError));
        if (!Enum.IsDefined(routine.OutputMuteAction) || !Enum.IsDefined(routine.InputMuteAction))
            checks.Add(new("actions", "blocked", "routine-invalid-mute", "The routine contains an unknown mute action."));

        RoutineConditions conditions = routine.Conditions;
        if (conditions.Validate() is { } conditionError) checks.Add(new("requirements", "blocked", "routine-condition-invalid", conditionError));
        else
        {
            if (conditions.TimeWindow != null) Requirement("time", new() { TimeWindow = conditions.TimeWindow });
            if (conditions.Device != null) Requirement("required-device", new() { Device = conditions.Device, DeviceAvailable = conditions.DeviceAvailable }, conditions.Device.Name);
            if (!string.IsNullOrWhiteSpace(conditions.RunningAppPath)) Requirement("required-app", new() { RunningAppPath = conditions.RunningAppPath }, conditions.RunningAppPath);
            if (!string.IsNullOrWhiteSpace(conditions.ConnectedNetwork)) Requirement("required-network", new() { ConnectedNetwork = conditions.ConnectedNetwork }, conditions.ConnectedNetwork);
        }

        if (!string.IsNullOrWhiteSpace(routine.OutputDeviceId)) Target("output", true, new() { Id = routine.OutputDeviceId, Name = routine.OutputDeviceName, StableId = routine.OutputDeviceStableId });
        else if (routine.MasterVolumePercent.HasValue || routine.OutputMuteAction != RoutineMuteAction.Unchanged) DefaultTarget("output", true);
        if (!string.IsNullOrWhiteSpace(routine.InputDeviceId)) Target("input", false, new() { Id = routine.InputDeviceId, Name = routine.InputDeviceName, StableId = routine.InputDeviceStableId });
        else if (routine.MicVolumePercent.HasValue || routine.InputMuteAction != RoutineMuteAction.Unchanged) DefaultTarget("input", false);
        if (routine.CommunicationsOutput is { } output) Target("communications-output", true, new() { Id = output.Id, Name = output.Name, StableId = output.StableId });
        if (routine.CommunicationsInput is { } input) Target("communications-input", false, new() { Id = input.Id, Name = input.Name, StableId = input.StableId });
        MuteAction("output-mute", true, routine.OutputMuteAction);
        MuteAction("input-mute", false, routine.InputMuteAction);
        if (routine.RouteToApplication)
        {
            Inspect("application-routing", () =>
            {
                bool running = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, processes, out _, out _, out _);
                routingState = running ? "app-running-audio-not-checked" : "waiting-for-app";
                return new("application-routing", running ? "ready" : "waiting", running ? "routine-target-app-running" : "routine-target-app-not-running",
                    running ? "The target application is running. Routing may still wait for its audio session; this check does not test or change routes."
                        : "Manual execution requires the target application to be running.", routine.TargetAppPath);
            });
        }
        token.ThrowIfCancellationRequested();
        return new(now, checks.All(static check => check.Status == "ready"), routingState, checks);

        void Requirement(string area, RoutineConditions requirement, string? target = null) => Inspect(area, () =>
        {
            var failure = RoutineConditionEvaluator.Evaluate(requirement, reads, token);
            return failure is { } failed ? new(area, failed.Code == "routine-condition-unavailable" ? "unknown" : "blocked", failed.Code, failed.Reason, target)
                : new(area, "ready", "routine-condition-met", "The requirement is met.", target);
        });

        void Target(string area, bool playback, CycleDevice configured) => Inspect(area, () =>
        {
            var (Device, Ambiguous) = PersistedAudioDeviceResolver.ResolveMatch(configured, Devices(playback));
            return new(area, Device == null ? "blocked" : "ready",
                Device != null ? "routine-target-available" : Ambiguous ? "routine-target-ambiguous" : "routine-target-unavailable",
                Device != null ? "The target device is currently available."
                    : Ambiguous ? "Multiple devices match this target; it cannot be resolved uniquely." : "The target device is currently unavailable. Reconnect was not attempted.",
                Device?.Name ?? configured.Name);
        });

        void DefaultTarget(string area, bool playback) => Inspect(area, () =>
        {
            if (reads.GetDefaultDevice == null) return new(area, "unknown", "routine-default-device-unavailable", "The current default device could not be checked.");
            CycleDevice? device = reads.GetDefaultDevice(playback);
            return new(area, device == null ? "blocked" : "ready", device == null ? "routine-default-device-missing" : "routine-default-device-available",
                device == null ? "A current default device is not available for the action." : "The action targets the current default device.", device?.Name);
        });

        void MuteAction(string area, bool playback, RoutineMuteAction action)
        {
            if (action == RoutineMuteAction.Unchanged || !Enum.IsDefined(action) || reads.GetMuteBlockReason == null) return;
            Inspect(area, () =>
            {
                string? blocked = reads.GetMuteBlockReason(playback, action == RoutineMuteAction.Mute);
                return new(area, blocked == null ? "ready" : "blocked", blocked == null ? "routine-mute-allowed" : "routine-mute-blocked",
                    blocked ?? "The mute action is allowed by the current runtime controls.");
            });
        }

        void Inspect(string area, Func<RoutineReadinessItem> inspect)
        {
            token.ThrowIfCancellationRequested();
            try { checks.Add(inspect()); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { checks.Add(new(area, "unknown", "routine-check-unavailable", "This part of the routine could not be checked.")); }
        }
    }
}

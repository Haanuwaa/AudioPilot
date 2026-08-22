using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private static ExecutionHistoryKind? TryParseExecutionHistoryKind(string? type)
        {
            return type switch
            {
                null or "" => null,
                "routine" => ExecutionHistoryKind.Routine,
                "switch" => ExecutionHistoryKind.Switch,
                "media" => ExecutionHistoryKind.Media,
                "mute" => ExecutionHistoryKind.Mute,
                _ => null,
            };
        }

        private void RecordExecutionHistory(ExecutionHistoryEntry entry)
        {
            _executionHistory.Value.Record(entry);
            if (entry.TraceId != null)
            {
                using var trace = OperationTrace.Attach(entry.TraceId);
                _logger.Info("Diagnostics", () => $"operation-completed | historyId={entry.OpId} kind={entry.Kind} action={entry.Action} outcome={(entry.Success ? "success" : entry.Skipped ? "skipped" : entry.DiagCode ?? "failed")}");
            }
        }

        private void RecordMuteHistory(PendingMuteStateApply request, bool succeeded, bool skipped, bool partial)
        {
            string target = request.Context == nameof(MuteMic) ? "mic" : request.Context == nameof(MuteSound) ? "sound" : "deafen";
            bool enabled = target == "mic" ? request.MuteMicrophone : request.MutePlayback;
            RecordExecutionHistory(new ExecutionHistoryEntry(request.OperationId ?? $"mute:{Guid.NewGuid():N}", DateTimeOffset.UtcNow, ExecutionHistoryKind.Mute,
                request.Source, request.Action ?? (target == "deafen" ? "deafen-set" : $"mute-{target}-set"), succeeded, skipped,
                skipped ? "Mute request superseded by a newer choice." : succeeded ? $"{target} mute {(enabled ? "enabled" : "disabled")}."
                    : partial ? "Mute change partially applied." : "Mute change could not be confirmed.",
                Target: target, Enabled: succeeded ? enabled : null, DiagCode: succeeded || skipped ? null : partial ? "mute-change-partial" : "mute-change-failed",
                ElapsedMs: System.Diagnostics.Stopwatch.GetElapsedTime(request.StartedTimestamp).TotalMilliseconds, TraceId: request.TraceId));
        }

        private async Task<bool> ApplyCliMuteAsync(string actionName, string target, bool enabled, Action<bool> change, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
            linked.Token.ThrowIfCancellationRequested();
            if (IsRuntimeShutdownRequested || _isCleaningUp) return false;
            string previousSource = _muteRequestSource;
            string? previousAction = _muteRequestAction;
            long revision = _muteIntentRevision;
            Task<MuteStateApplyOutcome>? pending;
            try
            {
                _muteRequestSource = "cli";
                _muteRequestAction = actionName;
                change(enabled);
                pending = revision != _muteIntentRevision || _lastMuteStateApplyTask is { IsCompleted: false } ? _lastMuteStateApplyTask : null;
            }
            finally { _muteRequestSource = previousSource; _muteRequestAction = previousAction; }

            bool changed = revision != _muteIntentRevision;
            long requestedRevision = _muteIntentRevision;
            MuteStateApplyOutcome outcome = pending != null
                ? await pending.WaitAsync(linked.Token)
                : new(requestedRevision, true, true, false);
            linked.Token.ThrowIfCancellationRequested();
            if (IsRuntimeShutdownRequested || _isCleaningUp || !outcome.IsCurrent
                || outcome.Revision != _muteIntentRevision || requestedRevision != _muteIntentRevision) return false;
            if (!changed)
                RecordExecutionHistory(new ExecutionHistoryEntry($"mute:{Guid.NewGuid():N}", DateTimeOffset.UtcNow,
                    ExecutionHistoryKind.Mute, "cli", actionName, false, true, "No mute state change requested.", DiagCode: "mute-no-change"));
            _cliOverlayCoordinator.ShowMuteResult(target, enabled, outcome.Succeeded, outcome.PartiallySucceeded);
            return outcome.Succeeded;
        }

        private async ValueTask<bool> TrackDeviceSwitchAsync(Func<ValueTask<bool>> operation, bool output, bool reverse, string source, string? requestedName = null, bool direct = false)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            string operationId = $"switch:{Guid.NewGuid():N}";
            using var trace = OperationTrace.Start("device-switch", _logger, OperationTrace.CurrentId ?? operationId);
            string? previous = source == "cli" && !direct ? TryGetCurrentDefaultDeviceName(output) : null;
            bool success = false;
            string? error = null;
            try { success = await operation(); trace.Complete(success ? "success" : "not-completed"); return success; }
            catch (Exception ex) { error = ex.GetType().Name; throw; }
            finally
            {
                string? current = source == "cli" && !direct ? TryGetCurrentDefaultDeviceName(output) : requestedName;
                RecordSwitchHistory(output, reverse, source, success, false,
                    error ?? (success ? "Device switch completed." : "Device switch was not completed or was rejected; recovery may still be pending."),
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, previous, current, error != null, operationId, trace.Id, direct);
            }
        }

        private void RecordSwitchHistory(bool output, bool reverse, string source, bool success, bool skipped, string summary,
            double? elapsedMs = null, string? previous = null, string? deviceName = null, bool faulted = false,
            string? operationId = null, string? traceId = null, bool direct = false)
        {
            RecordExecutionHistory(new ExecutionHistoryEntry(operationId ?? $"switch:{Guid.NewGuid():N}", DateTimeOffset.UtcNow,
                ExecutionHistoryKind.Switch, source, direct || source == "picker" ? output ? "switch-output-device" : "switch-input-device"
                    : output ? reverse ? "switch-output-reverse" : "switch-output" : reverse ? "switch-input-reverse" : "switch-input",
                success, skipped, summary, success ? null : summary, OutputDeviceName: output ? deviceName : null,
                InputDeviceName: output ? null : deviceName, Target: previous,
                DiagCode: success || skipped ? null : faulted ? "switch-failed" : "switch-not-completed", ElapsedMs: elapsedMs, TraceId: traceId));
        }

        internal void RecordCoordinatorExecutionHistory(ExecutionHistoryEntry entry)
        {
            RecordExecutionHistory(entry);
        }

        private string GetDiagnosticsHistoryOutput(bool jsonOutput, int? limit, string? type, bool redactOutput)
        {
            IReadOnlyList<ExecutionHistoryEntry> entries = _executionHistory.Value.GetEntries(limit, TryParseExecutionHistoryKind(type));
            return CliOutputFormatter.FormatExecutionHistory(entries, jsonOutput, redactOutput);
        }

        private (bool Found, string Output) GetDiagnosticsHistoryDetailOutput(string opId, bool jsonOutput, bool redactOutput)
        {
            ExecutionHistoryEntry? entry = _executionHistory.Value.GetEntry(opId);
            return entry == null
                ? (false, CliOutputFormatter.FormatExecutionHistoryNotFound(opId, jsonOutput, redactOutput))
                : (true, CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput, redactOutput));
        }

        private void RecordRoutineExecutionHistory(AudioRoutine routine, string executionSource, RoutineExecutionResult result, string? correlatedOperationId)
        {
            string opId = string.IsNullOrWhiteSpace(correlatedOperationId)
                ? CreateRoutineOperationId("routine-execution")
                : correlatedOperationId!;

            string? reason = result.Skipped
                ? result.SkipReason ?? result.OutputFailureDetail ?? result.InputFailureDetail ?? routine.LastRunDetail
                : !result.Success
                    ? result.CommunicationsFailureDetail ?? result.MuteFailureDetail ?? result.OutputFailureDetail ?? result.InputFailureDetail ?? "Routine execution failed."
                    : result.AwaitingAppCompletion
                        ? "Waiting for app audio to appear before completing per-app routing."
                        : null;

            string summary = result.Skipped
                ? $"Routine '{routine.Name}' skipped."
                : result.Success
                    ? result.AwaitingAppCompletion ? $"Routine '{routine.Name}' started; waiting for app audio." : $"Routine '{routine.Name}' completed."
                    : $"Routine '{routine.Name}' failed.";
            string diagCode = result.Skipped
                ? result.SkipCode ?? "routine-run-skipped"
                : !result.Success
                    ? result.HasPartialSuccess
                        ? "routine-run-partial"
                        : "routine-run-failed"
                    : result.AwaitingAppCompletion
                        ? "routine-run-awaiting-app-audio"
                        : "routine-run-success";

            RecordExecutionHistory(new ExecutionHistoryEntry(
                OpId: opId,
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: ExecutionHistoryKind.Routine,
                Source: executionSource,
                Action: "routine-run",
                Success: result.Success,
                Skipped: result.Skipped,
                Summary: summary,
                Reason: reason,
                RoutineId: routine.Id,
                RoutineName: routine.Name,
                OutputDeviceName: result.OutputDeviceName,
                InputDeviceName: result.InputDeviceName,
                Target: routine.TargetSummary,
                OutputSucceeded: result.OutputSucceeded,
                InputSucceeded: result.InputSucceeded,
                AwaitingAppCompletion: result.AwaitingAppCompletion,
                AppOutputApplied: result.AppOutputApplied,
                AppInputApplied: result.AppInputApplied,
                DiagCode: diagCode,
                ElapsedMs: result.ElapsedMs,
                Details: new Dictionary<string, string>
                {
                    ["trigger"] = routine.TriggerKind.ToString(),
                    ["executionSource"] = executionSource,
                    ["communicationsOutputSucceeded"] = result.CommunicationsOutputSucceeded?.ToString() ?? "none",
                    ["communicationsInputSucceeded"] = result.CommunicationsInputSucceeded?.ToString() ?? "none",
                    ["outputMuteAction"] = routine.OutputMuteAction.ToString(),
                    ["inputMuteAction"] = routine.InputMuteAction.ToString(),
                    ["outputMuteSucceeded"] = FormatRoutineLogBool(result.OutputMuteSucceeded),
                    ["inputMuteSucceeded"] = FormatRoutineLogBool(result.InputMuteSucceeded),
                }));
        }

        private void RecordCliActionHistory(ExecutionHistoryKind kind, string action, bool success, bool skipped, string summary, string? reason = null, string? target = null, string? outputDeviceName = null, string? inputDeviceName = null, bool? enabled = null, string? diagCode = null, double? elapsedMs = null, IReadOnlyDictionary<string, string>? details = null)
        {
            RecordExecutionHistory(new ExecutionHistoryEntry(
                OpId: $"cli-{action}:{Guid.NewGuid():N}",
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: kind,
                Source: "cli",
                Action: action,
                Success: success,
                Skipped: skipped,
                Summary: summary,
                Reason: reason,
                OutputDeviceName: outputDeviceName,
                InputDeviceName: inputDeviceName,
                Target: target,
                Enabled: enabled,
                DiagCode: diagCode,
                ElapsedMs: elapsedMs,
                Details: details));
        }

        private string? TryGetCurrentDefaultDeviceName(bool output)
        {
            try
            {
                using var device = output
                    ? _audio.GetDefaultPlaybackDevice()
                    : _audio.GetDefaultRecordingDevice();
                return device?.FriendlyName;
            }
            catch
            {
                return null;
            }
        }
    }
}

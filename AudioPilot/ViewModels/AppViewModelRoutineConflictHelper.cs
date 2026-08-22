using AudioPilot.Helpers;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    internal static class AppViewModelRoutineConflictHelper
    {
        public static IReadOnlyList<string> BuildDistinctConflictWarnings(IEnumerable<AudioRoutine>? routines)
        {
            IReadOnlyDictionary<string, string> summaries = BuildConflictSummaries(routines);
            return
            [
                .. summaries.Values
                    .Where(static summary => !string.IsNullOrWhiteSpace(summary))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static summary => summary, StringComparer.Ordinal)
            ];
        }

        public static IReadOnlyDictionary<string, string> BuildConflictSummaries(IEnumerable<AudioRoutine>? routines)
        {
            return BuildConflictSummaries(routines, () => DateTime.UtcNow);
        }

        internal static IReadOnlyDictionary<string, string> BuildConflictSummaries(IEnumerable<AudioRoutine>? routines, Func<DateTime> nowProvider)
        {
            if (routines == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            Dictionary<string, string> summaries = new(StringComparer.OrdinalIgnoreCase);
            List<AudioRoutine> enabledRoutines =
            [
                .. routines.SelectMany(static routine => routine.ExpandAutomaticTriggers()).Where(static routine =>
                    routine.Enabled &&
                    routine.HasExecutionTarget)
            ];

            foreach (IGrouping<string, AudioRoutine> group in enabledRoutines
                .Where(static routine => routine.TriggerKind == RoutineTriggerKind.Application && routine.HasApplicationTrigger)
                .GroupBy(static routine => RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TriggerAppPath), StringComparer.OrdinalIgnoreCase))
            {
                ApplyTriggerGroupConflicts(
                    [.. group],
                    summaries,
                    $"Application trigger for {RoutineTriggerPathHelper.GetTriggerDisplayName(group.Key)}");
            }

            List<AudioRoutine> audioPilotStartupRoutines =
            [
                .. enabledRoutines.Where(static routine => routine.TriggerKind == RoutineTriggerKind.AudioPilotStartup)
            ];
            ApplyTriggerGroupConflicts(audioPilotStartupRoutines, summaries, "AudioPilot startup");
            ApplyTriggerGroupConflicts([.. enabledRoutines.Where(static routine => routine.TriggerKind is RoutineTriggerKind.SessionUnlock or RoutineTriggerKind.SystemResume)], summaries, "Unlock/resume");

            List<AudioRoutine> deviceChangeRoutines =
            [
                .. enabledRoutines.Where(static routine => routine.TriggerKind == RoutineTriggerKind.DeviceChange)
            ];
            ApplyTriggerGroupConflicts(deviceChangeRoutines, summaries, "Device change");

            foreach (var deviceGroup in enabledRoutines
                .Where(static routine => routine.TriggerKind == RoutineTriggerKind.DeviceAvailability && routine.TriggerDevice != null)
                .GroupBy(static routine => $"{routine.TriggerDevice!.Playback}:{GetCommunicationsIdentity(routine.TriggerDevice)}", StringComparer.OrdinalIgnoreCase))
            {
                foreach (DeviceAvailabilityTransition transition in new[] { DeviceAvailabilityTransition.Connected, DeviceAvailabilityTransition.Disconnected })
                {
                    List<AudioRoutine> matching = [.. deviceGroup.Where(routine => routine.DeviceTransition == transition || routine.DeviceTransition == DeviceAvailabilityTransition.Both)];
                    if (matching.Count == 0) continue;
                    matching.AddRange(deviceChangeRoutines);
                    string label = transition == DeviceAvailabilityTransition.Connected ? "available" : "unavailable";
                    ApplyTriggerGroupConflicts(matching, summaries, $"Device {label} '{deviceGroup.First().TriggerDevice!.DisplayName}'");
                }
            }

            List<AudioRoutine> steamBigPictureRoutines =
            [
                .. enabledRoutines.Where(static routine => routine.TriggerKind == RoutineTriggerKind.SteamBigPicture)
            ];
            ApplyTriggerGroupConflicts(steamBigPictureRoutines, summaries, "Steam Big Picture");

            List<AudioRoutine> scheduledRoutines =
            [
                .. enabledRoutines.Where(static routine => routine.TriggerKind == RoutineTriggerKind.Scheduled)
            ];
            ApplyScheduleTimeConflicts(scheduledRoutines, summaries, nowProvider);

            foreach (IGrouping<string, AudioRoutine> group in enabledRoutines
                .Where(static routine =>
                    routine.TriggerKind == RoutineTriggerKind.Network &&
                    routine.NetworkTriggerDirection is NetworkTriggerDirection.Connect or NetworkTriggerDirection.Both &&
                    !string.IsNullOrWhiteSpace(routine.TriggerNetworkName))
                .GroupBy(static routine => NetworkHelper.NormalizeNetworkName(routine.TriggerNetworkName), StringComparer.OrdinalIgnoreCase))
            {
                ApplyTriggerGroupConflicts([.. group], summaries, $"Network connect '{group.Key}'");
            }

            foreach (IGrouping<string, AudioRoutine> group in enabledRoutines
                .Where(static routine =>
                    routine.TriggerKind == RoutineTriggerKind.Network &&
                    routine.NetworkTriggerDirection is NetworkTriggerDirection.Both or NetworkTriggerDirection.Disconnect &&
                    !string.IsNullOrWhiteSpace(routine.TriggerNetworkName))
                .GroupBy(static routine => NetworkHelper.NormalizeNetworkName(routine.TriggerNetworkName), StringComparer.OrdinalIgnoreCase))
            {
                ApplyTriggerGroupConflicts([.. group], summaries, $"Network disconnect '{group.Key}'");
            }

            List<AudioRoutine> disconnectAnyNetworkRoutines =
            [
                .. enabledRoutines.Where(static routine =>
                    routine.TriggerKind == RoutineTriggerKind.Network &&
                    routine.NetworkTriggerDirection == NetworkTriggerDirection.Disconnect &&
                    string.IsNullOrWhiteSpace(routine.TriggerNetworkName))
            ];
            ApplyTriggerGroupConflicts(disconnectAnyNetworkRoutines, summaries, "Network disconnect");

            return summaries;
        }

        private static void ApplyTriggerGroupConflicts(
            List<AudioRoutine> routines,
            Dictionary<string, string> summaries,
            string triggerLabel)
        {
            if (routines.Count < 2)
            {
                return;
            }

            ApplyEndpointActionConflicts(routines, summaries, triggerLabel);
            foreach (var scopeGroup in routines.GroupBy(GetRoutingScope, StringComparer.OrdinalIgnoreCase))
            {
                List<AudioRoutine> scopedRoutines = [.. scopeGroup];
                List<AudioRoutine> outputConflictRoutines = [.. BuildFlowConflictMembers(scopedRoutines, static routine => routine.OutputDeviceId),
                    .. BuildFlowConflictMembers(scopedRoutines, static routine => GetCommunicationsIdentity(routine.CommunicationsOutput))];
                List<AudioRoutine> inputConflictRoutines = [.. BuildFlowConflictMembers(scopedRoutines, static routine => routine.InputDeviceId),
                    .. BuildFlowConflictMembers(scopedRoutines, static routine => GetCommunicationsIdentity(routine.CommunicationsInput))];
                HashSet<string> involvedRoutineIds =
                [
                    .. outputConflictRoutines.Select(static routine => NormalizeRoutineId(routine.Id)),
                    .. inputConflictRoutines.Select(static routine => NormalizeRoutineId(routine.Id))
                ];

                if (involvedRoutineIds.Count < 2)
                {
                    continue;
                }

                foreach (AudioRoutine routine in scopeGroup)
                {
                    string routineId = NormalizeRoutineId(routine.Id);
                    if (!involvedRoutineIds.Contains(routineId))
                    {
                        continue;
                    }

                    bool hasOutputConflict = outputConflictRoutines.Any(candidate => string.Equals(candidate.Id, routine.Id, StringComparison.OrdinalIgnoreCase));
                    bool hasInputConflict = inputConflictRoutines.Any(candidate => string.Equals(candidate.Id, routine.Id, StringComparison.OrdinalIgnoreCase));
                    int otherCount = Math.Max(1, involvedRoutineIds.Count - 1);
                    MergeSummary(summaries, routineId, BuildConflictSummary(triggerLabel, hasOutputConflict, hasInputConflict, otherCount));
                }
            }
        }

        private static void ApplyEndpointActionConflicts(IReadOnlyList<AudioRoutine> routines, Dictionary<string, string> summaries, string triggerLabel)
        {
            Report(true, static routine => routine.MasterVolumePercent, "output volume levels");
            Report(false, static routine => routine.MicVolumePercent, "microphone volume levels");
            Report(true, static routine => routine.OutputMuteAction == RoutineMuteAction.Unchanged ? null : (int?)routine.OutputMuteAction, "output mute actions");
            Report(false, static routine => routine.InputMuteAction == RoutineMuteAction.Unchanged ? null : (int?)routine.InputMuteAction, "microphone mute actions");

            void Report(bool playback, Func<AudioRoutine, int?> value, string description)
            {
                foreach (var target in routines.Where(routine => value(routine).HasValue)
                    .GroupBy(routine => GetEndpointActionTarget(routine, playback), StringComparer.OrdinalIgnoreCase))
                {
                    var assignments = target.DistinctBy(static routine => routine.Id, StringComparer.OrdinalIgnoreCase).GroupBy(value).ToArray();
                    if (assignments.Length < 2) continue;
                    int total = assignments.Sum(static group => group.Count());
                    foreach (var assignment in assignments)
                    {
                        int others = total - assignment.Count();
                        string count = others == 1 ? "1 other enabled routine" : $"{others} other enabled routines";
                        foreach (AudioRoutine routine in assignment)
                            MergeSummary(summaries, NormalizeRoutineId(routine.Id), $"{triggerLabel} conflicts with {count}: different {description}.");
                    }
                }
            }
        }

        private static string GetEndpointActionTarget(AudioRoutine routine, bool playback)
        {
            string? stableId = playback ? routine.OutputDeviceStableId : routine.InputDeviceStableId;
            string id = playback ? routine.OutputDeviceId : routine.InputDeviceId;
            return !string.IsNullOrWhiteSpace(stableId) ? "stable:" + stableId : string.IsNullOrWhiteSpace(id) ? "default" : "id:" + id;
        }

        private static bool HaveCompetingEndpointActions(AudioRoutine left, AudioRoutine right)
        {
            bool output = string.Equals(GetEndpointActionTarget(left, true), GetEndpointActionTarget(right, true), StringComparison.OrdinalIgnoreCase);
            bool input = string.Equals(GetEndpointActionTarget(left, false), GetEndpointActionTarget(right, false), StringComparison.OrdinalIgnoreCase);
            return (output && ((left.MasterVolumePercent.HasValue && right.MasterVolumePercent.HasValue && left.MasterVolumePercent != right.MasterVolumePercent) ||
                (left.OutputMuteAction != RoutineMuteAction.Unchanged && right.OutputMuteAction != RoutineMuteAction.Unchanged && left.OutputMuteAction != right.OutputMuteAction))) ||
                (input && ((left.MicVolumePercent.HasValue && right.MicVolumePercent.HasValue && left.MicVolumePercent != right.MicVolumePercent) ||
                (left.InputMuteAction != RoutineMuteAction.Unchanged && right.InputMuteAction != RoutineMuteAction.Unchanged && left.InputMuteAction != right.InputMuteAction)));
        }

        private static string GetRoutingScope(AudioRoutine routine) => routine.RouteToApplication
            ? "app:" + RoutineTriggerPathHelper.NormalizeTriggerTarget(routine.TargetAppPath) : "system";

        private static void ApplyScheduleTimeConflicts(
            List<AudioRoutine> scheduledRoutines,
            Dictionary<string, string> summaries,
            Func<DateTime> nowProvider)
        {
            if (scheduledRoutines.Count < 2)
            {
                return;
            }

            DateTime nowUtc = RoutineScheduleCalculator.NormalizeToUtc(nowProvider());
            DateTime horizonStartUtc = RoutineScheduleCalculator.TruncateToMinute(nowUtc);
            DateTime horizonEndUtc = horizonStartUtc.AddDays(8);
            Dictionary<string, AudioRoutine> routinesById = scheduledRoutines.ToDictionary(static routine => NormalizeRoutineId(routine.RuntimeTriggerKey), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ScheduleConflictAccumulator> conflicts = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, IReadOnlyList<ScheduleConflictOccurrence>> occurrenceCache = new(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < scheduledRoutines.Count - 1; i++)
            {
                AudioRoutine left = scheduledRoutines[i];
                string leftId = NormalizeRoutineId(left.RuntimeTriggerKey);
                IReadOnlyList<ScheduleConflictOccurrence> leftOccurrences = GetOrCreateOccurrences(
                    occurrenceCache,
                    leftId,
                    left,
                    horizonStartUtc,
                    horizonEndUtc);

                for (int j = i + 1; j < scheduledRoutines.Count; j++)
                {
                    AudioRoutine right = scheduledRoutines[j];
                    if (string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    bool sameRoutingScope = string.Equals(GetRoutingScope(left), GetRoutingScope(right), StringComparison.OrdinalIgnoreCase);
                    if (!sameRoutingScope && !HaveCompetingEndpointActions(left, right)) continue;
                    string rightId = NormalizeRoutineId(right.RuntimeTriggerKey);
                    IReadOnlyList<ScheduleConflictOccurrence> rightOccurrences = GetOrCreateOccurrences(
                        occurrenceCache,
                        rightId,
                        right,
                        horizonStartUtc,
                        horizonEndUtc);

                    if (!TryGetScheduleOverlap(leftOccurrences, rightOccurrences, out HashSet<DayOfWeek> leftOverlapDays, out HashSet<DayOfWeek> rightOverlapDays))
                    {
                        continue;
                    }

                    ApplyEndpointActionConflicts([left, right], summaries, "Scheduled");
                    bool hasOutputConflict = sameRoutingScope && (HasConflictingTarget(left.OutputDeviceId, right.OutputDeviceId)
                        || HasConflictingTarget(GetCommunicationsIdentity(left.CommunicationsOutput), GetCommunicationsIdentity(right.CommunicationsOutput)));
                    bool hasInputConflict = sameRoutingScope && (HasConflictingTarget(left.InputDeviceId, right.InputDeviceId)
                        || HasConflictingTarget(GetCommunicationsIdentity(left.CommunicationsInput), GetCommunicationsIdentity(right.CommunicationsInput)));

                    AddScheduleConflict(conflicts, leftId, right.Id, leftOverlapDays, hasOutputConflict, hasInputConflict);
                    AddScheduleConflict(conflicts, rightId, left.Id, rightOverlapDays, hasOutputConflict, hasInputConflict);
                }
            }

            foreach ((string routineId, ScheduleConflictAccumulator accumulator) in conflicts)
            {
                if (!routinesById.TryGetValue(routineId, out AudioRoutine? routine))
                {
                    continue;
                }

                if (accumulator.HasOutputConflict || accumulator.HasInputConflict)
                {
                    MergeSummary(summaries, NormalizeRoutineId(routine.Id), BuildConflictSummary("Scheduled", accumulator.HasOutputConflict, accumulator.HasInputConflict, accumulator.OtherRoutineIds.Count));
                }

                bool overlapsDaily = routine.ScheduleDays.Count == 0 && accumulator.OverlapDays.Count == 7;
                string summary = BuildScheduleConflictSummary(accumulator.OtherRoutineIds.Count, overlapsDaily, accumulator.OverlapDays, routine.ScheduleTime);
                MergeSummary(summaries, NormalizeRoutineId(routine.Id), summary);
            }
        }

        private static IReadOnlyList<ScheduleConflictOccurrence> GetOrCreateOccurrences(
            Dictionary<string, IReadOnlyList<ScheduleConflictOccurrence>> occurrenceCache,
            string routineId,
            AudioRoutine routine,
            DateTime horizonStartUtc,
            DateTime horizonEndUtc)
        {
            if (occurrenceCache.TryGetValue(routineId, out IReadOnlyList<ScheduleConflictOccurrence>? cached))
            {
                return cached;
            }

            TimeZoneInfo routineTimeZone = RoutineScheduleCalculator.ResolveRoutineTimeZone(routine.ScheduleTimeZoneId);
            DateOnly startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(horizonStartUtc, routineTimeZone));
            DateOnly endDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(horizonEndUtc, routineTimeZone));
            List<ScheduleConflictOccurrence> occurrences = [];

            for (DateOnly localDate = startDate; localDate <= endDate; localDate = localDate.AddDays(1))
            {
                if (routine.ScheduleDays.Count > 0 && !routine.ScheduleDays.Contains(localDate.DayOfWeek))
                {
                    continue;
                }

                if (!RoutineScheduleCalculator.TryCreateScheduledOccurrenceUtc(localDate, routine.ScheduleTime, routineTimeZone, out DateTime occurrenceUtc))
                {
                    continue;
                }

                if (occurrenceUtc < horizonStartUtc || occurrenceUtc > horizonEndUtc)
                {
                    continue;
                }

                occurrences.Add(new ScheduleConflictOccurrence(occurrenceUtc, localDate.DayOfWeek));
            }

            occurrenceCache[routineId] = occurrences;
            return occurrences;
        }

        private static List<AudioRoutine> BuildFlowConflictMembers(
            IReadOnlyList<AudioRoutine> routines,
            Func<AudioRoutine, string?> getTargetId)
        {
            List<IGrouping<string, AudioRoutine>> groups =
            [
                .. routines
                    .Where(routine => !string.IsNullOrWhiteSpace(getTargetId(routine)))
                    .GroupBy(routine => getTargetId(routine)!, StringComparer.OrdinalIgnoreCase)
            ];
            if (groups.Count < 2)
            {
                return [];
            }

            return [.. groups.SelectMany(static group => group)];
        }

        private static string BuildConflictSummary(string triggerLabel, bool hasOutputConflict, bool hasInputConflict, int otherCount)
        {
            string targetSummary = (hasOutputConflict, hasInputConflict) switch
            {
                (true, true) => "different output and input targets",
                (true, false) => "different output targets",
                (false, true) => "different input targets",
                _ => "conflicting targets",
            };
            string routineCountLabel = otherCount == 1
                ? "1 other enabled routine"
                : $"{otherCount} other enabled routines";
            return $"{triggerLabel} conflicts with {routineCountLabel}: {targetSummary}.";
        }

        private static string BuildScheduleConflictSummary(int otherCount, bool overlapsDaily, HashSet<DayOfWeek> overlapDays, TimeOnly scheduleTime)
        {
            string routineCountLabel = otherCount == 1
                ? "1 other enabled routine"
                : $"{otherCount} other enabled routines";

            string overlapLabel = overlapsDaily
                ? "daily"
                : string.Join(", ", overlapDays.OrderBy(static day => (int)day).Select(static day => day.ToString()));

            return $"Scheduled time overlap with {routineCountLabel}: {scheduleTime:hh\\:mm tt} on {overlapLabel}.";
        }

        private static void MergeSummary(Dictionary<string, string> summaries, string routineId, string summary)
        {
            string normalized = summary?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (!summaries.TryGetValue(routineId, out string? existing) || string.IsNullOrWhiteSpace(existing))
            {
                summaries[routineId] = normalized;
                return;
            }

            if (existing.Contains(normalized, StringComparison.Ordinal))
            {
                return;
            }

            summaries[routineId] = $"{existing} {normalized}";
        }

        private static bool TryGetScheduleOverlap(
            IReadOnlyList<ScheduleConflictOccurrence> leftOccurrences,
            IReadOnlyList<ScheduleConflictOccurrence> rightOccurrences,
            out HashSet<DayOfWeek> leftOverlapDays,
            out HashSet<DayOfWeek> rightOverlapDays)
        {
            leftOverlapDays = [];
            rightOverlapDays = [];

            if (leftOccurrences.Count == 0 || rightOccurrences.Count == 0)
            {
                return false;
            }

            Dictionary<DateTime, List<ScheduleConflictOccurrence>> rightByUtcMinute = rightOccurrences
                .GroupBy(static occurrence => occurrence.UtcTime)
                .ToDictionary(static group => group.Key, static group => group.ToList());

            foreach (ScheduleConflictOccurrence leftOccurrence in leftOccurrences)
            {
                if (!rightByUtcMinute.TryGetValue(leftOccurrence.UtcTime, out List<ScheduleConflictOccurrence>? matches))
                {
                    continue;
                }

                leftOverlapDays.Add(leftOccurrence.LocalDay);
                foreach (ScheduleConflictOccurrence match in matches)
                {
                    rightOverlapDays.Add(match.LocalDay);
                }
            }

            return leftOverlapDays.Count > 0 && rightOverlapDays.Count > 0;
        }

        private static void AddScheduleConflict(
            Dictionary<string, ScheduleConflictAccumulator> conflicts,
            string routineId,
            string otherRoutineId,
            HashSet<DayOfWeek> overlapDays,
            bool hasOutputConflict,
            bool hasInputConflict)
        {
            if (!conflicts.TryGetValue(routineId, out ScheduleConflictAccumulator? accumulator))
            {
                accumulator = new ScheduleConflictAccumulator();
                conflicts[routineId] = accumulator;
            }

            accumulator.OtherRoutineIds.Add(otherRoutineId);
            accumulator.OverlapDays.UnionWith(overlapDays);
            accumulator.HasOutputConflict |= hasOutputConflict;
            accumulator.HasInputConflict |= hasInputConflict;
        }

        private static string GetCommunicationsIdentity(RoutineDeviceReference? target) =>
            !string.IsNullOrWhiteSpace(target?.StableId) ? target.StableId : target?.Id ?? string.Empty;

        private static bool HasConflictingTarget(string? leftTargetId, string? rightTargetId)
        {
            return !string.IsNullOrWhiteSpace(leftTargetId)
                && !string.IsNullOrWhiteSpace(rightTargetId)
                && !string.Equals(leftTargetId, rightTargetId, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ScheduleConflictAccumulator
        {
            public HashSet<string> OtherRoutineIds { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<DayOfWeek> OverlapDays { get; } = [];
            public bool HasOutputConflict { get; set; }
            public bool HasInputConflict { get; set; }
        }

        private sealed record ScheduleConflictOccurrence(DateTime UtcTime, DayOfWeek LocalDay);

        private static string NormalizeRoutineId(string? routineId)
        {
            return string.IsNullOrWhiteSpace(routineId)
                ? "unknown"
                : routineId.Trim();
        }
    }
}

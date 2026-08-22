using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Coordinators
{
    internal readonly record struct SwitchCycleState(
        Dictionary<string, MMDevice> ActiveById,
        List<CycleDevice> ConnectedCycle,
        List<CycleDevice> SkippedDevices);

    internal readonly record struct SwitchCycleStateResolution(
        SwitchCycleState State,
        bool UsedEnumeratedDevices);

    internal readonly record struct SwitchCycleSnapshotState(
        List<CycleDevice> ConnectedCycle,
        List<CycleDevice> SkippedDevices);

    internal enum SingleConnectedCycleDecision
    {
        ContinueSwitch = 0,
        AwaitReconnectObservation,
        ReturnReconnectPending,
        FailNoAlternateConnected,
    }

    internal static class AppSwitchCycleStateResolver
    {

        internal static int ResolveCycleTargetIndex(int currentIndex, int cycleCount, bool reverse)
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

        internal static bool ContainsConfiguredDeviceId(IReadOnlyList<CycleDevice> configuredCycle, string? deviceId)
        {
            if (configuredCycle == null || string.IsNullOrWhiteSpace(deviceId))
            {
                return false;
            }

            for (int index = 0; index < configuredCycle.Count; index++)
            {
                CycleDevice? configured = configuredCycle[index];
                if (!string.IsNullOrWhiteSpace(configured?.Id)
                    && configured.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool TryResolveDeferredSwitchTargetIndex(
            string? currentDeviceId,
            IReadOnlyList<CycleDevice> connectedCycle,
            bool reverse,
            out int targetIndex)
        {
            targetIndex = -1;
            if (string.IsNullOrWhiteSpace(currentDeviceId) || connectedCycle == null || connectedCycle.Count <= 1)
            {
                return false;
            }

            int currentIndex = -1;
            for (int index = 0; index < connectedCycle.Count; index++)
            {
                if (connectedCycle[index].Id.Equals(currentDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    currentIndex = index;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                return false;
            }

            targetIndex = ResolveCycleTargetIndex(currentIndex, connectedCycle.Count, reverse);
            return targetIndex >= 0 && targetIndex < connectedCycle.Count;
        }

        internal static bool TryResolveDeferredPendingTarget(
            string? pendingDeviceId,
            string? pendingDeviceName,
            IReadOnlyList<CycleDevice> connectedCycle,
            out CycleDevice targetDevice)
        {
            targetDevice = new CycleDevice();
            if ((string.IsNullOrWhiteSpace(pendingDeviceId) && string.IsNullOrWhiteSpace(pendingDeviceName))
                || connectedCycle == null
                || connectedCycle.Count == 0)
            {
                return false;
            }

            for (int index = 0; index < connectedCycle.Count; index++)
            {
                CycleDevice candidate = connectedCycle[index];
                if (!string.IsNullOrWhiteSpace(pendingDeviceId)
                    && candidate.Id.Equals(pendingDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    targetDevice = CloneCycleDevice(candidate);
                    return true;
                }
            }

            if (!TryResolveUniqueBestMatch(
                connectedCycle,
                static _ => false,
                static candidate => candidate.Name,
                pendingDeviceName,
                out CycleDevice bestCandidate,
                out _))
            {
                return false;
            }

            targetDevice = CloneCycleDevice(bestCandidate);
            return true;
        }

        internal static SingleConnectedCycleDecision ResolveSingleConnectedCycleDecision(
            int connectedCount,
            int skippedCount,
            bool reconnectAttempted,
            bool reconnectSucceeded)
        {
            if (connectedCount != 1)
            {
                return SingleConnectedCycleDecision.ContinueSwitch;
            }

            if (skippedCount > 0)
            {
                if (reconnectSucceeded)
                {
                    return SingleConnectedCycleDecision.AwaitReconnectObservation;
                }

                return reconnectAttempted
                    ? SingleConnectedCycleDecision.ReturnReconnectPending
                    : SingleConnectedCycleDecision.FailNoAlternateConnected;
            }

            return SingleConnectedCycleDecision.FailNoAlternateConnected;
        }

        internal static SwitchCycleStateResolution ResolveInitialCycleState(
            IReadOnlyList<CycleDevice> configuredCycle,
            Func<CycleDevice, CycleDevice?> tryResolveActiveDevice,
            Func<SwitchCycleState> buildEnumeratedState)
        {
            ArgumentNullException.ThrowIfNull(configuredCycle);
            ArgumentNullException.ThrowIfNull(tryResolveActiveDevice);
            ArgumentNullException.ThrowIfNull(buildEnumeratedState);

            if (TryBuildExactIdCycleState(
                    configuredCycle,
                    tryResolveActiveDevice,
                    out List<CycleDevice> connectedCycle,
                    out List<CycleDevice> skippedDevices)
                && skippedDevices.Count == 0)
            {
                return new SwitchCycleStateResolution(
                    new SwitchCycleState(new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase), connectedCycle, skippedDevices),
                    UsedEnumeratedDevices: false);
            }

            return new SwitchCycleStateResolution(buildEnumeratedState(), UsedEnumeratedDevices: true);
        }

        internal static SwitchCycleState BuildCycleState(
            IReadOnlyList<CycleDevice> configuredCycle,
            List<MMDevice> activeDevices)
        {
            var activeById = new Dictionary<string, MMDevice>(activeDevices.Count, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < activeDevices.Count; index++)
            {
                MMDevice device = activeDevices[index];
                if (device == null || string.IsNullOrWhiteSpace(device.ID) || activeById.ContainsKey(device.ID))
                {
                    continue;
                }

                activeById[device.ID] = device;
            }

            var connectedCycle = new List<CycleDevice>(configuredCycle.Count);
            var skippedDevices = new List<CycleDevice>(configuredCycle.Count);
            var reservedActiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? activeNamesById = null;

            for (int index = 0; index < configuredCycle.Count; index++)
            {
                CycleDevice configured = configuredCycle[index];
                if (!string.IsNullOrWhiteSpace(configured.Id) && activeById.ContainsKey(configured.Id))
                {
                    reservedActiveIds.Add(configured.Id);
                }
            }

            for (int index = 0; index < configuredCycle.Count; index++)
            {
                CycleDevice configured = configuredCycle[index];
                if (activeById.TryGetValue(configured.Id, out MMDevice? active))
                {
                    connectedCycle.Add(new CycleDevice
                    {
                        Id = configured.Id,
                        Name = TryGetFriendlyName(active, configured.Name),
                    });
                }
                else
                {
                    activeNamesById ??= BuildActiveNamesById(activeDevices, reservedActiveIds);

                    if (TryResolveConfiguredDeviceByActiveName(configured, activeNamesById, reservedActiveIds, out CycleDevice remappedDevice))
                    {
                        connectedCycle.Add(remappedDevice);
                        reservedActiveIds.Add(remappedDevice.Id);
                    }
                    else
                    {
                        skippedDevices.Add(new CycleDevice
                        {
                            Id = configured.Id,
                            Name = configured.Name,
                        });
                    }
                }
            }

            return new SwitchCycleState(activeById, connectedCycle, skippedDevices);
        }

        internal static SwitchCycleSnapshotState BuildCycleSnapshotState(
            IReadOnlyList<CycleDevice> configuredCycle,
            IReadOnlyList<CycleDevice> activeDevices)
        {
            ArgumentNullException.ThrowIfNull(configuredCycle);
            ArgumentNullException.ThrowIfNull(activeDevices);

            var activeById = new Dictionary<string, CycleDevice>(activeDevices.Count, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < activeDevices.Count; index++)
            {
                CycleDevice active = activeDevices[index];
                if (!string.IsNullOrWhiteSpace(active?.Id) && !activeById.ContainsKey(active.Id))
                {
                    activeById.Add(active.Id, active);
                }
            }

            var connectedCycle = new List<CycleDevice>(configuredCycle.Count);
            var skippedDevices = new List<CycleDevice>(configuredCycle.Count);
            var reservedActiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeNamesById = new Dictionary<string, string>(activeById.Count, StringComparer.OrdinalIgnoreCase);
            foreach ((string activeId, CycleDevice active) in activeById)
            {
                if (!string.IsNullOrWhiteSpace(active.Name))
                {
                    activeNamesById.Add(activeId, active.Name);
                }
            }

            for (int index = 0; index < configuredCycle.Count; index++)
            {
                string configuredId = configuredCycle[index].Id;
                if (!string.IsNullOrWhiteSpace(configuredId) && activeById.ContainsKey(configuredId))
                {
                    reservedActiveIds.Add(configuredId);
                }
            }

            for (int index = 0; index < configuredCycle.Count; index++)
            {
                CycleDevice configured = configuredCycle[index];
                if (activeById.TryGetValue(configured.Id, out CycleDevice? active))
                {
                    connectedCycle.Add(CreateCycleDevice(active.Id, string.IsNullOrWhiteSpace(active.Name) ? configured.Name : active.Name));
                    continue;
                }

                if (TryResolveConfiguredDeviceByActiveName(
                    configured,
                    activeNamesById,
                    reservedActiveIds,
                    out CycleDevice remappedDevice))
                {
                    connectedCycle.Add(remappedDevice);
                    reservedActiveIds.Add(remappedDevice.Id);
                }
                else
                {
                    skippedDevices.Add(CloneCycleDevice(configured));
                }
            }

            return new SwitchCycleSnapshotState(connectedCycle, skippedDevices);
        }

        internal static bool TryBuildExactIdCycleState(
            IReadOnlyList<CycleDevice> configuredCycle,
            Func<CycleDevice, CycleDevice?> tryResolveActiveDevice,
            out List<CycleDevice> connectedCycle,
            out List<CycleDevice> skippedDevices)
        {
            connectedCycle = new List<CycleDevice>(configuredCycle.Count);
            skippedDevices = new List<CycleDevice>(configuredCycle.Count);

            for (int index = 0; index < configuredCycle.Count; index++)
            {
                CycleDevice configured = configuredCycle[index];
                if (string.IsNullOrWhiteSpace(configured.Id))
                {
                    connectedCycle.Clear();
                    skippedDevices.Clear();
                    return false;
                }

                CycleDevice? resolved = tryResolveActiveDevice(configured);
                if (resolved != null)
                {
                    connectedCycle.Add(resolved);
                }
                else
                {
                    skippedDevices.Add(CloneCycleDevice(configured));
                }
            }

            return true;
        }

        internal static bool TryResolveConfiguredTarget(
            IReadOnlyList<CycleDevice> configuredCycle,
            string? currentDeviceId,
            bool reverse,
            out CycleDevice targetDevice)
        {
            targetDevice = new CycleDevice();
            if (configuredCycle == null || configuredCycle.Count == 0)
            {
                return false;
            }

            int currentIndex = -1;
            if (!string.IsNullOrWhiteSpace(currentDeviceId))
            {
                for (int index = 0; index < configuredCycle.Count; index++)
                {
                    if (configuredCycle[index].Id.Equals(currentDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        currentIndex = index;
                        break;
                    }
                }
            }

            int targetIndex = ResolveCycleTargetIndex(currentIndex, configuredCycle.Count, reverse);
            if (targetIndex < 0 || targetIndex >= configuredCycle.Count)
            {
                return false;
            }

            CycleDevice configuredTarget = configuredCycle[targetIndex];
            if (string.IsNullOrWhiteSpace(configuredTarget.Id))
            {
                return false;
            }

            if (configuredCycle.Count == 1 && string.Equals(configuredTarget.Id, currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            targetDevice = CloneCycleDevice(configuredTarget);

            return true;
        }

        private static Dictionary<string, string> BuildActiveNamesById(
            List<MMDevice> activeDevices,
            HashSet<string> reservedActiveIds)
        {
            var activeNamesById = new Dictionary<string, string>(activeDevices.Count, StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < activeDevices.Count; index++)
            {
                MMDevice device = activeDevices[index];
                if (device == null || string.IsNullOrWhiteSpace(device.ID) || reservedActiveIds.Contains(device.ID) || activeNamesById.ContainsKey(device.ID))
                {
                    continue;
                }

                string friendlyName = TryGetFriendlyName(device, string.Empty);
                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    activeNamesById[device.ID] = friendlyName;
                }
            }

            return activeNamesById;
        }

        internal static string TryGetFriendlyName(MMDevice device, string fallbackName)
        {
            try
            {
                return string.IsNullOrWhiteSpace(device.FriendlyName) ? fallbackName : device.FriendlyName;
            }
            catch
            {
                return fallbackName;
            }
        }

        internal static bool TryResolveConfiguredDeviceByActiveName(
            CycleDevice configured,
            IReadOnlyDictionary<string, string> activeNamesById,
            ISet<string> reservedActiveIds,
            out CycleDevice remappedDevice)
        {
            remappedDevice = new CycleDevice();

            if (activeNamesById == null || reservedActiveIds == null || string.IsNullOrWhiteSpace(configured.Name))
            {
                return false;
            }

            if (!TryResolveUniqueBestMatch(
                activeNamesById,
                candidate => string.IsNullOrWhiteSpace(candidate.Key) || reservedActiveIds.Contains(candidate.Key),
                static candidate => candidate.Value,
                configured.Name,
                out KeyValuePair<string, string> bestCandidate,
                out _))
            {
                return false;
            }

            remappedDevice = CreateCycleDevice(bestCandidate.Key, bestCandidate.Value);
            return true;
        }

        internal static bool TryResolveReconnectedCycleDeviceByName(
            List<MMDevice> activeDevices,
            List<CycleDevice> connectedCycle,
            IReadOnlyList<CycleDevice> skippedDevices,
            out CycleDevice remappedDevice,
            out string matchReason,
            out string configuredName)
        {
            remappedDevice = new CycleDevice();
            matchReason = string.Empty;
            configuredName = string.Empty;

            if (activeDevices == null || connectedCycle == null || skippedDevices == null)
            {
                return false;
            }

            var connectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < connectedCycle.Count; index++)
            {
                string id = connectedCycle[index].Id;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    connectedIds.Add(id);
                }
            }

            foreach (CycleDevice skipped in skippedDevices)
            {
                if (string.IsNullOrWhiteSpace(skipped.Name))
                {
                    continue;
                }

                string normalizedExpected = BluetoothReconnectService.NormalizeForMatch(skipped.Name);
                if (string.IsNullOrWhiteSpace(normalizedExpected))
                {
                    continue;
                }

                if (!TryResolveUniqueBestMatch(
                    activeDevices,
                    device => device == null || connectedIds.Contains(device.ID),
                    device => device == null ? string.Empty : TryGetFriendlyName(device, string.Empty),
                    skipped.Name,
                    out MMDevice bestDevice,
                    out string bestMatchReason))
                {
                    continue;
                }

                remappedDevice = CreateCycleDevice(bestDevice.ID, TryGetFriendlyName(bestDevice, skipped.Name));
                matchReason = bestMatchReason;
                configuredName = skipped.Name;
                return true;
            }

            return false;
        }

        internal static bool TryResolveUniqueBestMatch<TCandidate>(
            IEnumerable<TCandidate> candidates,
            Func<TCandidate, bool> shouldSkip,
            Func<TCandidate, string?> getCandidateName,
            string? expectedName,
            out TCandidate bestCandidate,
            out string bestMatchReason)
        {
            bestCandidate = default!;
            bestMatchReason = string.Empty;

            if (candidates == null || shouldSkip == null || getCandidateName == null || string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }

            string normalizedExpected = BluetoothReconnectService.NormalizeForMatch(expectedName);
            if (string.IsNullOrWhiteSpace(normalizedExpected))
            {
                return false;
            }

            int bestRank = 0;
            int topRankCount = 0;

            foreach (TCandidate candidate in candidates)
            {
                if (shouldSkip(candidate))
                {
                    continue;
                }

                string? candidateName = getCandidateName(candidate);
                if (string.IsNullOrWhiteSpace(candidateName))
                {
                    continue;
                }

                string matchReason = BluetoothReconnectService.ResolveMatchReason(candidateName, expectedName, normalizedExpected);
                if (string.IsNullOrWhiteSpace(matchReason))
                {
                    continue;
                }

                int candidateRank = GetMatchRank(matchReason);
                if (candidateRank > bestRank)
                {
                    bestRank = candidateRank;
                    bestCandidate = candidate;
                    bestMatchReason = matchReason;
                    topRankCount = 1;
                }
                else if (candidateRank == bestRank)
                {
                    topRankCount++;
                }
            }

            return bestRank > 0 && topRankCount == 1;
        }

        internal static CycleDevice CloneCycleDevice(CycleDevice device)
        {
            return CreateCycleDevice(device.Id, device.Name);
        }

        internal static CycleDevice CreateCycleDevice(string? id, string? name)
        {
            return new CycleDevice
            {
                Id = id ?? string.Empty,
                Name = name ?? string.Empty,
            };
        }

        internal static int GetMatchRank(string matchReason)
        {
            return matchReason switch
            {
                "exact" => 4,
                "contains" => 3,
                "normalized-equal" => 2,
                "normalized-contains" => 1,
                _ => 0,
            };
        }
    }
}

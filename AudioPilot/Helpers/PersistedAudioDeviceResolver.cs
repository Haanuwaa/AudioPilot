using AudioPilot.Models;

namespace AudioPilot.Helpers
{
    internal static class PersistedAudioDeviceResolver
    {
        public static CycleDevice? TryResolveMatch(CycleDevice? configuredDevice, IReadOnlyList<CycleDevice>? availableDevices)
        {
            if (configuredDevice == null || string.IsNullOrWhiteSpace(configuredDevice.Id) || availableDevices == null)
            {
                return null;
            }

            if (TryFindById(availableDevices, configuredDevice.Id) is CycleDevice exactMatch)
            {
                return exactMatch;
            }

            if (!string.IsNullOrEmpty(configuredDevice.StableId))
            {
                CycleDevice? match = TryFindByStableId(configuredDevice.StableId, availableDevices);
                if (match != null) return match;
                if (availableDevices.Any(candidate => string.Equals(candidate.StableId, configuredDevice.StableId, StringComparison.Ordinal))) return null;
            }

            return TryResolveUniqueBestNameMatch(configuredDevice.Name, availableDevices);
        }

        internal static CycleDevice? TryFindByStableId(string? stableId, IReadOnlyList<CycleDevice> availableDevices)
        {
            if (string.IsNullOrEmpty(stableId)) return null;
            CycleDevice? match = null;
            foreach (CycleDevice candidate in availableDevices)
            {
                if (!string.Equals(candidate.StableId, stableId, StringComparison.Ordinal)) continue;
                if (match != null) return null;
                match = candidate;
            }
            return match;
        }

        public static CycleDevice? TryResolveUniqueBestNameMatch(string? expectedName, IReadOnlyList<CycleDevice>? availableDevices)
        {
            if (availableDevices == null || string.IsNullOrWhiteSpace(expectedName))
            {
                return null;
            }

            string normalizedExpectedName = BluetoothReconnectService.NormalizeForMatch(expectedName);
            List<(CycleDevice Candidate, string MatchReason)> candidates = [];

            for (int index = 0; index < availableDevices.Count; index++)
            {
                CycleDevice candidate = availableDevices[index];
                if (string.IsNullOrWhiteSpace(candidate.Id))
                {
                    continue;
                }

                string matchReason = BluetoothReconnectService.ResolveMatchReason(candidate.Name, expectedName, normalizedExpectedName);
                if (BluetoothReconnectService.GetMatchRank(matchReason) <= 0)
                {
                    continue;
                }

                candidates.Add((candidate, matchReason));
            }

            if (!BluetoothReconnectService.TrySelectBestUniqueMatch(candidates, out (CycleDevice Candidate, string MatchReason)? selected)
                || selected is null)
            {
                return null;
            }

            return selected.Value.Candidate;
        }

        private static CycleDevice? TryFindById(IReadOnlyList<CycleDevice> availableDevices, string deviceId)
        {
            for (int index = 0; index < availableDevices.Count; index++)
            {
                CycleDevice candidate = availableDevices[index];
                if (string.Equals(candidate.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}

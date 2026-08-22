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

            return TryResolveUniqueBestNameMatch(configuredDevice.Name, availableDevices);
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

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;

namespace AudioPilot.Services.Bluetooth
{
    public interface IBluetoothReconnectService
    {
        Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken);
        Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, string audioEndpointId, string opId, string kind, CancellationToken cancellationToken)
            => TryReconnectPairedAudioDeviceAsync(deviceName, opId, kind, cancellationToken);
        Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, CancellationToken cancellationToken);
        Task<bool> TryReconnectUsingAudioEndpointControlAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken);
    }

    public sealed partial class BluetoothReconnectService : IBluetoothReconnectService
    {
        internal const string BluetoothAssociationEndpointSelector =
            "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";

        internal static readonly string[] RequestedProperties =
        [
            "System.Devices.Aep.IsPaired",
            "System.Devices.Aep.IsConnected",
            "System.Devices.Aep.DeviceAddress"
        ];

        private static readonly string[] MatchNormalizationNoiseTokens =
        [
            "(r)",
            "bluetooth",
            "hands-free ag audio",
            "hands free ag audio",
            "hands-free",
            "hands free",
            "stereo",
            "headset",
            "headphones",
            "speaker",
            "audio",
            "output",
            "input",
            "microphone",
            "\"",
            "'"
        ];

        private readonly Logger _logger;
        private readonly IBluetoothAudioEndpointReconnectFallback _audioEndpointFallback;
        private readonly IBluetoothAssociationEndpointSource _associationEndpointSource;
        private readonly RememberedBluetoothEndpointCache _rememberedAssociationEndpoints = new();

        public BluetoothReconnectService()
            : this(Logger.Instance, new BluetoothAudioEndpointReconnectFallback(Logger.Instance), new BluetoothAssociationEndpointSource(Logger.Instance))
        {
        }

        public BluetoothReconnectService(Logger logger)
            : this(logger, new BluetoothAudioEndpointReconnectFallback(logger), new BluetoothAssociationEndpointSource(logger))
        {
        }

        internal BluetoothReconnectService(
            Logger logger,
            IBluetoothAudioEndpointReconnectFallback audioEndpointFallback,
            IBluetoothAssociationEndpointSource? associationEndpointSource = null)
        {
            _logger = logger;
            _audioEndpointFallback = audioEndpointFallback;
            _associationEndpointSource = associationEndpointSource ?? new BluetoothAssociationEndpointSource(logger);
        }

        public Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, CancellationToken cancellationToken)
        {
            return TryReconnectPairedAudioDeviceAsync(deviceName, opId: "n/a", kind: "unknown", cancellationToken);
        }

        public async Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken)
        {
            return await TryReconnectPairedAudioDeviceAsync(deviceName, string.Empty, opId, kind, cancellationToken);
        }

        public async Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, string audioEndpointId, string opId, string kind, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Skip} | opId={opId} kind={kind} reason=empty-device-name");
                }

                return false;
            }

            string normalizedExpectedName = NormalizeForMatch(deviceName);
            string rememberedDeviceKey = ResolveRememberedDeviceKey(deviceName);
            string rememberedEndpointId = TryGetRememberedAssociationEndpointId(rememberedDeviceKey, out string cachedEndpointId)
                ? cachedEndpointId
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(rememberedEndpointId)
                && await TryReconnectLastSuccessfulAssociationEndpointAsync(deviceName, normalizedExpectedName, rememberedDeviceKey, rememberedEndpointId, opId, kind, cancellationToken))
            {
                return true;
            }

            IReadOnlyList<BluetoothAssociationEndpointCandidate> devices = await _associationEndpointSource.GetAssociationEndpointsAsync(opId, kind, cancellationToken);

            var pairedDisconnected = new List<BluetoothAssociationEndpointCandidate>(devices.Count);
            for (int index = 0; index < devices.Count; index++)
            {
                BluetoothAssociationEndpointCandidate device = devices[index];
                if (device.IsPaired && !device.IsConnected)
                {
                    pairedDisconnected.Add(device);
                }
            }

            var candidates = new List<(BluetoothAssociationEndpointCandidate Device, string MatchReason)>(pairedDisconnected.Count);
            for (int index = 0; index < pairedDisconnected.Count; index++)
            {
                BluetoothAssociationEndpointCandidate device = pairedDisconnected[index];
                string reason = DoesAudioEndpointMatchDeviceAddress(audioEndpointId, device.DeviceAddress)
                    ? "device-address"
                    : ResolveMatchReason(device.Name, deviceName, normalizedExpectedName);
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    candidates.Add((device, reason));
                }
            }

            string preferredEndpointId = TryGetRememberedAssociationEndpointId(rememberedDeviceKey, out string preferredCachedEndpointId)
                ? preferredCachedEndpointId
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(preferredEndpointId)
                && !candidates.Any(candidate => candidate.Device.Id.Equals(preferredEndpointId, StringComparison.OrdinalIgnoreCase)))
            {
                ForgetRememberedAssociationEndpointId(rememberedDeviceKey);
                preferredEndpointId = string.Empty;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "BluetoothReconnect",
                    () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Candidates} | opId={opId} kind={kind} expected={LogPrivacy.Device(deviceName)} normalizedExpected={LogPrivacy.Label(normalizedExpectedName)} pairedDisconnected={pairedDisconnected.Count} matched={candidates.Count}");
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace(
                    "BluetoothReconnect",
                    () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Candidates} | opId={opId} kind={kind} pairedDisconnectedNames={BuildCandidateSummary(pairedDisconnected)} matchedNames={BuildMatchedSummary(candidates)}");
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            List<(BluetoothAssociationEndpointCandidate Candidate, string MatchReason)> prioritizedCandidates = OrderAssociationEndpointCandidates(candidates, preferredEndpointId);
            if (prioritizedCandidates.Count == 0)
            {
                return false;
            }

            int topRank = GetMatchRank(prioritizedCandidates[0].MatchReason);
            int topRankCount = 0;
            for (int index = 0; index < prioritizedCandidates.Count; index++)
            {
                if (GetMatchRank(prioritizedCandidates[index].MatchReason) != topRank)
                {
                    break;
                }

                topRankCount++;
            }

            if (topRankCount > 1 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "BluetoothReconnect",
                    () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Match} | opId={opId} kind={kind} candidate=multiple reason=ambiguous-top-rank matched={candidates.Count} topRank={topRank} topRankCount={topRankCount}");
            }

            for (int index = 0; index < prioritizedCandidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                (BluetoothAssociationEndpointCandidate candidate, string matchReason) = prioritizedCandidates[index];
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    int attemptNumber = index + 1;
                    _logger.Debug(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Match} | opId={opId} kind={kind} candidate={LogPrivacy.Device(candidate.Name)} reason={matchReason} candidateAttempt={attemptNumber} candidateTotal={prioritizedCandidates.Count}");
                }

                if (await TryPairAssociationEndpointCandidateAsync(candidate, rememberedDeviceKey, opId, kind, allowRememberedIdUpdate: true, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<bool> TryReconnectLastSuccessfulAssociationEndpointAsync(
            string expectedDeviceName,
            string normalizedExpectedName,
            string rememberedDeviceKey,
            string endpointId,
            string opId,
            string kind,
            CancellationToken cancellationToken)
        {
            BluetoothAssociationEndpointCandidate? cachedCandidate = await _associationEndpointSource.TryGetAssociationEndpointByIdAsync(
                endpointId,
                opId,
                kind,
                cancellationToken);
            if (cachedCandidate == null)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Match} | opId={opId} kind={kind} candidate=none reason=cached-id-miss endpointId={LogPrivacy.Id(endpointId)} action=fallback-snapshot");
                }

                return false;
            }

            if (!cachedCandidate.IsPaired || cachedCandidate.IsConnected)
            {
                ForgetRememberedAssociationEndpointId(rememberedDeviceKey);
                return false;
            }

            string cachedMatchReason = ResolveMatchReason(cachedCandidate.Name, expectedDeviceName, normalizedExpectedName);
            if (!ShouldReuseRememberedAssociationEndpoint(cachedMatchReason))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Match} | opId={opId} kind={kind} candidate={LogPrivacy.Device(cachedCandidate.Name)} reason=cached-id-rejected matchReason={(string.IsNullOrWhiteSpace(cachedMatchReason) ? "none" : cachedMatchReason)}");
                }

                ForgetRememberedAssociationEndpointId(rememberedDeviceKey);
                return false;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "BluetoothReconnect",
                    () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Match} | opId={opId} kind={kind} candidate={LogPrivacy.Device(cachedCandidate.Name)} reason=cached-id matchReason={cachedMatchReason}");
            }

            bool connected = await TryPairAssociationEndpointCandidateAsync(cachedCandidate, rememberedDeviceKey, opId, kind, allowRememberedIdUpdate: false, cancellationToken);
            if (connected)
            {
                RememberAssociationEndpointId(rememberedDeviceKey, cachedCandidate.Id);
                return true;
            }

            ForgetRememberedAssociationEndpointId(rememberedDeviceKey);
            return false;
        }

        private async Task<bool> TryObserveAssociationEndpointConnectedAsync(
            BluetoothAssociationEndpointCandidate candidate,
            string opId,
            string kind,
            CancellationToken cancellationToken)
        {
            int probeAttempts = RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts;
            int probeDelayMs = RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs;

            for (int attempt = 1; attempt <= probeAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                BluetoothAssociationEndpointCandidate? refreshedCandidate = await _associationEndpointSource.TryGetAssociationEndpointByIdAsync(
                    candidate.Id,
                    opId,
                    kind,
                    cancellationToken);

                if (refreshedCandidate?.IsPaired == true && refreshedCandidate.IsConnected)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.PairResult} | opId={opId} kind={kind} candidate={LogPrivacy.Device(candidate.Name)} status=AlreadyPaired connected=true source=post-pair-observation checks={attempt}");
                    }

                    return true;
                }

                if (attempt < probeAttempts)
                {
                    await Task.Delay(probeDelayMs, cancellationToken);
                }
            }

            return false;
        }

        private async Task<bool> TryPairAssociationEndpointCandidateAsync(
            BluetoothAssociationEndpointCandidate candidate,
            string rememberedDeviceKey,
            string opId,
            string kind,
            bool allowRememberedIdUpdate,
            CancellationToken cancellationToken)
        {
            try
            {
                BluetoothAssociationEndpointPairAttempt result = await candidate.TryPairAsync(cancellationToken);

                if (result.Connected)
                {
                    if (allowRememberedIdUpdate && !string.IsNullOrWhiteSpace(rememberedDeviceKey))
                    {
                        RememberAssociationEndpointId(rememberedDeviceKey, candidate.Id);
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.PairResult} | opId={opId} kind={kind} candidate={LogPrivacy.Device(candidate.Name)} status={result.Status} connected={result.Connected}");
                    }

                    return true;
                }

                if (string.Equals(result.Status, DevicePairingResultStatus.AlreadyPaired.ToString(), StringComparison.OrdinalIgnoreCase)
                    && await TryObserveAssociationEndpointConnectedAsync(candidate, opId, kind, cancellationToken))
                {
                    if (allowRememberedIdUpdate && !string.IsNullOrWhiteSpace(rememberedDeviceKey))
                    {
                        RememberAssociationEndpointId(rememberedDeviceKey, candidate.Id);
                    }

                    return true;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.PairResult} | opId={opId} kind={kind} candidate={LogPrivacy.Device(candidate.Name)} status={result.Status} connected={result.Connected}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "BluetoothReconnect",
                    () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.PairResult} | opId={opId} kind={kind} candidate={LogPrivacy.Device(candidate.Name)} status=exception exceptionType={ex.GetType().Name} hresult=0x{ex.HResult:X8}",
                    nameof(TryPairAssociationEndpointCandidateAsync),
                    ex);
            }

            return false;
        }

        private bool TryGetRememberedAssociationEndpointId(string rememberedDeviceKey, out string endpointId)
        {
            return _rememberedAssociationEndpoints.TryGetEndpointId(rememberedDeviceKey, out endpointId);
        }

        private void RememberAssociationEndpointId(string rememberedDeviceKey, string endpointId)
        {
            _rememberedAssociationEndpoints.RememberEndpointId(rememberedDeviceKey, endpointId);
        }

        private void ForgetRememberedAssociationEndpointId(string rememberedDeviceKey)
        {
            _rememberedAssociationEndpoints.ForgetEndpointId(rememberedDeviceKey);
        }

        public async Task<bool> TryReconnectUsingAudioEndpointControlAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return false;
            }

            try
            {
                return await _audioEndpointFallback.TryReconnectAsync(deviceName, opId, kind, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "BluetoothReconnect",
                    () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=exception",
                    nameof(TryReconnectUsingAudioEndpointControlAsync),
                    ex);
                return false;
            }
        }

        internal static string ResolveMatchReason(string? discoveredName, string expectedName, string normalizedExpected)
        {
            if (string.IsNullOrWhiteSpace(discoveredName))
            {
                return string.Empty;
            }

            string discoveredTrimmed = discoveredName.Trim();

            if (string.Equals(discoveredTrimmed, expectedName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return "exact";
            }

            if (discoveredTrimmed.Contains(expectedName, StringComparison.OrdinalIgnoreCase)
                || expectedName.Contains(discoveredTrimmed, StringComparison.OrdinalIgnoreCase))
            {
                return "contains";
            }

            string normalizedDiscovered = NormalizeForMatch(discoveredTrimmed);
            if (string.IsNullOrWhiteSpace(normalizedDiscovered)
                || string.IsNullOrWhiteSpace(normalizedExpected))
            {
                return string.Empty;
            }

            if (string.Equals(normalizedDiscovered, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            {
                return "normalized-equal";
            }

            if (normalizedDiscovered.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase)
                || normalizedExpected.Contains(normalizedDiscovered, StringComparison.OrdinalIgnoreCase))
            {
                return "normalized-contains";
            }

            return string.Empty;
        }

        internal static string ResolveRememberedDeviceKey(string? value)
        {
            string normalized = NormalizeForMatch(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            return value?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        internal static int GetMatchRank(string matchReason)
        {
            return matchReason switch
            {
                "exact" => 4,
                "device-address" => 5,
                "contains" => 3,
                "normalized-equal" => 2,
                "normalized-contains" => 1,
                _ => 0,
            };
        }

        internal static bool DoesAudioEndpointMatchDeviceAddress(string? audioEndpointId, string? deviceAddress)
        {
            if (string.IsNullOrWhiteSpace(audioEndpointId) || string.IsNullOrWhiteSpace(deviceAddress))
            {
                return false;
            }

            string normalizedAddress = new([.. deviceAddress.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant)]);
            if (normalizedAddress.Length < 12)
            {
                return false;
            }

            string normalizedEndpointId = new([.. audioEndpointId.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant)]);
            return normalizedEndpointId.Contains(normalizedAddress, StringComparison.Ordinal);
        }

        internal static bool ShouldReuseRememberedAssociationEndpoint(string matchReason)
        {
            return GetMatchRank(matchReason) >= GetMatchRank("normalized-equal");
        }

        internal static bool TrySelectBestUniqueMatch<T>(
            IReadOnlyList<(T Candidate, string MatchReason)> candidates,
            out (T Candidate, string MatchReason)? selected)
        {
            selected = null;
            if (candidates == null || candidates.Count == 0)
            {
                return false;
            }

            int bestRank = 0;
            int topRankCount = 0;
            int bestCandidateIndex = -1;

            for (int index = 0; index < candidates.Count; index++)
            {
                string matchReason = candidates[index].MatchReason;
                int candidateRank = GetMatchRank(matchReason);
                if (candidateRank <= 0)
                {
                    continue;
                }

                if (candidateRank > bestRank)
                {
                    bestRank = candidateRank;
                    topRankCount = 1;
                    bestCandidateIndex = index;
                }
                else if (candidateRank == bestRank)
                {
                    topRankCount++;
                }
            }

            if (bestCandidateIndex < 0 || topRankCount != 1)
            {
                return false;
            }

            selected = candidates[bestCandidateIndex];
            return true;
        }

        internal static List<(T Candidate, string MatchReason)> OrderCandidatesByMatchPriority<T>(
            IReadOnlyList<(T Candidate, string MatchReason)> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return [];
            }

            var rankedCandidates = new List<(T Candidate, string MatchReason, int Rank, int Index)>(candidates.Count);
            for (int index = 0; index < candidates.Count; index++)
            {
                (T candidate, string matchReason) = candidates[index];
                int rank = GetMatchRank(matchReason);
                if (rank <= 0)
                {
                    continue;
                }

                rankedCandidates.Add((candidate, matchReason, rank, index));
            }

            rankedCandidates.Sort(static (left, right) =>
            {
                int rankComparison = right.Rank.CompareTo(left.Rank);
                if (rankComparison != 0)
                {
                    return rankComparison;
                }

                return left.Index.CompareTo(right.Index);
            });

            return [.. rankedCandidates.Select(static candidate => (candidate.Candidate, candidate.MatchReason))];
        }

        internal static List<(BluetoothAssociationEndpointCandidate Candidate, string MatchReason)> OrderAssociationEndpointCandidates(
            IReadOnlyList<(BluetoothAssociationEndpointCandidate Candidate, string MatchReason)> candidates,
            string? preferredEndpointId)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return [];
            }

            if (string.IsNullOrWhiteSpace(preferredEndpointId))
            {
                return OrderCandidatesByMatchPriority(candidates);
            }

            var rankedCandidates = new List<(BluetoothAssociationEndpointCandidate Candidate, string MatchReason, int Rank, bool Preferred, int Index)>(candidates.Count);
            for (int index = 0; index < candidates.Count; index++)
            {
                (BluetoothAssociationEndpointCandidate candidate, string matchReason) = candidates[index];
                int rank = GetMatchRank(matchReason);
                if (rank <= 0)
                {
                    continue;
                }

                rankedCandidates.Add((
                    candidate,
                    matchReason,
                    rank,
                    candidate.Id.Equals(preferredEndpointId, StringComparison.OrdinalIgnoreCase),
                    index));
            }

            rankedCandidates.Sort(static (left, right) =>
            {
                int rankComparison = right.Rank.CompareTo(left.Rank);
                if (rankComparison != 0)
                {
                    return rankComparison;
                }

                int preferredComparison = right.Preferred.CompareTo(left.Preferred);
                if (preferredComparison != 0)
                {
                    return preferredComparison;
                }

                return left.Index.CompareTo(right.Index);
            });

            return [.. rankedCandidates.Select(static candidate => (candidate.Candidate, candidate.MatchReason))];
        }

        internal static string NormalizeForMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = value.Trim().ToLowerInvariant();
            foreach (string token in MatchNormalizationNoiseTokens)
            {
                normalized = normalized.Replace(token, string.Empty, StringComparison.Ordinal);
            }

            normalized = SeparatorRegex().Replace(normalized, string.Empty);
            normalized = WhitespaceRegex().Replace(normalized, " ").Trim();

            return normalized;
        }

        private static string BuildCandidateSummary(IEnumerable<BluetoothAssociationEndpointCandidate> candidates)
        {
            StringBuilder builder = new();
            foreach (BluetoothAssociationEndpointCandidate candidate in candidates)
            {
                if (builder.Length > 0)
                {
                    builder.Append(';');
                }

                builder.Append(LogPrivacy.Device(candidate.Name));
            }

            return builder.ToString();
        }

        private static string BuildMatchedSummary(IEnumerable<(BluetoothAssociationEndpointCandidate Candidate, string MatchReason)> matches)
        {
            StringBuilder builder = new();
            foreach ((BluetoothAssociationEndpointCandidate candidate, string reason) in matches)
            {
                if (builder.Length > 0)
                {
                    builder.Append(';');
                }

                builder.Append(LogPrivacy.Device(candidate.Name));
                builder.Append('(');
                builder.Append(reason);
                builder.Append(')');
            }

            return builder.ToString();
        }

        internal static string GetPackageIdentityContext()
        {
            try
            {
                Package package = Package.Current;
                return package?.Id?.Name ?? "packaged-unknown";
            }
            catch
            {
                return "unpackaged";
            }
        }

        internal static bool IsPropertyKeySyntaxFailure(COMException exception)
        {
            const int TypeElementNotFoundHResult = unchecked((int)0x8002802B);
            return exception.HResult == TypeElementNotFoundHResult;
        }

        [GeneratedRegex("[\\(\\)\\[\\]{}._,:/\\\\+-]", RegexOptions.CultureInvariant)]
        private static partial Regex SeparatorRegex();

        [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
        private static partial Regex WhitespaceRegex();

        internal static bool IsPaired(DeviceInformation device)
        {
            return TryReadBoolean(device, "System.Devices.Aep.IsPaired") ?? false;
        }

        internal static bool IsConnected(DeviceInformation device)
        {
            return TryReadBoolean(device, "System.Devices.Aep.IsConnected") ?? false;
        }

        internal static bool? TryReadBoolean(DeviceInformation device, string propertyName)
        {
            if (!device.Properties.TryGetValue(propertyName, out object? value) || value == null)
            {
                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is IConvertible convertible)
            {
                try
                {
                    return convertible.ToBoolean(System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }
    }
}

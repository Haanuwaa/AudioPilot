using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using Windows.Devices.Enumeration;

namespace AudioPilot.Tests.Services.Bluetooth;

public sealed class BluetoothReconnectServiceTests
{
    [Fact]
    public async Task TryReconnectUsingAudioEndpointControlAsync_ReturnsFalse_WhenDeviceNameIsEmpty()
    {
        var service = new BluetoothReconnectService(Logger.Instance, new FakeBluetoothAudioEndpointReconnectFallback());

        bool result = await service.TryReconnectUsingAudioEndpointControlAsync(string.Empty, "op-1", "output", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task TryReconnectUsingAudioEndpointControlAsync_ReturnsFalse_WhenFallbackThrows()
    {
        var fallback = new FakeBluetoothAudioEndpointReconnectFallback
        {
            ExceptionToThrow = new InvalidOperationException("boom")
        };
        var service = new BluetoothReconnectService(Logger.Instance, fallback);

        bool result = await service.TryReconnectUsingAudioEndpointControlAsync("Headset", "op-1", "output", CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task TryReconnectUsingAudioEndpointControlAsync_PropagatesCancellation()
    {
        var fallback = new FakeBluetoothAudioEndpointReconnectFallback
        {
            ExceptionToThrow = new OperationCanceledException()
        };
        var service = new BluetoothReconnectService(Logger.Instance, fallback);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.TryReconnectUsingAudioEndpointControlAsync("Headset", "op-1", "output", CancellationToken.None));
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_ReusesLastSuccessfulAssociationEndpointId()
    {
        var source = new FakeBluetoothAssociationEndpointSource();
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-1", "Bluetooth Headset", connected: true)
        ];

        var service = new BluetoothReconnectService(Logger.Instance, new FakeBluetoothAudioEndpointReconnectFallback(), source);

        bool first = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-1", "output", CancellationToken.None);

        source.SnapshotCandidates = [];
        source.CandidateById = source.CreateCandidate("endpoint-1", "Bluetooth Headset", connected: true);

        bool second = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-2", "output", CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, source.PairCalls);
        Assert.Equal(1, source.GetEndpointsCalls);
        Assert.Equal(1, source.TryGetByIdCalls);
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_FallsBackToFuzzyMatching_WhenRememberedAssociationEndpointIdIsUnavailable()
    {
        var source = new FakeBluetoothAssociationEndpointSource();
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-1", "Bluetooth Headset", connected: true)
        ];

        var service = new BluetoothReconnectService(Logger.Instance, new FakeBluetoothAudioEndpointReconnectFallback(), source);

        bool first = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-1", "output", CancellationToken.None);

        source.CandidateById = null;
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-2", "Bluetooth Headset", connected: true)
        ];

        bool second = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-2", "output", CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, source.GetEndpointsCalls);
        Assert.Equal(1, source.TryGetByIdCalls);
        Assert.Equal(2, source.PairCalls);
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_InvalidatesFailedRememberedAssociationEndpointId()
    {
        var source = new FakeBluetoothAssociationEndpointSource();
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-1", "Bluetooth Headset", connected: true)
        ];

        var service = new BluetoothReconnectService(Logger.Instance, new FakeBluetoothAudioEndpointReconnectFallback(), source);

        bool first = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-1", "output", CancellationToken.None);

        source.CandidateById = source.CreateCandidate("endpoint-1", "Bluetooth Headset", connected: false);
        source.SnapshotCandidates = [];

        bool second = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-2", "output", CancellationToken.None);

        source.CandidateById = null;
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-2", "Bluetooth Headset", connected: true)
        ];

        bool third = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-3", "output", CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
        Assert.Equal(1, source.TryGetByIdCalls);
        Assert.Equal(3, source.GetEndpointsCalls);
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_RejectsMismatchedRememberedAssociationEndpoint_AndFallsBackToSnapshotOrdering()
    {
        var source = new FakeBluetoothAssociationEndpointSource();
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-a", "Bluetooth Headset", connected: true)
        ];

        var service = new BluetoothReconnectService(Logger.Instance, new FakeBluetoothAudioEndpointReconnectFallback(), source);

        bool first = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-1", "output", CancellationToken.None);

        source.CandidateById = source.CreateCandidate("endpoint-a", "Desk Speakers", connected: true);
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-b", "Bluetooth Headset", connected: true)
        ];

        bool second = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-2", "output", CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, source.TryGetByIdCalls);
        Assert.Equal(2, source.GetEndpointsCalls);
        Assert.Equal(["endpoint-a", "endpoint-b"], source.PairedEndpointIds);
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_AttemptsAmbiguousTopRankCandidatesInOrder()
    {
        var source = new FakeBluetoothAssociationEndpointSource();
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-1", "Bluetooth Headset Stereo", connected: false),
            source.CreateCandidate("endpoint-2", "Bluetooth Headset Hands-Free AG Audio", connected: true),
        ];

        var service = new BluetoothReconnectService(Logger.Instance, new FakeBluetoothAudioEndpointReconnectFallback(), source);

        bool result = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-ambiguous", "output", CancellationToken.None);

        Assert.True(result);
        Assert.Equal(2, source.PairCalls);
        Assert.Equal(["endpoint-1", "endpoint-2"], source.PairedEndpointIds);
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_PrefersRememberedEndpointId_WhenAmbiguousSnapshotCandidatesRemain()
    {
        var source = new FakeBluetoothAssociationEndpointSource();
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-2", "Bluetooth Headset Hands-Free AG Audio", connected: true)
        ];

        var service = new BluetoothReconnectService(Logger.Instance, new FakeBluetoothAudioEndpointReconnectFallback(), source);

        bool first = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-1", "output", CancellationToken.None);

        source.CandidateById = null;
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-1", "Bluetooth Headset Stereo", connected: false),
            source.CreateCandidate("endpoint-2", "Bluetooth Headset Hands-Free AG Audio", connected: true),
        ];

        bool second = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-2", "output", CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(["endpoint-2", "endpoint-2"], source.PairedEndpointIds);
    }

    [Theory]
    [InlineData("WH-1000XM4 Stereo", "WH-1000XM4 Hands-Free AG Audio", true)]
    [InlineData("Galaxy Buds2 Stereo", "Galaxy Buds2", true)]
    [InlineData("AirPods Pro Hands-Free AG Audio", "AirPods Pro Stereo", true)]
    [InlineData("USB DAC", "Bluetooth Headset", false)]
    [InlineData("Speakers (Realtek(R) Audio)", "Galaxy Buds2 Stereo", false)]
    public void ResolveMatchReason_HandlesNormalizedBluetoothVariants(string discoveredName, string expectedName, bool shouldMatch)
    {
        string normalizedExpected = BluetoothReconnectService.NormalizeForMatch(expectedName);
        string reason = BluetoothReconnectService.ResolveMatchReason(discoveredName, expectedName, normalizedExpected);

        if (shouldMatch)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
        else
        {
            Assert.True(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Theory]
    [InlineData("WH-1000XM4 Stereo", "wh1000xm4")]
    [InlineData("Galaxy Buds2 Hands-Free AG Audio", "galaxy buds2")]
    [InlineData("AirPods Pro Bluetooth Stereo", "airpods pro")]
    public void NormalizeForMatch_RemovesRoleAndBluetoothNoise(string source, string expected)
    {
        string normalized = BluetoothReconnectService.NormalizeForMatch(source);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("Bluetooth Headset", "bluetooth headset")]
    [InlineData("  Bluetooth Headphones  ", "bluetooth headphones")]
    [InlineData("WH-1000XM4 Stereo", "wh1000xm4")]
    public void ResolveRememberedDeviceKey_FallsBackWhenNormalizationIsEmpty(string source, string expected)
    {
        string key = BluetoothReconnectService.ResolveRememberedDeviceKey(source);

        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData(DevicePairingResultStatus.Paired, true)]
    [InlineData(DevicePairingResultStatus.AlreadyPaired, false)]
    [InlineData(DevicePairingResultStatus.Failed, false)]
    public void ShouldTreatPairStatusAsConnected_OnlyAcceptsPaired(DevicePairingResultStatus status, bool expected)
    {
        bool connected = BluetoothAssociationEndpointSource.ShouldTreatPairStatusAsConnected(status);

        Assert.Equal(expected, connected);
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_AlreadyPairedAssociationEndpointResultDoesNotCountAsReconnectSuccess()
    {
        var source = new FakeBluetoothAssociationEndpointSource();
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-1", "Bluetooth Headset", pairStatus: "AlreadyPaired", connected: false)
        ];

        var service = new BluetoothReconnectService(Logger.Instance, new FakeBluetoothAudioEndpointReconnectFallback(), source);

        bool result = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-already-paired", "output", CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, source.PairCalls);
        Assert.Equal(["endpoint-1"], source.PairedEndpointIds);
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_AlreadyPairedAssociationEndpointResultSucceedsWhenEndpointBecomesConnected()
    {
        int originalProbeAttempts = RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts;
        int originalProbeDelayMs = RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs;
        RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts = 1;
        RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs = 25;

        try
        {
            var source = new FakeBluetoothAssociationEndpointSource();
            source.SnapshotCandidates =
            [
                source.CreateCandidate(
                    "endpoint-1",
                    "Bluetooth Headset",
                    connected: false,
                    pairStatus: "AlreadyPaired",
                    onPair: () => source.CandidateById = new BluetoothAssociationEndpointCandidate(
                        "endpoint-1",
                        "Bluetooth Headset",
                        IsPaired: true,
                        IsConnected: true,
                        TryPairAsync: _ => Task.FromResult(new BluetoothAssociationEndpointPairAttempt(false, "AlreadyPaired"))))
            ];

            var fallback = new FakeBluetoothAudioEndpointReconnectFallback();
            var service = new BluetoothReconnectService(Logger.Instance, fallback, source);

            bool result = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-already-paired-observed", "output", CancellationToken.None);

            Assert.True(result);
            Assert.Equal(1, source.PairCalls);
            Assert.Equal(1, source.TryGetByIdCalls);
            Assert.Equal(0, fallback.Calls);
        }
        finally
        {
            RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts = originalProbeAttempts;
            RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs = originalProbeDelayMs;
        }
    }

    [Fact]
    public async Task TryReconnectPairedAudioDeviceAsync_LogsTraceWhenRememberedAssociationEndpointIdMissesAndSnapshotFallbackContinues()
    {
        using var loggerScope = new TestLoggerScope(nameof(TryReconnectPairedAudioDeviceAsync_LogsTraceWhenRememberedAssociationEndpointIdMissesAndSnapshotFallbackContinues), "bluetooth-cached-id-miss.log", LogLevel.Trace);

        var source = new FakeBluetoothAssociationEndpointSource();
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-1", "Bluetooth Headset", connected: true)
        ];

        var service = new BluetoothReconnectService(loggerScope.Logger, new FakeBluetoothAudioEndpointReconnectFallback(), source);

        bool first = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-1", "output", CancellationToken.None);

        source.CandidateById = null;
        source.SnapshotCandidates =
        [
            source.CreateCandidate("endpoint-2", "Bluetooth Headset", connected: true)
        ];

        bool second = await service.TryReconnectPairedAudioDeviceAsync("Bluetooth Headset", "op-2", "output", CancellationToken.None);

        Assert.True(first);
        Assert.True(second);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("reason=cached-id-miss", logText, StringComparison.Ordinal);
        Assert.Contains("action=fallback-snapshot", logText, StringComparison.Ordinal);
        Assert.Contains("opId=op-2", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void LogEndpointSourceDiagnostics_EmitsWatcherCacheTimingFields()
    {
        using var loggerScope = new TestLoggerScope(nameof(BluetoothReconnectServiceTests), "bluetooth-source-diagnostics.log", LogLevel.Debug);

        BluetoothAssociationEndpointSource.LogEndpointSourceDiagnostics(
            loggerScope.Logger,
            opId: "op-source",
            kind: "output",
            mode: "snapshot",
            source: "watcher-cache",
            elapsedMs: 7,
            candidateCount: 2);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains("bluetooth-reconnect-source | opId=op-source kind=output mode=snapshot source=watcher-cache elapsedMs=7 candidates=2", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void TrySelectBestUniqueMatch_PrefersHigherRankRegardlessOfOrder()
    {
        List<(string Candidate, string MatchReason)> candidates =
        [
            ("candidate-a", "normalized-equal"),
            ("candidate-b", "exact"),
            ("candidate-c", "contains"),
        ];

        bool selected = BluetoothReconnectService.TrySelectBestUniqueMatch(candidates, out (string Candidate, string MatchReason)? best);

        Assert.True(selected);
        Assert.Equal(("candidate-b", "exact"), best);
    }

    [Fact]
    public void TrySelectBestUniqueMatch_ReturnsFalse_WhenTopRankIsAmbiguous()
    {
        List<(string Candidate, string MatchReason)> candidates =
        [
            ("candidate-a", "contains"),
            ("candidate-b", "contains"),
            ("candidate-c", "normalized-equal"),
        ];

        bool selected = BluetoothReconnectService.TrySelectBestUniqueMatch(candidates, out (string Candidate, string MatchReason)? best);

        Assert.False(selected);
        Assert.Null(best);
    }

    [Fact]
    public void OrderCandidatesByMatchPriority_PrefersHigherRankAndPreservesSourceOrderForTies()
    {
        List<(string Candidate, string MatchReason)> candidates =
        [
            ("candidate-a", "contains"),
            ("candidate-b", "exact"),
            ("candidate-c", "contains"),
        ];

        List<(string Candidate, string MatchReason)> ordered = BluetoothReconnectService.OrderCandidatesByMatchPriority(candidates);

        Assert.Equal(
            [
                ("candidate-b", "exact"),
                ("candidate-a", "contains"),
                ("candidate-c", "contains"),
            ],
            ordered);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("contains", true)]
    [InlineData("normalized-equal", true)]
    [InlineData("normalized-contains", false)]
    [InlineData("", false)]
    public void ShouldReuseRememberedAssociationEndpoint_AllowsOnlyStrongMatches(string matchReason, bool expected)
    {
        bool reusable = BluetoothReconnectService.ShouldReuseRememberedAssociationEndpoint(matchReason);

        Assert.Equal(expected, reusable);
    }

    [Theory]
    [InlineData("{0.0.0.00000000}.{BTHENUM_DEV_001122AABBCC}", "00:11:22:AA:BB:CC", true)]
    [InlineData("BTHENUM\\DEV_001122AABBCC", "00-11-22-AA-BB-CC", true)]
    [InlineData("BTHENUM\\DEV_001122AABBCC", "00:11:22:DD:EE:FF", false)]
    [InlineData("endpoint-without-address", "00:11:22:AA:BB:CC", false)]
    public void DoesAudioEndpointMatchDeviceAddress_NormalizesStableBluetoothIdentity(
        string endpointId,
        string deviceAddress,
        bool expected)
    {
        Assert.Equal(expected, BluetoothReconnectService.DoesAudioEndpointMatchDeviceAddress(endpointId, deviceAddress));
    }

    private sealed class FakeBluetoothAudioEndpointReconnectFallback : IBluetoothAudioEndpointReconnectFallback
    {
        public int Calls { get; private set; }
        public Exception? ExceptionToThrow { get; set; }
        public bool Result { get; set; }

        public Task<bool> TryReconnectAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken)
        {
            Calls++;
            if (ExceptionToThrow != null)
            {
                return Task.FromException<bool>(ExceptionToThrow);
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeBluetoothAssociationEndpointSource : IBluetoothAssociationEndpointSource
    {
        public IReadOnlyList<BluetoothAssociationEndpointCandidate> SnapshotCandidates { get; set; } = [];
        public BluetoothAssociationEndpointCandidate? CandidateById { get; set; }
        public int GetEndpointsCalls { get; private set; }
        public int TryGetByIdCalls { get; private set; }
        public int PairCalls { get; private set; }
        public List<string> PairedEndpointIds { get; } = [];

        public Task<IReadOnlyList<BluetoothAssociationEndpointCandidate>> GetAssociationEndpointsAsync(string opId, string kind, CancellationToken cancellationToken)
        {
            GetEndpointsCalls++;
            return Task.FromResult(SnapshotCandidates);
        }

        public Task<BluetoothAssociationEndpointCandidate?> TryGetAssociationEndpointByIdAsync(string endpointId, string opId, string kind, CancellationToken cancellationToken)
        {
            TryGetByIdCalls++;
            if (CandidateById != null && CandidateById.Id.Equals(endpointId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<BluetoothAssociationEndpointCandidate?>(CandidateById);
            }

            return Task.FromResult<BluetoothAssociationEndpointCandidate?>(null);
        }

        public BluetoothAssociationEndpointCandidate CreateCandidate(string id, string name, bool connected, string pairStatus = "Paired", Action? onPair = null)
        {
            return new BluetoothAssociationEndpointCandidate(
                id,
                name,
                IsPaired: true,
                IsConnected: false,
                TryPairAsync: _ =>
                {
                    PairCalls++;
                    PairedEndpointIds.Add(id);
                    onPair?.Invoke();
                    return Task.FromResult(new BluetoothAssociationEndpointPairAttempt(connected, pairStatus));
                });
        }
    }
}

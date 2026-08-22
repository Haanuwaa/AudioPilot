using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.ViewModels;

public sealed class MixerViewModelChurnTests
{
    [Fact]
    public void ShouldApplyVolumeImmediately_RespectsThrottleInterval()
    {
        DateTime lastApplied = DateTime.UtcNow;

        bool immediate = MixerViewModel.ShouldApplyVolumeImmediately(lastApplied.AddMilliseconds(60), lastApplied, 50);
        bool throttled = MixerViewModel.ShouldApplyVolumeImmediately(lastApplied.AddMilliseconds(10), lastApplied, 50);

        Assert.True(immediate);
        Assert.False(throttled);
    }

    [Fact]
    public void ShouldUseTrailingEdgeOnly_ReturnsTrueForEndpointRows()
    {
        var master = new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false);
        var mic = new AudioSessionItem("Microphone Volume", 50f, isMaster: false, isMic: true);
        var app = new AudioSessionItem("Discord", 50f, isMaster: false, isMic: false, processId: 42);

        Assert.True(MixerViewModel.ShouldUseTrailingEdgeOnly(master));
        Assert.True(MixerViewModel.ShouldUseTrailingEdgeOnly(mic));
        Assert.False(MixerViewModel.ShouldUseTrailingEdgeOnly(app));
    }

    [Fact]
    public void ApplyEndpointMuteStateFromSystem_UpdatesEndpointRowsWithoutWritingBack()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        var master = new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false);
        var microphone = new AudioSessionItem("Microphone Volume", 50f, isMaster: false, isMic: true);
        var application = new AudioSessionItem("Discord", 50f, isMaster: false, isMic: false, processId: 42);
        int muteWriteBacks = 0;
        master.MuteChanged += _ => muteWriteBacks++;
        microphone.MuteChanged += _ => muteWriteBacks++;

        mixer.Sessions.Add(master);
        mixer.Sessions.Add(microphone);
        mixer.Sessions.Add(application);

        mixer.ApplyEndpointMuteStateFromSystem(playbackMuted: true, microphoneMuted: true);

        Assert.True(master.IsMuted);
        Assert.True(microphone.IsMuted);
        Assert.False(application.IsMuted);
        Assert.Equal(0, muteWriteBacks);
    }

    [Fact]
    public void ApplyEndpointStateFromSystem_UpdatesOnlyMatchingEndpointRowWithoutWritingBack()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        var master = new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false);
        var microphone = new AudioSessionItem("Microphone Volume", 40f, isMaster: false, isMic: true);
        var application = new AudioSessionItem("Discord", 30f, isMaster: false, isMic: false, processId: 42);
        int writeBacks = 0;
        master.VolumeChanged += _ => writeBacks++;
        master.MuteChanged += _ => writeBacks++;

        mixer.Sessions.Add(master);
        mixer.Sessions.Add(microphone);
        mixer.Sessions.Add(application);

        mixer.ApplyEndpointStateFromSystem(AudioMixerMode.Output, 73f, isMuted: true);

        Assert.Equal(73f, master.Volume);
        Assert.True(master.IsMuted);
        Assert.Equal(40f, microphone.Volume);
        Assert.False(microphone.IsMuted);
        Assert.Equal(30f, application.Volume);
        Assert.Equal(0, writeBacks);
    }

    [Fact]
    public void ApplySessionStateFromSystem_UsesStableInstanceMappingWithoutWritingBack()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        var application = new AudioSessionItem("Discord", 30f, isMaster: false, isMic: false, processId: 42);
        int writeBacks = 0;
        application.VolumeChanged += _ => writeBacks++;
        application.MuteChanged += _ => writeBacks++;
        mixer.Sessions.Add(application);

        var sessionsById = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById");
        sessionsById["pid:42"] = application;
        var rowIdsByInstance = TestPrivateAccess.GetField<ConcurrentDictionary<string, string>>(mixer, "_sessionRowIdByInstanceId");
        rowIdsByInstance["session-instance-42"] = "pid:42";

        bool applied = mixer.ApplySessionStateFromSystem("session-instance-42", 64f, isMuted: true);
        bool unknownApplied = mixer.ApplySessionStateFromSystem("other-session", 90f, isMuted: false);

        Assert.True(applied);
        Assert.False(unknownApplied);
        Assert.Equal(64f, application.Volume);
        Assert.True(application.IsMuted);
        Assert.Equal(0, writeBacks);
    }

    [Theory]
    [InlineData(10, 75, false, 70)]
    [InlineData(80, 75, false, 0)]
    [InlineData(10, 75, true, 75)]
    [InlineData(80, 75, true, 75)]
    public void ResolveTrailingApplyDelay_ReturnsExpectedDelay(
        double elapsedMs,
        int throttleIntervalMs,
        bool trailingEdgeOnly,
        int expected)
    {
        int actual = MixerViewModel.ResolveTrailingApplyDelay(elapsedMs, throttleIntervalMs, trailingEdgeOnly);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, 0f, 10f, true)]
    [InlineData(true, 0.005f, 10f, true)]
    [InlineData(true, 30f, 40f, false)]
    [InlineData(true, 30f, 0f, false)]
    [InlineData(false, 0f, 10f, false)]
    public void ShouldAutoUnmuteOnVolumeDrag_ReturnsExpectedValue(bool isMuted, float previousVolume, float currentVolume, bool expected)
    {
        bool actual = MixerViewModel.ShouldAutoUnmuteOnVolumeDrag(isMuted, previousVolume, currentVolume);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData((int)AudioMixerMode.Output, (int)DataFlow.Render)]
    [InlineData((int)AudioMixerMode.Input, (int)DataFlow.Capture)]
    public void GetProcessSessionFlow_ReturnsExpectedDataFlow(int mixerMode, int expectedFlow)
    {
        DataFlow actual = MixerViewModel.GetProcessSessionFlow((AudioMixerMode)mixerMode);

        Assert.Equal((DataFlow)expectedFlow, actual);
    }

    [Theory]
    [InlineData("master:primary", true)]
    [InlineData("mic:primary", true)]
    [InlineData("system:sounds", true)]
    [InlineData("pid:42", false)]
    public void IsSharedSessionId_ReturnsExpectedValue(string sessionId, bool expected)
    {
        bool actual = MixerViewModel.IsSharedSessionId(sessionId);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SwapRefreshTokenSource_ReturnsPreviousAndSetsNext()
    {
        CancellationTokenSource? current = new();
        using var next = new CancellationTokenSource();

        var previous = MixerViewModel.SwapRefreshTokenSource(ref current, next);

        Assert.NotNull(previous);
        Assert.Same(next, current);

        previous?.Dispose();
    }

    [Fact]
    public void BeginRefreshCycle_CancelsAndReplacesPreviousRefreshToken()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();

        CancellationTokenSource first = mixer.BeginRefreshCycle();
        CancellationTokenSource second = mixer.BeginRefreshCycle();

        try
        {
            Assert.True(first.IsCancellationRequested);
            Assert.Same(second, TestPrivateAccess.GetField<CancellationTokenSource?>(mixer, "_refreshCts"));
        }
        finally
        {
            second.Dispose();
        }
    }

    [Theory]
    [InlineData(5, 5, 0, false)]
    [InlineData(5, 4, 0, true)]
    [InlineData(5, 6, 0, true)]
    [InlineData(5, 5, 1, true)]
    public void ShouldScanForRemovedSessions_MatchesExpectedFastPath(
        int existingCount,
        int incomingCount,
        int addedCount,
        bool expected)
    {
        bool result = MixerViewModel.ShouldScanForRemovedSessions(existingCount, incomingCount, addedCount);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, false, 100, 300, 10000, 10000)]
    [InlineData(true, true, 100, 300, 10000, 100)]
    [InlineData(false, false, 100, 300, 10000, 300)]
    public void ResolveSnapshotCacheWindowMs_ReturnsExpectedWindow(
        bool interactive,
        bool hasCompletedFirstRefresh,
        int interactiveCacheWindowMs,
        int backgroundCacheWindowMs,
        int prewarmReuseWindowMs,
        int expected)
    {
        int actual = MixerViewModel.ResolveSnapshotCacheWindowMs(
            interactive,
            hasCompletedFirstRefresh,
            interactiveCacheWindowMs,
            backgroundCacheWindowMs,
            prewarmReuseWindowMs);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void ShouldCollectRefreshElapsedTiming_MatchesObservableLogs(
        bool traceEnabled,
        bool debugEnabled,
        bool warningEnabled,
        bool expected)
    {
        bool actual = MixerViewModel.ShouldCollectRefreshElapsedTiming(traceEnabled, debugEnabled, warningEnabled);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldSkipRefreshForRepeatedSnapshotReference_ReturnsTrue_ForSameReference()
    {
        AudioSessionSnapshot[] snapshots =
        [
            new("Master Volume", 50f, "Playback", null, null, null),
        ];

        bool actual = MixerViewModel.ShouldSkipRefreshForRepeatedSnapshotReference(snapshots, snapshots);

        Assert.True(actual);
    }

    [Fact]
    public void ShouldSkipRefreshForRepeatedSnapshotReference_ReturnsFalse_ForDifferentReferences()
    {
        IReadOnlyList<AudioSessionSnapshot> first =
        [
            new AudioSessionSnapshot("Master Volume", 50f, "Playback", null, null, null),
        ];
        IReadOnlyList<AudioSessionSnapshot> second =
        [
            new AudioSessionSnapshot("Master Volume", 50f, "Playback", null, null, null),
        ];

        bool actual = MixerViewModel.ShouldSkipRefreshForRepeatedSnapshotReference(first, second);

        Assert.False(actual);
    }

    [Fact]
    public async Task ApplyRefreshResultsAsync_DropsStaleGenerationBeforeMutatingSessions()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        List<(string Id, AudioSessionItem Item)> toAdd =
        [
            ("master:primary", new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false)),
            ("mic:primary", new AudioSessionItem("Microphone Volume", 25f, isMaster: false, isMic: true)),
            ("pid:42", new AudioSessionItem("Discord", 75f, isMaster: false, isMic: false, processId: 42))
        ];

        TestPrivateAccess.SetField(mixer, "_refreshGeneration", 2);

        Task<bool> applyTask = mixer.ApplyRefreshResultsAsync(1, null, toAdd, null, CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(applyTask);

        bool applied = await applyTask;
        var sessionsById = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById");

        Assert.False(applied);
        Assert.Empty(mixer.Sessions);
        Assert.Empty(sessionsById);
    }

    [Fact]
    public async Task ApplyRefreshResultsAsync_AppliesCurrentGenerationExactlyOnce()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        List<(string Id, AudioSessionItem Item)> toAdd =
        [
            ("master:primary", new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false)),
            ("mic:primary", new AudioSessionItem("Microphone Volume", 25f, isMaster: false, isMic: true)),
            ("pid:42", new AudioSessionItem("Discord", 75f, isMaster: false, isMic: false, processId: 42))
        ];

        TestPrivateAccess.SetField(mixer, "_refreshGeneration", 3);

        Task<bool> applyTask = mixer.ApplyRefreshResultsAsync(3, null, toAdd, null, CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(applyTask);

        bool applied = await applyTask;
        var sessionsById = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById");

        Assert.True(applied);
        Assert.Equal(3, mixer.Sessions.Count);
        Assert.Equal(3, sessionsById.Count);
        Assert.Collection(
            mixer.Sessions,
            session => Assert.True(session.IsMaster),
            session => Assert.True(session.IsMic),
            session => Assert.Equal("Discord", session.DisplayName));
    }

    [Fact]
    public async Task ApplyRefreshResultsAsync_UsesSingleSharedSessionInstanceAcrossPeerMixers()
    {
        MixerViewModel sourceMixer = CreateMixerForApplyTests(AudioMixerMode.Output);
        MixerViewModel peerMixer = CreateMixerForApplyTests(AudioMixerMode.Input);
        MixerViewModel.ConnectSharedSessionPair(sourceMixer, peerMixer);
        TestPrivateAccess.SetField(sourceMixer, "_refreshGeneration", 1);
        TestPrivateAccess.SetField(peerMixer, "_refreshGeneration", 1);

        Task<bool> sourceApplyTask = sourceMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: null,
            toAdd:
            [
                ("master:primary", new AudioSessionItem("Master Volume", 25f, isMaster: true, isMic: false))
            ],
            toUpdate: null,
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(sourceApplyTask);

        Task<bool> peerApplyTask = peerMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: null,
            toAdd:
            [
                ("master:primary", new AudioSessionItem("Master Volume", 25f, isMaster: true, isMic: false))
            ],
            toUpdate: null,
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(peerApplyTask);

        bool sourceApplied = await sourceApplyTask;
        bool peerApplied = await peerApplyTask;

        Assert.True(sourceApplied);
        Assert.True(peerApplied);

        var sourceSessionsById = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(sourceMixer, "_sessionsById");
        var peerSessionsById = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(peerMixer, "_sessionsById");

        AudioSessionItem sourceMaster = sourceSessionsById["master:primary"];
        AudioSessionItem peerMaster = peerSessionsById["master:primary"];

        Assert.Same(sourceMaster, peerMaster);

        Task<bool> updateTask = sourceMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: null,
            toAdd: null,
            toUpdate: [("master:primary", 72f, false)],
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(updateTask);

        bool updated = await updateTask;

        Assert.True(updated);
        Assert.Equal(72f, peerMaster.Volume);
    }

    [Fact]
    public async Task SharedSessionObject_IsVisibleFromBothMixerCollections()
    {
        MixerViewModel sourceMixer = CreateMixerForApplyTests(AudioMixerMode.Output);
        MixerViewModel peerMixer = CreateMixerForApplyTests(AudioMixerMode.Input);
        MixerViewModel.ConnectSharedSessionPair(sourceMixer, peerMixer);

        TestPrivateAccess.SetField(sourceMixer, "_refreshGeneration", 1);
        TestPrivateAccess.SetField(peerMixer, "_refreshGeneration", 1);

        Task<bool> sourceApplyTask = sourceMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: null,
            toAdd:
            [
                ("master:primary", new AudioSessionItem("Master Volume", 40f, isMaster: true, isMic: false))
            ],
            toUpdate: null,
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(sourceApplyTask);

        Task<bool> peerApplyTask = peerMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: null,
            toAdd:
            [
                ("master:primary", new AudioSessionItem("Master Volume", 40f, isMaster: true, isMic: false))
            ],
            toUpdate: null,
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(peerApplyTask);

        Assert.True(await sourceApplyTask);
        Assert.True(await peerApplyTask);
        Assert.Same(sourceMixer.Sessions.Single(), peerMixer.Sessions.Single());
    }

    [Fact]
    public async Task SharedSessionVolumeSubscription_AttachesToAvailableMixer_WhenPreferredOwnerIsAbsent()
    {
        MixerViewModel outputMixer = CreateMixerForApplyTests(AudioMixerMode.Output);
        MixerViewModel inputMixer = CreateMixerForApplyTests(AudioMixerMode.Input);
        MixerViewModel.ConnectSharedSessionPair(outputMixer, inputMixer);
        TestPrivateAccess.SetField(inputMixer, "_refreshGeneration", 1);

        Task<bool> inputApplyTask = inputMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: null,
            toAdd:
            [
                ("master:primary", new AudioSessionItem("Master Volume", 35f, isMaster: true, isMic: false))
            ],
            toUpdate: null,
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(inputApplyTask);

        Assert.True(await inputApplyTask);
        Assert.Contains(
            "master:primary",
            TestPrivateAccess.GetField<ConcurrentDictionary<string, byte>>(inputMixer, "_subscribedSessionIds").Keys);
    }

    [Fact]
    public async Task SharedSessionVolumeSubscription_TransfersToPeer_WhenOwnerRemovesSharedSession()
    {
        MixerViewModel outputMixer = CreateMixerForApplyTests(AudioMixerMode.Output);
        MixerViewModel inputMixer = CreateMixerForApplyTests(AudioMixerMode.Input);
        MixerViewModel.ConnectSharedSessionPair(outputMixer, inputMixer);
        TestPrivateAccess.SetField(outputMixer, "_refreshGeneration", 1);
        TestPrivateAccess.SetField(inputMixer, "_refreshGeneration", 1);

        Task<bool> outputApplyTask = outputMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: null,
            toAdd:
            [
                ("master:primary", new AudioSessionItem("Master Volume", 45f, isMaster: true, isMic: false))
            ],
            toUpdate: null,
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(outputApplyTask);

        Task<bool> inputApplyTask = inputMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: null,
            toAdd:
            [
                ("master:primary", new AudioSessionItem("Master Volume", 45f, isMaster: true, isMic: false))
            ],
            toUpdate: null,
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(inputApplyTask);

        Assert.True(await outputApplyTask);
        Assert.True(await inputApplyTask);
        Assert.Contains(
            "master:primary",
            TestPrivateAccess.GetField<ConcurrentDictionary<string, byte>>(outputMixer, "_subscribedSessionIds").Keys);
        Assert.DoesNotContain(
            "master:primary",
            TestPrivateAccess.GetField<ConcurrentDictionary<string, byte>>(inputMixer, "_subscribedSessionIds").Keys);

        Task<bool> removeTask = outputMixer.ApplyRefreshResultsAsync(
            1,
            idsToRemove: ["master:primary"],
            toAdd: null,
            toUpdate: null,
            CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(removeTask);

        Assert.True(await removeTask);
        Assert.DoesNotContain(
            "master:primary",
            TestPrivateAccess.GetField<ConcurrentDictionary<string, byte>>(outputMixer, "_subscribedSessionIds").Keys);
        Assert.Contains(
            "master:primary",
            TestPrivateAccess.GetField<ConcurrentDictionary<string, byte>>(inputMixer, "_subscribedSessionIds").Keys);
    }

    [Fact]
    public async Task ApplyRefreshResultsAsync_BatchInsertsUnsortedItemsWithoutBreakingMixerOrder()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        mixer.Sessions.Add(new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false));
        mixer.Sessions.Add(new AudioSessionItem("System Sounds", 20f, isMaster: false, isMic: false, isSystemSounds: true));

        List<(string Id, AudioSessionItem Item)> toAdd =
        [
            ("pid:99", new AudioSessionItem("Zoom", 40f, isMaster: false, isMic: false, processId: 99)),
            ("mic:primary", new AudioSessionItem("Microphone Volume", 30f, isMaster: false, isMic: true)),
            ("pid:42", new AudioSessionItem("Discord", 70f, isMaster: false, isMic: false, processId: 42))
        ];

        TestPrivateAccess.SetField(mixer, "_refreshGeneration", 2);

        Task<bool> applyTask = mixer.ApplyRefreshResultsAsync(2, null, toAdd, null, CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(applyTask);

        bool applied = await applyTask;

        Assert.True(applied);
        Assert.Collection(
            mixer.Sessions,
            session => Assert.True(session.IsMaster),
            session => Assert.True(session.IsMic),
            session => Assert.True(session.IsSystemSounds),
            session => Assert.Equal("Discord", session.DisplayName),
            session => Assert.Equal("Zoom", session.DisplayName));
    }

    [Fact]
    public async Task ApplyRefreshResultsAsync_WhenDispatcherShutdown_ReturnsFalseWithoutMutatingSessions()
    {
        Dispatcher dispatcher = AppViewModelDispatcherSafetyTests.CreateShutdownDispatcher();
        MixerViewModel mixer = CreateMixerForApplyTests(dispatcher: dispatcher);
        List<(string Id, AudioSessionItem Item)> toAdd =
        [
            ("pid:42", new AudioSessionItem("Discord", 75f, isMaster: false, isMic: false, processId: 42))
        ];

        TestPrivateAccess.SetField(mixer, "_refreshGeneration", 1);

        bool applied = await mixer.ApplyRefreshResultsAsync(1, null, toAdd, null, CancellationToken.None);
        var sessionsById = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById");

        Assert.False(applied);
        Assert.Empty(mixer.Sessions);
        Assert.Empty(sessionsById);
    }

    [Fact]
    public async Task ApplyRefreshResultsAsync_AfterCleanup_DropsRefreshResultsWithoutMutatingSessions()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        List<(string Id, AudioSessionItem Item)> toAdd =
        [
            ("pid:42", new AudioSessionItem("Discord", 75f, isMaster: false, isMic: false, processId: 42))
        ];

        TestPrivateAccess.SetField(mixer, "_refreshGeneration", 1);

        mixer.Cleanup();

        Task<bool> applyTask = mixer.ApplyRefreshResultsAsync(1, null, toAdd, null, CancellationToken.None);
        TestPrivateAccess.RunTaskOnDispatcher(applyTask);

        bool applied = await applyTask;
        var sessionsById = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById");

        Assert.False(applied);
        Assert.Empty(mixer.Sessions);
        Assert.Empty(sessionsById);
    }

    [Fact]
    public void CompleteRefreshSettlementCycle_CompletesPriorCycleWithoutCompletingNewCycle()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();

        TaskCompletionSource<object?> firstCycle = InvokeNonPublic<TaskCompletionSource<object?>>(mixer, "EnterRefreshSettlementCycle");

        TestPrivateAccess.SetField(mixer, "_activeRefreshCount", 0);

        TaskCompletionSource<object?> secondCycle = InvokeNonPublic<TaskCompletionSource<object?>>(mixer, "EnterRefreshSettlementCycle");

        InvokeNonPublicStatic(typeof(MixerViewModel), "CompleteRefreshSettlementCycle", firstCycle);

        Assert.True(firstCycle.Task.IsCompleted);
        Assert.False(secondCycle.Task.IsCompleted);
        Assert.Same(secondCycle, TestPrivateAccess.GetField<TaskCompletionSource<object?>>(mixer, "_refreshSettlementTcs"));
    }

    [Fact]
    public void MarkActivationRefreshStale_WhenSessionsPresent_SetsFlag()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        mixer.Sessions.Add(new AudioSessionItem("Spotify", 50f, isMaster: false, isMic: false, processId: 321));

        mixer.MarkActivationRefreshStale("test");

        Assert.True(mixer.RequiresActivationRefresh);
    }

    [Fact]
    public void TrimIdleState_ClearsActivationRefreshFlag()
    {
        MixerViewModel mixer = CreateMixerForApplyTests();
        mixer.Sessions.Add(new AudioSessionItem("Spotify", 50f, isMaster: false, isMic: false, processId: 321));
        mixer.MarkActivationRefreshStale("test");

        mixer.TrimIdleState();

        Assert.False(mixer.RequiresActivationRefresh);
    }

    private static MixerViewModel CreateMixerForApplyTests(AudioMixerMode mixerMode = AudioMixerMode.Output, Dispatcher? dispatcher = null)
    {
        var mixer = (MixerViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MixerViewModel));

        TestPrivateAccess.SetField(mixer, "_logger", Logger.Instance);
        TestPrivateAccess.SetField(mixer, "_dispatcher", dispatcher ?? Dispatcher.CurrentDispatcher);
        TestPrivateAccess.SetField(mixer, "_mixerMode", mixerMode);
        TestPrivateAccess.SetField(mixer, "_refreshSettlementLock", new Lock());
        TestPrivateAccess.SetField(mixer, "_refreshSettlementTcs", CreateCompletedSettlementSource());
        TestPrivateAccess.SetField(mixer, "_sessionsById", new ConcurrentDictionary<string, AudioSessionItem>(StringComparer.OrdinalIgnoreCase));
        TestPrivateAccess.SetField(mixer, "_sessionRowIdByInstanceId", new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        TestPrivateAccess.SetField(mixer, "_pidToProcessName", new ConcurrentDictionary<uint, string>());
        TestPrivateAccess.SetField(mixer, "_userSetVolumes", new ConcurrentDictionary<string, float>(StringComparer.OrdinalIgnoreCase));
        TestPrivateAccess.SetField(mixer, "_lastVolumeSetByUs", new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase));
        TestPrivateAccess.SetField(mixer, "_subscribedSessionIds", new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        TestPrivateAccess.SetField(mixer, "_throttleStates", CreateThrottleStatesDictionary());
        TestPrivateAccess.SetField(mixer, "<Sessions>k__BackingField", new ObservableCollection<AudioSessionItem>());
        TestPrivateAccess.SetField<object?>(mixer, "_sharedSessionBridge", null);

        return mixer;
    }

    private static object CreateThrottleStatesDictionary()
    {
        FieldInfo field = typeof(MixerViewModel).GetField("_throttleStates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Activator.CreateInstance(field.FieldType, StringComparer.OrdinalIgnoreCase)!;
    }

    private static TaskCompletionSource<object?> CreateCompletedSettlementSource()
    {
        var completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        completionSource.TrySetResult(null);
        return completionSource;
    }

    private static void InvokeNonPublic(object target, string methodName, params object?[]? args)
    {
        MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method!.Invoke(target, args);
    }

    private static void InvokeNonPublicStatic(Type targetType, string methodName, params object?[]? args)
    {
        MethodInfo? method = targetType.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method!.Invoke(null, args);
    }

    private static T InvokeNonPublic<T>(object target, string methodName, params object?[]? args)
    {
        MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (T)method!.Invoke(target, args)!;
    }
}


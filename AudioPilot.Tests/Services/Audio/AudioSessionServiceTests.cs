using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

[Collection("DeviceCacheHelperIsolation")]
public sealed class AudioSessionServiceTests
{
    private sealed class StubEnumerator : IAudioDeviceEnumerator
    {
        public MMDeviceCollection GetActivePlaybackDevices() => throw new NotSupportedException();
        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds) => throw new NotSupportedException();
        public MMDevice GetDefaultPlaybackDevice() => throw new NotSupportedException();
        public MMDevice? GetDefaultRecordingDevice() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultPlaybackDevices() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultRecordingDevices() => throw new NotSupportedException();
    }

    [Fact]
    public void IsCacheEntryExpired_ReturnsFalse_ForFreshTimestamp()
    {
        using var service = new AudioSessionService(
            new StubEnumerator(),
            deviceCacheAccessor: static () => null);

        long freshTicks = GetMonotonicTimestampTicks();

        bool expired = service.IsCacheEntryExpired(freshTicks);

        Assert.False(expired);
    }

    [Fact]
    public void IsCacheEntryExpired_ReturnsTrue_ForOldTimestamp()
    {
        using var service = new AudioSessionService(
            new StubEnumerator(),
            deviceCacheAccessor: static () => null);

        long oldTicks = GetMonotonicTimestampTicks() - TimeSpan.FromHours(1).Ticks;

        bool expired = service.IsCacheEntryExpired(oldTicks);

        Assert.True(expired);
    }

    [Fact]
    public async Task StartCleanupTaskAsync_IsIdempotent_AndStartsLoop()
    {
        using var service = new AudioSessionService(new StubEnumerator());

        await service.StartCleanupTaskAsync();
        await service.StartCleanupTaskAsync();

        Assert.True(service.IsCleanupLoopStarted);
    }

    [Fact]
    public async Task Dispose_AfterCleanupLoopStart_DoesNotLeaveCleanupTaskRunning()
    {
        var service = new AudioSessionService(new StubEnumerator());

        await service.StartCleanupTaskAsync();

        Task? cleanupTask = service.CleanupTaskForTests;
        Assert.NotNull(cleanupTask);

        service.Dispose();

        Assert.False(service.IsCleanupLoopStarted);

        try
        {
            await cleanupTask!.WaitAsync(TimeSpan.FromMilliseconds(AppConstants.Timing.CacheCleanupIntervalMs + 1000), TestContext.Current.CancellationToken);
        }
        catch (TaskCanceledException)
        {
        }
    }

    [Fact]
    public async Task StartCleanupTaskAsync_AfterDispose_DoesNotRestartCleanupLoop()
    {
        var service = new AudioSessionService(new StubEnumerator());

        service.Dispose();
        await service.StartCleanupTaskAsync();

        Assert.False(service.IsCleanupLoopStarted);
        Assert.Null(service.CleanupTaskForTests);
    }

    [Fact]
    public void OnDeviceStateChange_DoesNotThrow_WhenDeviceCacheSingletonUnavailable()
    {
        DeviceCacheHelper.DisposeSingleton();
        using var service = new AudioDeviceService(new FakeInputListenPropertyWriter());

        Exception? ex = Record.Exception(service.RaiseDeviceStateChangedForTests);

        Assert.Null(ex);
    }

    [Fact]
    public void ShouldUseRecentSnapshotCache_ReturnsFalse_WhenControlsRequested()
    {
        DateTime nowUtc = DateTime.UtcNow;

        bool shouldUse = AudioSessionService.ShouldUseRecentSnapshotCache(
            nowUtc,
            nowUtc,
            cacheWindowMs: 200,
            includeSessionControls: true);

        Assert.False(shouldUse);
    }

    [Fact]
    public void ShouldUseRecentSnapshotCache_ReturnsTrue_WhenWithinWindow_AndNoControls()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime last = nowUtc.AddMilliseconds(-120);

        bool shouldUse = AudioSessionService.ShouldUseRecentSnapshotCache(
            nowUtc,
            last,
            cacheWindowMs: 200,
            includeSessionControls: false);

        Assert.True(shouldUse);
    }

    [Fact]
    public void ShouldUseRecentSnapshotCache_ReturnsFalse_WhenWindowExpired()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime last = nowUtc.AddMilliseconds(-400);

        bool shouldUse = AudioSessionService.ShouldUseRecentSnapshotCache(
            nowUtc,
            last,
            cacheWindowMs: 200,
            includeSessionControls: false);

        Assert.False(shouldUse);
    }

    [Theory]
    [InlineData(false, "abc", "abc", 1, 0, 4, true)]
    [InlineData(true, "abc", "abc", 1, 0, 4, false)]
    [InlineData(false, "abc", "xyz", 1, 0, 4, false)]
    [InlineData(false, "abc", "abc", 0, 0, 4, false)]
    [InlineData(false, "abc", "abc", 1, 4, 4, false)]
    [InlineData(false, "", "", 1, 0, 4, false)]
    public void ShouldUseSelectivePlaybackDeviceScan_ReturnsExpectedValue(
        bool includeSessionControls,
        string currentFingerprint,
        string previousFingerprint,
        int candidateDeviceCount,
        int selectiveScanStreak,
        int selectiveScanLimit,
        bool expected)
    {
        bool result = AudioSessionService.ShouldUseSelectivePlaybackDeviceScan(
            includeSessionControls,
            currentFingerprint,
            previousFingerprint,
            candidateDeviceCount,
            selectiveScanStreak,
            selectiveScanLimit);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetAllAudioSessionsSnapshot_SyncPath_StartsCleanupLoop()
    {
        using var service = new AudioSessionService(
            new StubEnumerator(),
            deviceCacheAccessor: static () => null);
        Assert.False(service.IsCleanupLoopStarted);

        try
        {
            service.GetAllAudioSessionsSnapshot(_ => { }, includeSessionControls: false);
        }
        catch (NotSupportedException)
        {
        }

        Assert.True(service.IsCleanupLoopStarted);
    }

    [Fact]
    public void GetAllAudioSessionSnapshots_SyncPath_StartsCleanupLoop()
    {
        using var service = new AudioSessionService(
            new StubEnumerator(),
            deviceCacheAccessor: static () => null);
        Assert.False(service.IsCleanupLoopStarted);

        try
        {
            service.GetAllAudioSessionSnapshotsForTests(includeSessionControls: false);
        }
        catch (NotSupportedException)
        {
        }

        Assert.True(service.IsCleanupLoopStarted);
    }

    [Fact]
    public void DeferredWindowPidMap_BuildsLazily_AndCachesResult()
    {
        int factoryCalls = 0;
        IReadOnlyDictionary<IntPtr, uint> expected = new Dictionary<IntPtr, uint>
        {
            [new IntPtr(42)] = 100,
        };

        var deferred = new AudioSessionService.DeferredWindowPidMap(() =>
        {
            factoryCalls++;
            return expected;
        });

        Assert.Equal(0, factoryCalls);

        IReadOnlyDictionary<IntPtr, uint> first = deferred.GetOrCreate();
        IReadOnlyDictionary<IntPtr, uint> second = deferred.GetOrCreate();

        Assert.Equal(1, factoryCalls);
        Assert.Same(expected, first);
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData("spotify.exe", null, "Spotify", "spotify.exe", null)]
    [InlineData("discord_x64", "Discord Voice", "Discord Voice", "discord_x64", "Main Window")]
    public void TryProjectSessionProcessMetadataForTests_ReturnsProjectedMetadata(
        string processName,
        string? displayName,
        string expectedDisplayName,
        string expectedProcessName,
        string? mainWindowTitle)
    {
        bool result = AudioSessionService.TryProjectSessionProcessMetadataForTests(
            processName,
            displayName,
            mainWindowTitle,
            out var metadata);

        Assert.True(result);
        Assert.Equal(expectedProcessName, metadata.ProcessName);
        Assert.Equal(expectedDisplayName, metadata.DisplayName);
        Assert.Equal(mainWindowTitle, metadata.MainWindowTitle);
    }

    [Theory]
    [InlineData("audiodg", "Music")]
    [InlineData("audiodg", null)]
    [InlineData("spotify", "@%SystemRoot%\\System32\\AudioSrv")]
    [InlineData("audiopilot", "AudioPilot")]
    [InlineData("audiopilot.clihost", "AudioPilot CLI")]
    public void TryProjectSessionProcessMetadataForTests_ReturnsFalse_ForIgnoredMetadata(
        string processName,
        string? displayName)
    {
        bool result = AudioSessionService.TryProjectSessionProcessMetadataForTests(
            processName,
            displayName,
            mainWindowTitle: "Ignored Window",
            out var metadata);

        Assert.False(result);
        Assert.Equal(default, metadata);
    }

    [Theory]
    [InlineData(0u, null, true)]
    [InlineData(null, "System Sounds", true)]
    [InlineData(null, "@%SystemRoot%\\System32\\AudioSrv", true)]
    [InlineData(42u, "Spotify", false)]
    public void IsSystemSoundsSessionCandidateForTests_ReturnsExpectedValue(uint? processId, string? displayName, bool expected)
    {
        bool result = AudioSessionService.IsSystemSoundsSessionCandidateForTests(processId, displayName);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("audiopilot.exe", "AudioPilot", true, "AudioPilot")]
    [InlineData("runtimebroker.exe", "Music", true, "Music")]
    [InlineData("msedgewebview2.exe", "WebView Host", true, "WebView Host")]
    public void TryProjectSessionProcessMetadataForTests_UsesExactBasenameSemantics(
        string processName,
        string? displayName,
        bool expectedResult,
        string expectedDisplayName)
    {
        bool result = AudioSessionService.TryProjectSessionProcessMetadataForTests(
            processName,
            displayName,
            mainWindowTitle: null,
            out var metadata);

        Assert.Equal(expectedResult, result);
        Assert.Equal(processName, metadata.ProcessName);
        Assert.Equal(expectedDisplayName, metadata.DisplayName);
    }

    [Fact]
    public void ShouldSkipSelfSessionForTests_ReturnsTrue_ForCurrentProcess()
    {
        bool result = AudioSessionService.ShouldSkipSelfSessionForTests((uint)Environment.ProcessId, Environment.ProcessId);

        Assert.True(result);
    }

    [Fact]
    public void ShouldSkipSelfSessionForTests_ReturnsFalse_ForOtherProcess()
    {
        bool result = AudioSessionService.ShouldSkipSelfSessionForTests((uint)(Environment.ProcessId + 1), Environment.ProcessId);

        Assert.False(result);
    }

    [Fact]
    public void TryGetRecentNoControlsSnapshotData_ReusesCachedSnapshotReference()
    {
        using var service = new AudioSessionService(new StubEnumerator());
        AudioSessionSnapshot[] cachedSnapshots =
        [
            new("Master Volume", 50f, "Playback", null, null, null),
            new("Discord", 25f, "Playback", "discord", null, 42),
        ];

        service.SeedRecentSnapshotForTests(AudioMixerMode.Output, cachedSnapshots, DateTime.UtcNow);

        IReadOnlyList<AudioSessionSnapshot> reused = service.GetAllAudioSessionSnapshots(AudioMixerMode.Output, 200);

        Assert.Same(cachedSnapshots, reused);
    }

    [Fact]
    public void TryGetRecentInputNoControlsSnapshotData_ReusesCachedSnapshotReference()
    {
        using var service = new AudioSessionService(new StubEnumerator());
        AudioSessionSnapshot[] cachedSnapshots =
        [
            new("Master Volume", 50f, "Playback", null, null, null),
            new("Microphone Volume", 25f, "Recording", null, null, null),
        ];

        service.SeedRecentSnapshotForTests(AudioMixerMode.Input, cachedSnapshots, DateTime.UtcNow);

        IReadOnlyList<AudioSessionSnapshot> reused = service.GetAllAudioSessionSnapshots(AudioMixerMode.Input, 200);

        Assert.Same(cachedSnapshots, reused);
    }

    [Fact]
    public void RecordEndpointVolumeNotification_UpdatesCachedEndpointEntries()
    {
        using var service = new AudioSessionService(new StubEnumerator());
        AudioSessionSnapshot[] cachedSnapshots =
        [
            new("Master Volume", 50f, "Playback", null, null, null),
            new("Microphone Volume", 25f, "Recording", null, null, null),
            new("Discord", 75f, "Playback", "discord", null, 42),
        ];

        service.SeedRecentSnapshotForTests(AudioMixerMode.Output, cachedSnapshots, DateTime.UtcNow);
        service.SeedEndpointSnapshotForTests(
            AudioMixerMode.Output,
            CreateEndpointSnapshotEntry("playback-1", "Playback", 50f, isMuted: false));
        service.SeedEndpointSnapshotForTests(
            AudioMixerMode.Input,
            CreateEndpointSnapshotEntry("recording-1", "Recording", 25f, isMuted: false));

        service.RecordEndpointVolumeNotification(AudioMixerMode.Output, "playback-1", 61f, isMuted: true);
        service.RecordEndpointVolumeNotification(AudioMixerMode.Input, "recording-1", 37f, isMuted: true);

        AudioSessionSnapshot[] updatedSnapshots = service.GetRecentSnapshotDataForTests(AudioMixerMode.Output).Snapshot!;
        Assert.Equal(61f, updatedSnapshots[0].Volume);
        Assert.True(updatedSnapshots[0].IsMuted);
        Assert.Equal(37f, updatedSnapshots[1].Volume);
        Assert.True(updatedSnapshots[1].IsMuted);

        var playbackEndpointSnapshot = service.GetEndpointSnapshotForTests(AudioMixerMode.Output);
        var recordingEndpointSnapshot = service.GetEndpointSnapshotForTests(AudioMixerMode.Input);

        Assert.NotNull(playbackEndpointSnapshot);
        Assert.NotNull(recordingEndpointSnapshot);
        Assert.Equal(61f, playbackEndpointSnapshot.Value.VolumePercent);
        Assert.True(playbackEndpointSnapshot.Value.IsMuted);
        Assert.Equal(37f, recordingEndpointSnapshot.Value.VolumePercent);
        Assert.True(recordingEndpointSnapshot.Value.IsMuted);
    }

    [Fact]
    public void InvalidateRecentMixerSnapshotState_ClearsRecentTopologyStateOnly()
    {
        using var service = new AudioSessionService(new StubEnumerator());
        AudioSessionSnapshot[] cachedSnapshots =
        [
            new("Master Volume", 50f, "Playback", null, null, null),
            new("Discord", 75f, "Playback", "discord", null, 42),
        ];

        service.SeedRecentSnapshotForTests(AudioMixerMode.Output, cachedSnapshots, DateTime.UtcNow);
        service.SeedRecentSnapshotForTests(AudioMixerMode.Input, cachedSnapshots, DateTime.UtcNow);
        service.SeedEndpointSnapshotForTests(AudioMixerMode.Output, CreateEndpointSnapshotEntry("playback-1", "Playback", 50f, isMuted: false));
        service.SeedEndpointSnapshotForTests(AudioMixerMode.Input, CreateEndpointSnapshotEntry("recording-1", "Recording", 25f, isMuted: false));
        service.SetOutputScanStateForTests(
            "playback-topology",
            new HashSet<string>(["playback-1"], StringComparer.OrdinalIgnoreCase),
            selectivePlaybackScanStreak: 3);
        service.AddProcessCacheEntryForTests(42, "spotify", "Spotify", null, GetMonotonicTimestampTicks());

        service.InvalidateRecentMixerSnapshotState();

        Assert.Null(service.GetRecentSnapshotDataForTests(AudioMixerMode.Output).Snapshot);
        Assert.Null(service.GetRecentSnapshotDataForTests(AudioMixerMode.Input).Snapshot);
        Assert.Null(service.GetEndpointSnapshotForTests(AudioMixerMode.Output));
        Assert.Null(service.GetEndpointSnapshotForTests(AudioMixerMode.Input));
        var outputScanState = service.GetOutputScanStateForTests();
        Assert.Equal(string.Empty, outputScanState.PlaybackFingerprint);
        Assert.Null(outputScanState.SessionBearingPlaybackDeviceIds);
        Assert.Equal(0, outputScanState.SelectivePlaybackScanStreak);
        Assert.Equal(1, service.ProcessCacheCountForTests);
    }

    [Fact]
    public void ClearCaches_ResetsRecentTopologyStateAndProcessCache()
    {
        using var service = new AudioSessionService(new StubEnumerator());

        service.SetOutputScanStateForTests(
            "playback-topology",
            new HashSet<string>(["playback-1"], StringComparer.OrdinalIgnoreCase),
            selectivePlaybackScanStreak: 4);
        service.SeedRecentSnapshotForTests(
            AudioMixerMode.Output,
            [new AudioSessionSnapshot("Master Volume", 50f, "Playback", null, null, null)],
            DateTime.UtcNow);
        service.AddProcessCacheEntryForTests(42, "spotify", "Spotify", null, GetMonotonicTimestampTicks());

        service.ClearCaches();

        Assert.Equal(0, service.ProcessCacheCountForTests);
        var outputScanState = service.GetOutputScanStateForTests();
        Assert.Equal(string.Empty, outputScanState.PlaybackFingerprint);
        Assert.Null(outputScanState.SessionBearingPlaybackDeviceIds);
        Assert.Equal(0, outputScanState.SelectivePlaybackScanStreak);
        Assert.Null(service.GetRecentSnapshotDataForTests(AudioMixerMode.Output).Snapshot);
    }

    private static AudioSessionRecentSnapshotCache.EndpointSnapshotEntry CreateEndpointSnapshotEntry(
        string deviceId,
        string deviceName,
        float volumePercent,
        bool isMuted)
    {
        return new AudioSessionRecentSnapshotCache.EndpointSnapshotEntry(
            deviceId,
            deviceName,
            volumePercent,
            isMuted,
            DateTime.UtcNow.Ticks);
    }

    private static long GetMonotonicTimestampTicks() =>
        Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;
}


using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPilot.Logging;
using AudioPilot.Tests.TestDoubles;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Helpers;

[Collection("DeviceCacheHelperIsolation")]
public sealed class DeviceCacheHelperTests
{
    public DeviceCacheHelperTests()
    {
        DeviceCacheHelper.DisposeSingleton();
    }

    [Fact]
    public void Instance_Throws_WhenNotInitialized()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _ = DeviceCacheHelper.Instance);

        Assert.Contains("not initialized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initialize_SetsSingleton_AndDisposeSingleton_ClearsIt()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);

        Assert.True(DeviceCacheHelper.IsInitialized);
        Assert.NotNull(DeviceCacheHelper.Instance);

        DeviceCacheHelper.DisposeSingleton();

        Assert.False(DeviceCacheHelper.IsInitialized);
    }

    [Fact]
    public void Initialize_Throws_WhenAlreadyInitialized()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DeviceCacheHelper.Initialize(audio));

        Assert.Contains("already initialized", ex.Message, StringComparison.OrdinalIgnoreCase);

        DeviceCacheHelper.DisposeSingleton();
    }

    [Fact]
    public void InitializeForTests_SetsSingleton_AndDisposeSingleton_ClearsIt()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime());

        Assert.True(DeviceCacheHelper.IsInitialized);
        Assert.NotNull(DeviceCacheHelper.Instance);

        DeviceCacheHelper.DisposeSingleton();

        Assert.False(DeviceCacheHelper.IsInitialized);
    }

    [Fact]
    public void ShouldThrottleInvalidation_UsesWindowCorrectly()
    {
        Assert.True(DeviceCacheHelper.ShouldThrottleInvalidation(105, 10, minIntervalMs: 100));
        Assert.False(DeviceCacheHelper.ShouldThrottleInvalidation(250, 10, minIntervalMs: 100));
    }

    [Fact]
    public void BuildTopologyFingerprintForTests_ChangesWhenTopologyChanges()
    {
        string baseline = DeviceCacheHelper.BuildTopologyFingerprintForTests(
            [("out-1", "Speakers", DeviceState.Active), ("out-2", "Headset", DeviceState.Active)],
            [("in-1", "Mic", DeviceState.Active), null]);

        string same = DeviceCacheHelper.BuildTopologyFingerprintForTests(
            [("out-1", "Speakers", DeviceState.Active), ("out-2", "Headset", DeviceState.Active)],
            [("in-1", "Mic", DeviceState.Active), null]);

        string changed = DeviceCacheHelper.BuildTopologyFingerprintForTests(
            [("out-1", "Speakers", DeviceState.Unplugged), ("out-2", "Headset", DeviceState.Active)],
            [("in-1", "Mic", DeviceState.Active), null]);

        Assert.Equal(baseline, same);
        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void UsePlaybackFallbackForRole_OnlyForMultimedia()
    {
        Assert.True(DeviceCacheHelper.UsePlaybackFallbackForRole(Role.Multimedia));
        Assert.False(DeviceCacheHelper.UsePlaybackFallbackForRole(Role.Console));
        Assert.False(DeviceCacheHelper.UsePlaybackFallbackForRole(Role.Communications));
    }

    [Fact]
    public void UseRecordingFallbackForRole_OnlyForConsole()
    {
        Assert.True(DeviceCacheHelper.UseRecordingFallbackForRole(Role.Console));
        Assert.False(DeviceCacheHelper.UseRecordingFallbackForRole(Role.Multimedia));
        Assert.False(DeviceCacheHelper.UseRecordingFallbackForRole(Role.Communications));
    }

    [Fact]
    public void InvalidateCache_SetsInvalidatedFlag_WhenInitialized()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        Assert.False(cache.CacheInvalidatedForTests);

        cache.InvalidateCache();

        Assert.True(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public void InvalidateCache_ThrottlesRepeatedCallsWithinWindow()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();
        long firstTicks = cache.LastInvalidateTicksForTests;

        cache.InvalidateCache();

        Assert.Equal(firstTicks, cache.LastInvalidateTicksForTests);
    }

    [Fact]
    public async Task RefreshAsync_CompletesAndLeavesNoRefreshInProgress_AfterInvalidation()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        await cache.RefreshAsync();

        Assert.False(cache.RefreshInProgressForTests);
    }

    [Fact]
    public async Task RefreshAsync_ClearsInvalidationAndAdvancesExpiry()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();
        long firstExpiry = cache.DeviceCacheExpiryTicksForTests;

        cache.InvalidateCache();
        Assert.True(cache.CacheInvalidatedForTests);

        await cache.RefreshAsync();

        Assert.False(cache.CacheInvalidatedForTests);
        Assert.True(cache.DeviceCacheExpiryTicksForTests >= firstExpiry);
        Assert.True(cache.DeviceCacheExpiryTicksForTests > Environment.TickCount64);
    }

    [Fact]
    public async Task RefreshAsync_PopulatesTopologyFingerprint_AndDisposeClearsIt()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        Assert.False(string.IsNullOrWhiteSpace(cache.LastTopologyFingerprintForTests));

        DeviceCacheHelper.DisposeSingleton();

        Assert.Equal(string.Empty, cache.LastTopologyFingerprintForTests);
    }

    [Fact]
    public async Task RefreshAsync_RepeatedWithoutInvalidation_ReusesFingerprint_AndKeepsExpiryAdvanced()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();
        string firstFingerprint = cache.LastTopologyFingerprintForTests;
        long firstExpiry = cache.DeviceCacheExpiryTicksForTests;

        await cache.RefreshAsync();

        Assert.Equal(firstFingerprint, cache.LastTopologyFingerprintForTests);
        Assert.True(cache.DeviceCacheExpiryTicksForTests >= firstExpiry);
    }

    [Fact]
    public async Task RefreshAsync_AfterInvalidationWithUnchangedTopology_ReusesFingerprint_AndClearsInvalidation()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();
        string firstFingerprint = cache.LastTopologyFingerprintForTests;

        cache.InvalidateCache();
        Assert.True(cache.CacheInvalidatedForTests);

        await cache.RefreshAsync();

        Assert.False(cache.CacheInvalidatedForTests);
        Assert.Equal(firstFingerprint, cache.LastTopologyFingerprintForTests);
    }

    [Fact]
    public async Task InvalidateCache_AfterRefresh_KeepsExistingExpiryUntilNextRefreshRuns()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();
        long expiryBeforeInvalidation = cache.DeviceCacheExpiryTicksForTests;

        cache.InvalidateCache();

        Assert.True(cache.CacheInvalidatedForTests);
        Assert.Equal(expiryBeforeInvalidation, cache.DeviceCacheExpiryTicksForTests);
    }

    [Fact]
    public async Task TrimForHiddenMode_ClearsPendingAccessDiagnostics_WithoutDroppingSnapshots()
    {
        using var loggerScope = new TestLoggerScope(nameof(DeviceCacheHelperTests), "device-cache-hidden-trim.log", LogLevel.Debug);
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        MMDevice playbackDevice = CreateTestDevice();
        DeviceCacheHelper.InitializeForTests(
            audio,
            CreateRuntime(
                capture: new DeviceCacheRefreshCapture(
                    [null, new DeviceCacheSnapshotData("playback-a", "Playback A", DeviceState.Active), null],
                    [null, null, null]),
                materializeDeviceById: id => id == "playback-a" ? playbackDevice : null),
            loggerScope.Logger);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();
        _ = cache.GetPlaybackDevice(Role.Multimedia);

        int playbackSnapshotCountBeforeTrim = GetPlaybackSnapshotCount(cache);
        Assert.True(GetPendingAccessDiagnosticsCount(cache) > 0);
        Assert.True(playbackSnapshotCountBeforeTrim > 0);

        cache.TrimDiagnosticsForHiddenMode();

        Assert.Equal(0, GetPendingAccessDiagnosticsCount(cache));
        Assert.Equal(playbackSnapshotCountBeforeTrim, GetPlaybackSnapshotCount(cache));
    }

    [Fact]
    public async Task RefreshAsync_WhenInvokedConcurrently_CompletesBothCalls_AndLeavesNoRefreshInProgress()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        Task firstRefresh = cache.RefreshAsync();
        Task secondRefresh = cache.RefreshAsync();

        await Task.WhenAll(firstRefresh, secondRefresh);

        Assert.False(cache.RefreshInProgressForTests);
        Assert.False(cache.CacheInvalidatedForTests);
        Assert.False(string.IsNullOrWhiteSpace(cache.LastTopologyFingerprintForTests));
    }

    [Fact]
    public async Task RefreshAsync_UsesInjectedRefreshRunner_AndPublishesActiveCompletionTask()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int runnerCalls = 0;

        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            runRefreshWorkAsync: async refreshWork =>
            {
                Interlocked.Increment(ref runnerCalls);
                await gate.Task;
                await refreshWork();
            }));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        Task refreshTask = cache.RefreshAsync();
        Task completionTask = cache.WaitForRefreshCompletionForTestsAsync();

        Assert.True(cache.RefreshInProgressForTests);
        Assert.False(completionTask.IsCompleted);
        Assert.Equal(1, runnerCalls);

        gate.SetResult(true);

        await Task.WhenAll(refreshTask, completionTask);

        Assert.False(cache.RefreshInProgressForTests);
        Assert.True(completionTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task GetPlaybackDeviceName_WhenRefreshAlreadyTriggered_ReusesBackgroundRefreshCompletion()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        _ = cache.GetPlaybackDeviceName(Role.Multimedia);
        Task firstRefreshCompletion = cache.WaitForRefreshCompletionForTestsAsync();

        _ = cache.GetPlaybackDeviceName(Role.Multimedia);
        Task secondRefreshCompletion = cache.WaitForRefreshCompletionForTestsAsync();

        AssertSharedCompletionWhileRefreshActiveOrAllowCompletedRace(cache, firstRefreshCompletion, secondRefreshCompletion);

        await secondRefreshCompletion;

        Task postCompletion = cache.WaitForRefreshCompletionForTestsAsync();

        Assert.False(cache.RefreshInProgressForTests);
        Assert.False(cache.CacheInvalidatedForTests);
        Assert.False(string.IsNullOrWhiteSpace(cache.LastTopologyFingerprintForTests));
        Assert.NotSame(firstRefreshCompletion, postCompletion);
        Assert.True(postCompletion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PlaybackAndRecordingAccessors_WhenBackgroundRefreshInProgress_ShareSameCompletionTask()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        _ = cache.GetPlaybackDeviceName(Role.Multimedia);
        Task sharedCompletion = cache.WaitForRefreshCompletionForTestsAsync();

        _ = cache.GetRecordingDeviceName(Role.Console);

        AssertSharedCompletionWhileRefreshActiveOrAllowCompletedRace(
            cache,
            sharedCompletion,
            cache.WaitForRefreshCompletionForTestsAsync());

        await sharedCompletion;

        Assert.False(cache.RefreshInProgressForTests);
    }

    [Fact]
    public async Task GetAllAccessors_WhenBackgroundRefreshInProgress_ShareSameCompletionTask()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        _ = cache.GetAllPlaybackDevices();
        Task sharedCompletion = cache.WaitForRefreshCompletionForTestsAsync();

        _ = cache.GetAllRecordingDevices();

        AssertSharedCompletionWhileRefreshActiveOrAllowCompletedRace(
            cache,
            sharedCompletion,
            cache.WaitForRefreshCompletionForTestsAsync());

        await sharedCompletion;

        Assert.False(cache.RefreshInProgressForTests);
        Assert.False(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public async Task RefreshAsync_WhenBackgroundRefreshAlreadyRunning_AwaitsExistingRefreshCompletion()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        _ = cache.GetPlaybackDeviceName(Role.Multimedia);
        Task backgroundRefreshCompletion = cache.WaitForRefreshCompletionForTestsAsync();

        Task explicitRefresh = cache.RefreshAsync();

        AssertSharedCompletionWhileRefreshActiveOrAllowCompletedRace(
            cache,
            backgroundRefreshCompletion,
            cache.WaitForRefreshCompletionForTestsAsync());

        await Task.WhenAll(backgroundRefreshCompletion, explicitRefresh);

        Assert.False(cache.RefreshInProgressForTests);
        Assert.False(cache.CacheInvalidatedForTests);
        Assert.False(string.IsNullOrWhiteSpace(cache.LastTopologyFingerprintForTests));
    }

    [Fact]
    public async Task GetPlaybackDevice_WhenMaterializationFails_InvalidatesCache()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [null, new DeviceCacheSnapshotData("playback-a", "Playback A", DeviceState.Active), null],
                [null, null, null]),
            materializeDeviceById: _ => throw new COMException("materialize-failed")));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        _ = cache.GetPlaybackDevice(Role.Multimedia);

        Assert.True(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public async Task AccessDiagnostics_AggregatesCacheHitsAndFallbacks_IntoSummaryLog()
    {
        using var loggerScope = new TestLoggerScope(nameof(DeviceCacheHelperTests), "device-cache-access-diagnostics.log", LogLevel.Debug);
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        MMDevice playbackDevice = CreateTestDevice();
        MMDevice recordingFallback = CreateTestDevice();
        DeviceCacheHelper.InitializeForTests(
            audio,
            CreateRuntime(
                capture: new DeviceCacheRefreshCapture(
                    [null, new DeviceCacheSnapshotData("playback-a", "Playback A", DeviceState.Active), null],
                    [null, null, null]),
                materializeDeviceById: id => id == "playback-a" ? playbackDevice : null,
                fallbackRecordingDevice: (role, _) => role == Role.Console ? recordingFallback : null),
            loggerScope.Logger);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        Assert.Same(playbackDevice, cache.GetPlaybackDevice(Role.Multimedia));
        Assert.Same(playbackDevice, cache.GetPlaybackDevice(Role.Multimedia));
        Assert.Same(recordingFallback, cache.GetRecordingDevice(Role.Console));
        Assert.Same(recordingFallback, cache.GetRecordingDevice(Role.Console));

        cache.FlushAccessDiagnosticsForTests();

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains("event=get-playback-cache-hit role=Multimedia reason=cache-hit count=2", logText, StringComparison.Ordinal);
        Assert.Contains("event=get-recording-fallback role=Console reason=invalid-cache-entry count=2", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_WhenSnapshotCaptureThrows_LogsFetchFailure_AndClearsExpiry()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            captureException: new InvalidOperationException("capture-failed")));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        Assert.Equal(0, cache.DeviceCacheExpiryTicksForTests);
        Assert.False(cache.RefreshInProgressForTests);
        Assert.True(cache.WaitForRefreshCompletionForTestsAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task GetPlaybackDeviceName_WhenBackgroundRefreshThrows_LeavesRefreshIdle()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            runRefreshWorkAsync: _ => throw new InvalidOperationException("background-refresh-failed")));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        _ = cache.GetPlaybackDeviceName(Role.Multimedia);

        await cache.WaitForRefreshCompletionForTestsAsync();

        Assert.False(cache.RefreshInProgressForTests);
        Assert.True(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public async Task Refresh_WhenFireAndForgetRefreshThrows_LeavesRefreshIdle()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            runRefreshWorkAsync: _ => throw new InvalidOperationException("refresh-task-faulted")));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.Refresh();

        await cache.WaitForRefreshCompletionForTestsAsync();

        Assert.False(cache.RefreshInProgressForTests);
        Assert.True(cache.WaitForRefreshCompletionForTestsAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task GetPlaybackDevice_WhenSnapshotMissingForFallbackRole_ReturnsInjectedFallbackDevice()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        MMDevice fallbackDevice = CreateTestDevice();
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture([null, null, null], [null, null, null]),
            fallbackPlaybackDevice: (role, _) => role == Role.Multimedia ? fallbackDevice : null));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        MMDevice? result = cache.GetPlaybackDevice(Role.Multimedia);

        Assert.Same(fallbackDevice, result);
    }

    [Fact]
    public async Task GetRecordingDevice_WhenSnapshotMissingForFallbackRole_ReturnsInjectedFallbackDevice()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        MMDevice fallbackDevice = CreateTestDevice();
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture([null, null, null], [null, null, null]),
            fallbackRecordingDevice: (role, _) => role == Role.Console ? fallbackDevice : null));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        MMDevice? result = cache.GetRecordingDevice(Role.Console);

        Assert.Same(fallbackDevice, result);
    }

    [Fact]
    public async Task IsPlaybackMuted_ReturnsInjectedMuteState_WhenPrimaryDeviceAvailable()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [null, new DeviceCacheSnapshotData("playback-a", "Playback A", DeviceState.Active), null],
                [null, null, null]),
            materializeDeviceById: _ => CreateTestDevice(),
            probeMuteState: static (_, _, _) => true));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        bool isMuted = cache.IsPlaybackMuted();

        Assert.True(isMuted);
    }

    [Fact]
    public async Task IsPlaybackMuted_ReturnsFalse_WhenProbeReportsUnmuted()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [null, new DeviceCacheSnapshotData("playback-a", "Playback A", DeviceState.Active), null],
                [null, null, null]),
            materializeDeviceById: _ => CreateTestDevice(),
            probeMuteState: static (_, _, _) => false));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        bool isMuted = cache.IsPlaybackMuted();

        Assert.False(isMuted);
        Assert.False(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public async Task TryGetPlaybackMuteState_DistinguishesUnmutedFromUnavailable()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [null, new DeviceCacheSnapshotData("playback-a", "Playback A", DeviceState.Active), null],
                [null, null, null]),
            materializeDeviceById: _ => CreateTestDevice(),
            probeMuteState: static (_, _, _) => false));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;
        await cache.RefreshAsync();

        bool read = cache.TryGetPlaybackMuteState(out bool muted);

        Assert.True(read);
        Assert.False(muted);

        DeviceCacheHelper.DisposeSingleton();
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [null, new DeviceCacheSnapshotData("playback-a", "Playback A", DeviceState.Active), null],
                [null, null, null]),
            materializeDeviceById: _ => CreateTestDevice(),
            probeMuteState: static (_, _, _) => null));
        cache = DeviceCacheHelper.Instance;
        await cache.RefreshAsync();

        read = cache.TryGetPlaybackMuteState(out muted);

        Assert.False(read);
        Assert.False(muted);
    }

    [Fact]
    public async Task IsRecordingMuted_InvalidComObject_InvalidateCacheAndReturnsFalse()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [null, null, null],
                [new DeviceCacheSnapshotData("recording-a", "Recording A", DeviceState.Active), null, null]),
            materializeDeviceById: _ => CreateTestDevice(),
            probeMuteState: static (_, _, _) => throw new COMException("stale-device", unchecked((int)0x80010108))));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        bool isMuted = cache.IsRecordingMuted();

        Assert.False(isMuted);
        Assert.True(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public async Task IsPlaybackMuted_PropagatesProbeContext()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        string? capturedContext = null;
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [null, new DeviceCacheSnapshotData("playback-a", "Playback A", DeviceState.Active), null],
                [null, null, null]),
            materializeDeviceById: _ => CreateTestDevice(),
            probeMuteState: (_, _, context) =>
            {
                capturedContext = context;
                return true;
            }));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        bool isMuted = cache.IsPlaybackMuted("show-window:playback");

        Assert.True(isMuted);
        Assert.Equal("show-window:playback", capturedContext);
    }

    [Fact]
    public async Task GetAllPlaybackDevices_WhenMaterializationFails_UsesFallbackOnlyForMultimedia()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        MMDevice fallbackDevice = CreateTestDevice();
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [
                    new DeviceCacheSnapshotData("playback-console", "Playback Console", DeviceState.Active),
                    new DeviceCacheSnapshotData("playback-multimedia", "Playback Multimedia", DeviceState.Active),
                    new DeviceCacheSnapshotData("playback-communications", "Playback Communications", DeviceState.Active)
                ],
                [null, null, null]),
            materializeDeviceById: _ => null,
            fallbackPlaybackDevice: (role, _) => role == Role.Multimedia ? fallbackDevice : null));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        IReadOnlyList<MMDevice?> devices = cache.GetAllPlaybackDevices();

        Assert.Equal(3, devices.Count);
        Assert.Null(devices[0]);
        Assert.Same(fallbackDevice, devices[1]);
        Assert.Null(devices[2]);
        Assert.False(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public async Task GetAllRecordingDevices_WhenMaterializationFails_UsesFallbackOnlyForConsole()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        MMDevice fallbackDevice = CreateTestDevice();
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [null, null, null],
                [
                    new DeviceCacheSnapshotData("recording-console", "Recording Console", DeviceState.Active),
                    new DeviceCacheSnapshotData("recording-multimedia", "Recording Multimedia", DeviceState.Active),
                    new DeviceCacheSnapshotData("recording-communications", "Recording Communications", DeviceState.Active)
                ]),
            materializeDeviceById: _ => null,
            fallbackRecordingDevice: (role, _) => role == Role.Console ? fallbackDevice : null));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        IReadOnlyList<MMDevice?> devices = cache.GetAllRecordingDevices();

        Assert.Equal(3, devices.Count);
        Assert.Same(fallbackDevice, devices[0]);
        Assert.Null(devices[1]);
        Assert.Null(devices[2]);
        Assert.False(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public async Task GetAllPlaybackDevices_WhenMaterializationSucceeds_PreservesMaterializedEntries()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        MMDevice materializedConsole = CreateTestDevice();
        MMDevice materializedMultimedia = CreateTestDevice();
        MMDevice materializedCommunications = CreateTestDevice();
        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            capture: new DeviceCacheRefreshCapture(
                [
                    new DeviceCacheSnapshotData("playback-console", "Playback Console", DeviceState.Active),
                    new DeviceCacheSnapshotData("playback-multimedia", "Playback Multimedia", DeviceState.Active),
                    new DeviceCacheSnapshotData("playback-communications", "Playback Communications", DeviceState.Active)
                ],
                [null, null, null]),
            materializeDeviceById: id => id switch
            {
                "playback-console" => materializedConsole,
                "playback-multimedia" => materializedMultimedia,
                "playback-communications" => materializedCommunications,
                _ => null,
            }));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();

        IReadOnlyList<MMDevice?> devices = cache.GetAllPlaybackDevices();

        Assert.Same(materializedConsole, devices[0]);
        Assert.Same(materializedMultimedia, devices[1]);
        Assert.Same(materializedCommunications, devices[2]);
        Assert.False(cache.CacheInvalidatedForTests);
    }

    [Fact]
    public async Task InvalidateCache_DuringBackgroundRefresh_DoesNotLeaveRefreshMarkedInProgress()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        _ = cache.GetPlaybackDeviceName(Role.Multimedia);
        Task backgroundRefreshCompletion = cache.WaitForRefreshCompletionForTestsAsync();

        cache.InvalidateCache();

        await backgroundRefreshCompletion;

        Assert.False(cache.RefreshInProgressForTests);
    }

    [Fact]
    public async Task WaitForRefreshCompletionForTestsAsync_OnDisposedCache_ReturnsCompletedTask()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        DeviceCacheHelper.DisposeSingleton();

        Task completion = cache.WaitForRefreshCompletionForTestsAsync();
        await completion;

        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeSingleton_DuringInFlightRefresh_CompletesPublishedRefreshTask()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        DeviceCacheHelper.InitializeForTests(audio, CreateRuntime(
            runRefreshWorkAsync: async refreshWork =>
            {
                await gate.Task;
                await refreshWork();
            }));
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        Task refreshTask = cache.RefreshAsync();
        Task completionTask = cache.WaitForRefreshCompletionForTestsAsync();

        Assert.True(cache.RefreshInProgressForTests);
        Assert.False(completionTask.IsCompleted);

        DeviceCacheHelper.DisposeSingleton();

        await completionTask;

        Assert.True(completionTask.IsCompletedSuccessfully);
        Assert.False(cache.RefreshInProgressForTests);

        gate.SetResult(true);
        await refreshTask;
    }

    [Fact]
    public void AccessorsWithoutRefresh_WhenCacheInvalidated_DoNotStartBackgroundRefresh()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();

        _ = cache.GetPlaybackDeviceIdWithoutRefresh(Role.Multimedia);
        _ = cache.GetPlaybackDeviceNameWithoutRefresh(Role.Multimedia);
        _ = cache.GetRecordingDeviceIdWithoutRefresh(Role.Console);
        _ = cache.GetRecordingDeviceNameWithoutRefresh(Role.Console);

        Assert.True(cache.CacheInvalidatedForTests);
        Assert.False(cache.RefreshInProgressForTests);
        Assert.True(cache.WaitForRefreshCompletionForTestsAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void WaitForRefreshCompletionForTestsAsync_WithoutActiveRefresh_ReturnsCompletedTask()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        Task completion = cache.WaitForRefreshCompletionForTestsAsync();

        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeSingleton_AfterRefresh_ResetsExpiryTicks()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        await cache.RefreshAsync();
        Assert.True(cache.DeviceCacheExpiryTicksForTests > 0);

        DeviceCacheHelper.DisposeSingleton();

        Assert.Equal(0, cache.DeviceCacheExpiryTicksForTests);
    }

    [Fact]
    public void DisposeSingleton_AfterInvalidation_ClearsInvalidationStateAndTicks()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        cache.InvalidateCache();
        Assert.True(cache.CacheInvalidatedForTests);
        Assert.True(cache.LastInvalidateTicksForTests > 0);

        DeviceCacheHelper.DisposeSingleton();

        Assert.False(cache.CacheInvalidatedForTests);
        Assert.Equal(0, cache.LastInvalidateTicksForTests);
    }

    [Fact]
    public void InvalidateCache_OnDisposedInstance_DoesNotRestoreInvalidationState()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        DeviceCacheHelper.DisposeSingleton();

        cache.InvalidateCache();

        Assert.False(cache.CacheInvalidatedForTests);
        Assert.True(cache.LastInvalidateTicksForTests > 0);
    }

    [Fact]
    public async Task DisposedInstance_AccessorsReturnNullOrEmpty_AndRefreshIsNoOp()
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());

        DeviceCacheHelper.Initialize(audio);
        DeviceCacheHelper cache = DeviceCacheHelper.Instance;

        DeviceCacheHelper.DisposeSingleton();

        cache.InvalidateCache();
        await cache.RefreshAsync();

        Assert.Null(cache.GetPlaybackDeviceIdWithoutRefresh(Role.Multimedia));
        Assert.Null(cache.GetPlaybackDeviceNameWithoutRefresh(Role.Multimedia));
        Assert.Null(cache.GetRecordingDeviceIdWithoutRefresh(Role.Console));
        Assert.Null(cache.GetRecordingDeviceNameWithoutRefresh(Role.Console));
        Assert.Empty(cache.GetAllPlaybackDevices());
        Assert.Empty(cache.GetAllRecordingDevices());
    }

    private static void AssertSharedCompletionWhileRefreshActiveOrAllowCompletedRace(
        DeviceCacheHelper cache,
        Task initialCompletion,
        Task observedCompletion)
    {
        if (cache.RefreshInProgressForTests)
        {
            Assert.Same(initialCompletion, observedCompletion);
            return;
        }

        Assert.True(initialCompletion.IsCompletedSuccessfully);
        Assert.True(observedCompletion.IsCompletedSuccessfully);
    }

    private static DeviceCacheHelperRuntime CreateRuntime(
        DeviceCacheRefreshCapture? capture = null,
        Exception? captureException = null,
        Func<string, MMDevice?>? materializeDeviceById = null,
        Func<Role, string, MMDevice?>? fallbackPlaybackDevice = null,
        Func<Role, string, MMDevice?>? fallbackRecordingDevice = null,
        Func<Logger, MMDevice, string, bool?>? probeMuteState = null,
        Func<Func<Task>, Task>? runRefreshWorkAsync = null)
    {
        return new DeviceCacheHelperRuntime
        {
            CaptureSnapshotsAsync = () => captureException != null
                ? Task.FromException<DeviceCacheRefreshCapture>(captureException)
                : Task.FromResult(capture ?? new DeviceCacheRefreshCapture([null, null, null], [null, null, null])),
            MaterializeDeviceById = materializeDeviceById ?? (_ => null),
            GetFallbackPlaybackDevice = fallbackPlaybackDevice ?? ((_, _) => null),
            GetFallbackRecordingDevice = fallbackRecordingDevice ?? ((_, _) => null),
            ProbeMuteState = probeMuteState ?? ((_, _, _) => null),
            RunRefreshWorkAsync = runRefreshWorkAsync ?? (refreshWork => refreshWork()),
        };
    }

    private static MMDevice CreateTestDevice()
    {
        return (MMDevice)RuntimeHelpers.GetUninitializedObject(typeof(MMDevice));
    }

    private static int GetPendingAccessDiagnosticsCount(DeviceCacheHelper cache)
    {
        var field = typeof(DeviceCacheHelper).GetField("_pendingAccessDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(cache);
        Assert.NotNull(value);
        return ((System.Collections.IDictionary)value!).Count;
    }

    private static int GetPlaybackSnapshotCount(DeviceCacheHelper cache)
    {
        var field = typeof(DeviceCacheHelper).GetField("_cachedPlaybackSnapshots", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var snapshots = (Array)field!.GetValue(cache)!;
        int count = 0;
        foreach (object? snapshot in snapshots)
        {
            if (snapshot != null)
            {
                count++;
            }
        }

        return count;
    }
}


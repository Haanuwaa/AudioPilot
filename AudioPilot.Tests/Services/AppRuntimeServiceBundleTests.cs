using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services;

[Collection("DeviceCacheHelperIsolation")]
public sealed class AppRuntimeServiceBundleTests
{
    [Fact]
    public async Task AudioDisposalFailureDoesNotAuthorizeReleasingItsPlatformDependencies()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppRuntimeServiceBundleTests));
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        int cacheReleases = 0;
        int coreReleases = 0;
        var bundle = AppRuntimeServiceBundle.Create(new SettingsService(workspace.PrimaryDir, workspace.FallbackDir),
            InMemoryStartupTaskStore.CreateStartupService(), audio,
            new(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance)),
            ownsCoreAudioExecutor: true, disposeDeviceCache: () => cacheReleases++,
            disposeCoreAudioExecutor: () => coreReleases++);
        bundle.InitializeDeviceCache();
        var error = new InvalidOperationException("synthetic failed audio shutdown owner");
        TestPrivateAccess.SetField(audio, "_disposeTask", Task.FromException(error));
        try
        {
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => bundle.DisposeAsync().AsTask());
            Assert.Contains(error, failure.InnerExceptions);
            Assert.Equal(0, cacheReleases);
            Assert.Equal(0, coreReleases);
        }
        finally
        {
            TestPrivateAccess.SetField<Task?>(audio, "_disposeTask", null);
            await audio.DisposeAsync();
            DeviceCacheHelper.DisposeSingleton();
        }
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForAudioAndReleasesDependentsExactlyOnce()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppRuntimeServiceBundleTests));
        using var log = TestLoggerScope.CreateFileBacked(nameof(AppRuntimeServiceBundleTests), "bundle-deferred.log");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int released = 0;
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter(), logger: log.Logger,
            sessionMonitoringDrainOverride: () => { entered.TrySetResult(); return release.Task; });
        AppRuntimeServiceBundle bundle = AppRuntimeServiceBundle.Create(new SettingsService(workspace.PrimaryDir, workspace.FallbackDir),
            InMemoryStartupTaskStore.CreateStartupService(), audio,
            new(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance)),
            ownsCoreAudioExecutor: true, disposeCoreAudioExecutor: () => released++);
        Task first = bundle.DisposeAsync().AsTask();
        Task second = bundle.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Task.Run(() => TestLogFileAssert.WaitForLogText(log.LogPath, "audio-disposal-deferred", timeoutMs: 10000), TestContext.Current.CancellationToken);
            Assert.Same(first, second);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, released);
        }
        finally { release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await bundle.DisposeAsync();
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task UndrainedMicrophoneOwnerRetainsPlatformResources()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppRuntimeServiceBundleTests));
        int released = 0;
        AppRuntimeServiceBundle bundle = AppRuntimeServiceBundle.Create(new SettingsService(workspace.PrimaryDir, workspace.FallbackDir),
            InMemoryStartupTaskStore.CreateStartupService(), new AudioDeviceService(new FakeInputListenPropertyWriter()),
            new(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance)),
            ownsCoreAudioExecutor: true, disposeCoreAudioExecutor: () => released++);
        await bundle.DisposeAsync(releasePlatformResources: false);
        await bundle.DisposeAsync();
        Assert.Equal(0, released);
    }

    [Fact]
    public async Task CacheFailureStillAttemptsCoreCleanupAndIsReportedToAllCallers()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppRuntimeServiceBundleTests));
        DeviceCacheHelper.DisposeSingleton();
        int coreReleases = 0;
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        AppRuntimeServiceBundle bundle = AppRuntimeServiceBundle.Create(new SettingsService(workspace.PrimaryDir, workspace.FallbackDir),
            InMemoryStartupTaskStore.CreateStartupService(), audio,
            new(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance)), ownsCoreAudioExecutor: true,
            disposeDeviceCache: () => { DeviceCacheHelper.DisposeSingleton(); throw new InvalidOperationException("Injected cache cleanup failure."); },
            disposeCoreAudioExecutor: () => coreReleases++);
        Assert.Same(bundle.InitializeDeviceCache(), bundle.InitializeDeviceCache());
        await Assert.ThrowsAsync<AggregateException>(() => bundle.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<AggregateException>(() => bundle.DisposeAsync().AsTask());
        Assert.Equal(1, coreReleases);
        Assert.Throws<ObjectDisposedException>(() => bundle.InitializeDeviceCache());
    }
}

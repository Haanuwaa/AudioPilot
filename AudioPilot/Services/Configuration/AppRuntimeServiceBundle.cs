using AudioPilot.Logging;

namespace AudioPilot.Services.Configuration
{
    public sealed class AppRuntimeServiceBundle : IDisposable, IAsyncDisposable
    {
        private readonly Lock _disposeSync = new();
        private readonly bool _ownsCoreAudioExecutor;
        private readonly Action _disposeDeviceCache;
        private readonly Action _disposeCoreAudioExecutor;
        private DeviceCacheHelper? _deviceCache;
        private Task? _disposeTask;

        private AppRuntimeServiceBundle(
            SettingsService settingsService,
            StartupService startupService,
            AudioDeviceService audioService,
            Lazy<BluetoothReconnectCoordinator> bluetoothReconnectCoordinator,
            bool ownsCoreAudioExecutor = false,
            Action? disposeDeviceCache = null,
            Action? disposeCoreAudioExecutor = null)
        {
            SettingsService = settingsService;
            StartupService = startupService;
            AudioService = audioService;
            BluetoothReconnectCoordinator = bluetoothReconnectCoordinator;
            _ownsCoreAudioExecutor = ownsCoreAudioExecutor;
            _disposeDeviceCache = disposeDeviceCache ?? DeviceCacheHelper.DisposeSingleton;
            _disposeCoreAudioExecutor = disposeCoreAudioExecutor ?? ComThreadingHelper.DisposeCoreAudioExecutor;
        }

        public SettingsService SettingsService { get; }
        public StartupService StartupService { get; }
        public AudioDeviceService AudioService { get; }
        public Lazy<BluetoothReconnectCoordinator> BluetoothReconnectCoordinator { get; }

        public static AppRuntimeServiceBundle CreateDefault()
        {
            AudioDeviceService? audioService = null;
            try
            {
                var settingsService = new SettingsService();
                var startupService = new StartupService();
                audioService = new AudioDeviceService();
                return new AppRuntimeServiceBundle(settingsService, startupService, audioService,
                    new Lazy<BluetoothReconnectCoordinator>(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                    ownsCoreAudioExecutor: true);
            }
            catch
            {
                try { audioService?.Dispose(); }
                catch (Exception ex) { Logger.Instance.Warning("AppRuntimeServiceBundle", "composition-audio-cleanup-failed", nameof(CreateDefault), ex); }
                try { ComThreadingHelper.DisposeCoreAudioExecutor(); }
                catch (Exception ex) { Logger.Instance.Warning("AppRuntimeServiceBundle", "composition-core-audio-cleanup-failed", nameof(CreateDefault), ex); }
                throw;
            }
        }

        internal static AppRuntimeServiceBundle Create(
            SettingsService settingsService,
            StartupService startupService,
            AudioDeviceService audioService,
            Lazy<BluetoothReconnectCoordinator> bluetoothReconnectCoordinator,
            bool ownsCoreAudioExecutor = false,
            Action? disposeDeviceCache = null,
            Action? disposeCoreAudioExecutor = null)
        {
            return new AppRuntimeServiceBundle(
                settingsService,
                startupService,
                audioService,
                bluetoothReconnectCoordinator, ownsCoreAudioExecutor, disposeDeviceCache, disposeCoreAudioExecutor);
        }

        internal DeviceCacheHelper InitializeDeviceCache()
        {
            lock (_disposeSync)
            {
                ObjectDisposedException.ThrowIf(_disposeTask != null, this);
                if (_deviceCache != null) return _deviceCache;
                DeviceCacheHelper.Initialize(AudioService);
                return _deviceCache = DeviceCacheHelper.Instance;
            }
        }

        public void Dispose()
        {
            try { DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(Constants.AppConstants.Timing.ShutdownStepTimeoutMs)).GetAwaiter().GetResult(); }
            catch (TimeoutException) { Logger.Instance.Warning("AppRuntimeServiceBundle", "runtime-disposal-deferred | reason=synchronous-dispose-timeout"); }
        }

        public ValueTask DisposeAsync() => DisposeAsync(releasePlatformResources: true);

        /// <summary>Retains platform resources when an external audio owner has not drained.</summary>
        internal ValueTask DisposeAsync(bool releasePlatformResources)
        {
            lock (_disposeSync)
            {
                return new(_disposeTask ??= Task.Run(() => DisposeCoreAsync(releasePlatformResources)));
            }
        }

        private async Task DisposeCoreAsync(bool releasePlatformResources)
        {
            List<Exception>? failures = null;
            bool audioDrained = false;
            try { await AudioService.DisposeAsync().ConfigureAwait(false); audioDrained = true; }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            if (audioDrained)
            {
                DisposeResource(() => BluetoothAssociationEndpointSource.DisposeWatcherCache(Logger.Instance));
                if (releasePlatformResources)
                {
                    if (_deviceCache != null) DisposeResource(_disposeDeviceCache);
                    if (_ownsCoreAudioExecutor) DisposeResource(_disposeCoreAudioExecutor);
                }
            }
            if (failures != null) throw new AggregateException("Runtime resource cleanup failed.", failures);

            void DisposeResource(Action dispose)
            {
                try { dispose(); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
            }
        }
    }
}

using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;
namespace AudioPilot.CliHost
{
    internal sealed partial class LocalHeadlessCommandRunner : IDisposable, ICliCommandRuntime
    {
        internal sealed record AudioOverrides(
            Func<bool?>? GetDefaultPlaybackMute = null,
            Func<bool?>? GetDefaultCaptureMute = null,
            Func<(string? DeviceId, string? DeviceName)>? GetDefaultPlaybackDeviceSnapshot = null,
            Func<(bool Success, float Percent, bool Muted)>? GetDefaultPlaybackVolume = null,
            Func<(bool Success, float Percent, bool Muted)>? GetDefaultCaptureVolume = null,
            Func<string, (bool Success, float Percent, bool Muted)>? GetPlaybackVolumeByDeviceId = null,
            Func<string, (bool Success, float Percent, bool Muted)>? GetCaptureVolumeByDeviceId = null,
            Func<bool, bool>? TrySetPlaybackMute = null,
            Func<bool, bool>? TrySetMicrophoneMute = null,
            Func<float, (bool Success, float Percent, bool Muted)>? TrySetPlaybackVolume = null,
            Func<float, (bool Success, float Percent, bool Muted)>? TrySetCaptureVolume = null,
            Func<string, float, (bool Success, float Percent, bool Muted)>? TrySetPlaybackVolumeByDeviceId = null,
            Func<string, float, (bool Success, float Percent, bool Muted)>? TrySetCaptureVolumeByDeviceId = null,
            Func<bool>? TryToggleListenToInput = null,
            Func<bool, bool>? TrySetListenToInput = null,
            Func<(bool? Enabled, string? MonitorTargetOutputDeviceName)>? GetListenStatusSnapshot = null,
            Func<List<CycleDevice>>? GetActiveOutputDeviceInfos = null,
            Func<List<CycleDevice>>? GetActiveInputDeviceInfos = null,
            Func<string?>? GetCurrentOutputDeviceId = null,
            Func<string?>? GetCurrentInputDeviceId = null,
            Func<string>? GetLogRootDirectory = null,
            Func<uint, string, string, string?, ProcessAudioDeviceSwitchResult>? SwitchApplicationOutputDeviceDetailedAsync = null,
            Func<uint, string, string, string?, ProcessAudioDeviceSwitchResult>? SwitchApplicationInputDeviceDetailedAsync = null,
            Func<string, string, bool, bool, bool, bool, string?, (bool Success, string? DeviceName)>? SwitchAudioDeviceAsync = null,
            Func<string, string, string?, (bool Success, string? DeviceName)>? SwitchInputDeviceToAsync = null,
            Func<bool>? HasDefaultInputDevice = null,
            Func<Action, IDisposable>? SubscribeDeviceStateChanged = null,
            Func<int, Task>? DelayAsync = null);

        internal static int ResolveBluetoothPostAttemptRecheckDelayMs()
        {
            return RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs;
        }

        private sealed class DelegateDisposable(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;

            public void Dispose()
            {
                Interlocked.Exchange(ref _dispose, null)?.Invoke();
            }
        }

        internal sealed record RuntimeServiceFactories(
            Func<SettingsService>? CreateSettingsService = null,
            Func<StartupService>? CreateStartupService = null,
            Func<AudioDeviceService>? CreateAudioService = null,
            Func<BluetoothReconnectCoordinator>? CreateBluetoothReconnectCoordinator = null,
            Func<IRoutineProcessSnapshotProvider>? CreateRoutineProcessSnapshotProvider = null);

        private sealed class LazyRuntimeServices : IDisposable
        {
            private readonly Lock _sync = new();
            private readonly Func<SettingsService> _createSettingsService;
            private readonly Func<StartupService> _createStartupService;
            private readonly Func<AudioDeviceService> _createAudioService;
            private readonly Func<BluetoothReconnectCoordinator> _createBluetoothReconnectCoordinator;
            private readonly Action? _disposeOwner;
            private int _disposeStarted;
            private SettingsService? _settingsService;
            private StartupService? _startupService;
            private AudioDeviceService? _audioService;
            private BluetoothReconnectCoordinator? _bluetoothReconnectCoordinator;

            private LazyRuntimeServices(
                Func<SettingsService> createSettingsService,
                Func<StartupService> createStartupService,
                Func<AudioDeviceService> createAudioService,
                Func<BluetoothReconnectCoordinator> createBluetoothReconnectCoordinator,
                Action? disposeOwner = null)
            {
                _createSettingsService = createSettingsService;
                _createStartupService = createStartupService;
                _createAudioService = createAudioService;
                _createBluetoothReconnectCoordinator = createBluetoothReconnectCoordinator;
                _disposeOwner = disposeOwner;
            }

            public static LazyRuntimeServices CreateDefault(RuntimeServiceFactories? factories = null)
            {
                return new LazyRuntimeServices(
                    factories?.CreateSettingsService ?? (() => new SettingsService()),
                    factories?.CreateStartupService ?? (() => StartupService.CreateForExecutable(ResolveGuiExecutablePath())),
                    factories?.CreateAudioService ?? (() => new AudioDeviceService()),
                    factories?.CreateBluetoothReconnectCoordinator ?? (() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance)));
            }

            public static LazyRuntimeServices CreateFromBundle(AppRuntimeServiceBundle runtimeServices)
            {
                ArgumentNullException.ThrowIfNull(runtimeServices);
                return new LazyRuntimeServices(
                    () => runtimeServices.SettingsService,
                    () => runtimeServices.StartupService,
                    () => runtimeServices.AudioService,
                    () => runtimeServices.BluetoothReconnectCoordinator.Value,
                    runtimeServices.Dispose);
            }

            public SettingsService GetSettingsService() => GetOrCreate(ref _settingsService, _createSettingsService);
            public StartupService GetStartupService() => GetOrCreate(ref _startupService, _createStartupService);
            public AudioDeviceService GetAudioService() => GetOrCreate(ref _audioService, _createAudioService);
            public BluetoothReconnectCoordinator GetBluetoothReconnectCoordinator() => GetOrCreate(ref _bluetoothReconnectCoordinator, _createBluetoothReconnectCoordinator);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                {
                    return;
                }

                _disposeOwner?.Invoke();
                if (_disposeOwner == null)
                {
                    _audioService?.Dispose();
                }
                BluetoothAssociationEndpointSource.DisposeWatcherCache(Logger.Instance);
            }

            private TService GetOrCreate<TService>(ref TService? service, Func<TService> factory)
                where TService : class
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, typeof(LazyRuntimeServices));

                if (service != null)
                {
                    return service;
                }

                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposeStarted != 0, typeof(LazyRuntimeServices));
                    service ??= factory();
                    return service;
                }
            }
        }

        private readonly record struct RoutineSwitchExecutionResult(bool Success, string? DeviceName, string? FailureDetail = null);

        private readonly LazyRuntimeServices _runtimeServices;
        private readonly IRoutineProcessSnapshotProvider _routineProcessSnapshotProvider;
        private readonly AudioOverrides? _audioOverrides;
        private readonly ExecutionHistoryService _executionHistory = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private int _disposeStarted;

        public LocalHeadlessCommandRunner()
            : this(LazyRuntimeServices.CreateDefault(), new RoutineProcessSnapshotProvider(Logger.Instance), null)
        {
        }

        internal LocalHeadlessCommandRunner(AppRuntimeServiceBundle runtimeServices, AudioOverrides? audioOverrides = null)
            : this(LazyRuntimeServices.CreateFromBundle(runtimeServices), new RoutineProcessSnapshotProvider(Logger.Instance), audioOverrides)
        {
        }

        internal LocalHeadlessCommandRunner(RuntimeServiceFactories runtimeServiceFactories, AudioOverrides? audioOverrides = null)
            : this(
                LazyRuntimeServices.CreateDefault(runtimeServiceFactories),
                runtimeServiceFactories.CreateRoutineProcessSnapshotProvider?.Invoke() ?? new RoutineProcessSnapshotProvider(Logger.Instance),
                audioOverrides)
        {
        }

        private LocalHeadlessCommandRunner(
            LazyRuntimeServices runtimeServices,
            IRoutineProcessSnapshotProvider routineProcessSnapshotProvider,
            AudioOverrides? audioOverrides)
        {
            _runtimeServices = runtimeServices;
            _routineProcessSnapshotProvider = routineProcessSnapshotProvider;
            _audioOverrides = audioOverrides;
        }

        private SettingsService SettingsService => _runtimeServices.GetSettingsService();
        private StartupService StartupService => _runtimeServices.GetStartupService();

        internal static string ResolveGuiExecutablePath()
        {
            return Path.Combine(AppContext.BaseDirectory, $"{AppConstants.Identity.AppName}.exe");
        }
        private AudioDeviceService AudioService => _runtimeServices.GetAudioService();
        private BluetoothReconnectCoordinator BluetoothReconnectCoordinator => _runtimeServices.GetBluetoothReconnectCoordinator();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _lifetimeCts.Cancel();
            try
            {
                _runtimeServices.Dispose();
            }
            finally
            {
                _lifetimeCts.Dispose();
            }
        }

        public Task<CliExecutionResult> ExecuteAsync(CliCommand command)
        {
            return CliCommandExecutor.ExecuteAsync(command, this);
        }

        public void ShowWindow()
        {
        }

        public void HideWindow()
        {
        }

    }
}

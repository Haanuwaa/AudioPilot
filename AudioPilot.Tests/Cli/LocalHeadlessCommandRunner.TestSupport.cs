using AudioPilot.CliHost;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    private sealed class HeadlessRunnerScope : IDisposable
    {
        private readonly TestSettingsWorkspace _workspace;

        public HeadlessRunnerScope(
            Settings settings,
            IBluetoothReconnectService? reconnectService = null,
            LocalHeadlessCommandRunner.AudioOverrides? audioOverrides = null)
        {
            _workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));

            var settingsService = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
            settingsService.SaveSettings(settings);

            AppRuntimeServiceBundle bundle = AppRuntimeServiceBundle.Create(
                settingsService,
                new StartupService(),
                new AudioDeviceService(new FakeInputListenPropertyWriter()),
                new Lazy<BluetoothReconnectCoordinator>(() => new BluetoothReconnectCoordinator(reconnectService ?? new BluetoothReconnectService(), Logger.Instance), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

            Runner = new LocalHeadlessCommandRunner(bundle, audioOverrides);
        }

        public LocalHeadlessCommandRunner Runner { get; }

        public void Dispose()
        {
            Runner.Dispose();
            _workspace.Dispose();
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool DisposeCalled { get; private set; }

        public void Dispose()
        {
            DisposeCalled = true;
        }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose()
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class RuntimeServiceCreationTracker(TestSettingsWorkspace workspace)
    {
        private readonly TestSettingsWorkspace _workspace = workspace;

        public int SettingsCreated { get; private set; }
        public int StartupCreated { get; private set; }
        public int AudioCreated { get; private set; }
        public int BluetoothCreated { get; private set; }

        public LocalHeadlessCommandRunner CreateRunner()
        {
            return new LocalHeadlessCommandRunner(
                new LocalHeadlessCommandRunner.RuntimeServiceFactories(
                    CreateSettingsService: () =>
                    {
                        SettingsCreated++;
                        return new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
                    },
                    CreateStartupService: () =>
                    {
                        StartupCreated++;
                        return new StartupService();
                    },
                    CreateAudioService: () =>
                    {
                        AudioCreated++;
                        return new AudioDeviceService(new FakeInputListenPropertyWriter());
                    },
                    CreateBluetoothReconnectCoordinator: () =>
                    {
                        BluetoothCreated++;
                        return new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance);
                    }));
        }
    }

}

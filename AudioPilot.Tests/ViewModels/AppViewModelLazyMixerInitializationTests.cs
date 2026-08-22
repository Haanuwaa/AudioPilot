using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("DeviceCacheHelperIsolation")]
public sealed class AppViewModelLazyMixerInitializationTests
{
    private static readonly Lock DeviceCacheInitLock = new();

    [Fact]
    public void Constructor_DoesNotInitializeMixersUntilNeeded()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var fixture = CreateFixture();

            Assert.False(IsMixerInitialized(fixture.ViewModel, "_mixer"));
            Assert.False(IsMixerInitialized(fixture.ViewModel, "_inputMixer"));
            Assert.Empty(fixture.ViewModel.ActiveMixerSessions);
            Assert.Equal(0, fixture.OutputFactoryCallCount);
            Assert.Equal(0, fixture.InputFactoryCallCount);
        });
    }

    [Fact]
    public void MixerProperty_InitializesOnlyOutputMixerOnFirstUse()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var fixture = CreateFixture();

            _ = fixture.ViewModel.Mixer;

            Assert.Equal(1, fixture.OutputFactoryCallCount);
            Assert.Equal(0, fixture.InputFactoryCallCount);
            Assert.True(IsMixerInitialized(fixture.ViewModel, "_mixer"));
            Assert.False(IsMixerInitialized(fixture.ViewModel, "_inputMixer"));
        });
    }

    [Fact]
    public void InputMixerProperty_InitializesOnlyInputMixerOnFirstUse()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var fixture = CreateFixture();

            _ = fixture.ViewModel.InputMixer;

            Assert.Equal(0, fixture.OutputFactoryCallCount);
            Assert.Equal(1, fixture.InputFactoryCallCount);
            Assert.False(IsMixerInitialized(fixture.ViewModel, "_mixer"));
            Assert.True(IsMixerInitialized(fixture.ViewModel, "_inputMixer"));
        });
    }

    [Theory]
    [InlineData((int)MixerRefreshTarget.Output, 1, 0)]
    [InlineData((int)MixerRefreshTarget.Input, 0, 1)]
    public void RefreshMixerAsync_InitializesOnlyRequestedMixerOnFirstUse(
        int targetValue,
        int expectedOutputFactoryCalls,
        int expectedInputFactoryCalls)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var fixture = CreateFixture();
            MixerRefreshTarget target = (MixerRefreshTarget)targetValue;

            InvokeNonPublicTask(fixture.ViewModel, "RefreshMixerAsync", target, true);

            Assert.Equal(expectedOutputFactoryCalls, fixture.OutputFactoryCallCount);
            Assert.Equal(expectedInputFactoryCalls, fixture.InputFactoryCallCount);
        });
    }

    [Fact]
    public void CreatingSecondMixer_LaterConnectsSharedSessionBridge()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var fixture = CreateFixture();

            MixerViewModel outputMixer = fixture.ViewModel.Mixer;
            Assert.Null(TestPrivateAccess.GetField<object?>(outputMixer, "_sharedSessionBridge"));

            MixerViewModel inputMixer = fixture.ViewModel.InputMixer;

            Assert.NotNull(TestPrivateAccess.GetField<object?>(outputMixer, "_sharedSessionBridge"));
            Assert.NotNull(TestPrivateAccess.GetField<object?>(inputMixer, "_sharedSessionBridge"));
        });
    }

    private static AppViewModelLazyMixerFixture CreateFixture()
    {
        var workspace = new TestSettingsWorkspace(nameof(AppViewModelLazyMixerInitializationTests));
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        DeviceCacheHelper.DisposeSingleton();
        EnsureDeviceCacheInitialized(audio);

        var overlay = new OverlayService(action => action(), _ => new RecordingOverlayPresenter());
        Logger logger = Logger.Instance;
        var bluetoothReconnectCoordinator = new BluetoothReconnectCoordinator(new BluetoothReconnectService(logger), logger);
        var switchCoordinator = new AppSwitchCommandCoordinator(audio, overlay, logger, bluetoothReconnectCoordinator);
        var cliOverlayCoordinator = new AppCliOverlayCoordinator(audio, overlay, new MediaOverlayCommandService(), logger, static () => null);
        var startup = (StartupService)RuntimeHelpers.GetUninitializedObject(typeof(StartupService));
        var shell = new AppShellService(new TestAppMainWindowManager(), new TestAppTrayIconService());
        var processLifecycleMonitor = new FakeProcessLifecycleMonitor();
        var steamBigPictureSignalMonitor = new FakeSteamBigPictureSignalMonitor();
        int outputFactoryCallCount = 0;
        int inputFactoryCallCount = 0;

        var viewModel = new AppViewModel(
            new SettingsService(workspace.PrimaryDir, workspace.FallbackDir),
            startup,
            audio,
            new HotkeyService(),
            cliOverlayCoordinator,
            switchCoordinator,
            shell,
            mixerFactory: () =>
            {
                EnsureDeviceCacheInitialized(audio);
                outputFactoryCallCount++;
                return new MixerViewModel(audio, Dispatcher.CurrentDispatcher, AudioMixerMode.Output);
            },
            inputMixerFactory: () =>
            {
                EnsureDeviceCacheInitialized(audio);
                inputFactoryCallCount++;
                return new MixerViewModel(audio, Dispatcher.CurrentDispatcher, AudioMixerMode.Input);
            },
            overlay,
            Dispatcher.CurrentDispatcher,
            bluetoothReconnectCoordinator,
            processLifecycleMonitor,
            steamBigPictureSignalMonitor,
            logger);

        return new AppViewModelLazyMixerFixture(
            workspace,
            viewModel,
            overlay,
            audio,
            () => outputFactoryCallCount,
            () => inputFactoryCallCount);
    }

    private static bool IsMixerInitialized(AppViewModel viewModel, string fieldName)
    {
        return TestPrivateAccess.GetField<MixerViewModel?>(viewModel, fieldName) != null;
    }

    private static void EnsureDeviceCacheInitialized(AudioDeviceService audio)
    {
        lock (DeviceCacheInitLock)
        {
            if (!DeviceCacheHelper.IsInitialized)
            {
                DeviceCacheHelper.Initialize(audio);
            }
        }
    }

    private static void InvokeNonPublicTask(object target, string methodName, params object?[] args)
    {
        Type[] parameterTypes = [.. args.Select(static arg => arg?.GetType() ?? typeof(object))];
        var method = target.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);

        Assert.NotNull(method);
        Task task = Assert.IsType<Task>(method!.Invoke(target, args), exactMatch: false);
        TestPrivateAccess.RunTaskOnDispatcher(task);
    }

    private sealed class AppViewModelLazyMixerFixture(
        TestSettingsWorkspace workspace,
        AppViewModel viewModel,
        OverlayService overlay,
        AudioDeviceService audio,
        Func<int> getOutputFactoryCallCount,
        Func<int> getInputFactoryCallCount) : IDisposable
    {
        public AppViewModel ViewModel { get; } = viewModel;
        public int OutputFactoryCallCount => getOutputFactoryCallCount();
        public int InputFactoryCallCount => getInputFactoryCallCount();

        public void Dispose()
        {
            TestPrivateAccess.RunTaskOnDispatcher(ViewModel.CleanupAsync());
            DeviceCacheHelper.DisposeSingleton();
            overlay.Dispose();
            audio.Dispose();
            workspace.Dispose();
        }
    }
}

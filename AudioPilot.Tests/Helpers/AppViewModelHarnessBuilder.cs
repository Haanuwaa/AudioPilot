using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.Services.Diagnostics;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Helpers;

internal static class AppViewModelHarnessBuilder
{
    private static readonly Lock DeviceCacheInitSync = new();

    private static AppViewModel CreateConstructedViewModel(
        SettingsService settingsService,
        HotkeyService hotkeys,
        AudioDeviceService audio,
        DeviceCacheHelper deviceCache,
        Dispatcher dispatcher,
        Logger logger,
        OverlayService overlay,
        StartupService? startupService = null,
        IProcessLifecycleMonitor? processLifecycleMonitor = null,
        ISteamBigPictureSignalMonitor? steamBigPictureSignalMonitor = null,
        bool cancelBackgroundWork = true,
        IAudioEndpointTestService? audioEndpointTestService = null,
        IAppDialogService? dialogService = null,
        IRoutineProcessSnapshotProvider? routineProcessSnapshotProvider = null)
    {
        var bluetoothReconnectCoordinator = new BluetoothReconnectCoordinator(new BluetoothReconnectService(logger), logger);
        var switchCoordinator = new AppSwitchCommandCoordinator(audio, overlay, logger, bluetoothReconnectCoordinator);
        var startup = startupService ?? InMemoryStartupTaskStore.CreateStartupService(logger);
        var cliOverlayCoordinator = new AppCliOverlayCoordinator(
            audio, overlay, new MediaOverlayCommandService(), logger, static () => null);
        var shell = new AppShellService(new TestAppMainWindowManager(), new TestAppTrayIconService());
        var viewModel = new AppViewModel(
            settingsService, startup, audio, hotkeys, cliOverlayCoordinator, switchCoordinator, shell,
            () => new MixerViewModel(audio, dispatcher, AudioMixerMode.Output, logger, deviceCache),
            () => new MixerViewModel(audio, dispatcher, AudioMixerMode.Input, logger, deviceCache),
            overlay, dispatcher, bluetoothReconnectCoordinator, processLifecycleMonitor, steamBigPictureSignalMonitor,
            logger, deviceCache: deviceCache, audioEndpointTestService: audioEndpointTestService,
            routineProcessSnapshotProvider: routineProcessSnapshotProvider,
            dialogService: dialogService ?? new RecordingAppDialogService());
        if (cancelBackgroundWork)
            TestPrivateAccess.GetField<CancellationTokenSource>(viewModel, "_backgroundWorkCts").Cancel();
        return viewModel;
    }

    private static (AudioDeviceService Audio, DeviceCacheHelper Cache) CreateAudioServices(
        Logger logger, IPerAppAudioRoutingResetter? routingResetter = null,
        Func<IAudioDeviceEnumerator, AudioSessionService>? sessionFactory = null)
    {
        DeviceCacheHelper? cache = null;
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter(),
            perAppAudioRoutingResetter: routingResetter, audioSessionServiceFactory: sessionFactory,
            logger: logger, deviceCacheAccessor: () => cache);
        cache = new DeviceCacheHelper(audio, logger: logger);
        return (audio, cache);
    }

    internal static void EnsureDeviceCacheInitialized(AudioDeviceService audio)
    {
        lock (DeviceCacheInitSync)
        {
            if (!DeviceCacheHelper.IsInitialized)
            {
                DeviceCacheHelper.Initialize(audio);
            }
        }
    }

    internal static AppViewModelInteractionHarness CreateInteractionHarness(
        TestSettingsWorkspace workspace,
        Dispatcher dispatcher,
        IPerAppAudioRoutingResetter? perAppAudioRoutingResetter = null,
        Logger? logger = null,
        StartupService? startupService = null,
        bool allowBackgroundWork = false,
        IProcessLifecycleMonitor? processLifecycleMonitor = null,
        ISteamBigPictureSignalMonitor? steamBigPictureSignalMonitor = null,
        IAudioEndpointTestService? audioEndpointTestService = null,
        IRoutineProcessSnapshotProvider? routineProcessSnapshotProvider = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return CreateInteractionHarness(
            workspace.PrimaryDir,
            workspace.FallbackDir,
            dispatcher,
            perAppAudioRoutingResetter,
            logger,
            startupService,
            allowBackgroundWork,
            processLifecycleMonitor,
            steamBigPictureSignalMonitor,
            audioEndpointTestService,
            routineProcessSnapshotProvider);
    }

    internal static AppViewModel CreateUninitializedViewModelShell(Logger? logger = null, Settings? cachedSettings = null)
    {
        var viewModel = (AppViewModel)RuntimeHelpers.GetUninitializedObject(typeof(AppViewModel));

        Logger resolvedLogger = logger ?? Logger.Instance;
        TestPrivateAccess.SetField(viewModel, "_routineRuntime", new RoutineRuntimeCoordinator([], new RoutineRuntimeCallbacks(
            () => viewModel.CurrentSettings?.Routines.Items ?? [], _ => [], (_, _) => { }, (_, _) => { },
            (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => { }, _ => false,
            _ => { }, _ => { }, _ => { }, (_, _) => Task.CompletedTask), resolvedLogger));
        TestPrivateAccess.SetField(viewModel, "_logger", resolvedLogger);
        TestPrivateAccess.SetField(viewModel, "_executionHistory", new Lazy<ExecutionHistoryService>(() => new ExecutionHistoryService(), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));
        TestPrivateAccess.SetField(viewModel, "_settingsLock", new Lock());
        TestPrivateAccess.SetField(viewModel, "_mixerInitializationLock", new Lock());
        TestPrivateAccess.SetField(viewModel, "_mixerRestoreQueueLock", new Lock());
        TestPrivateAccess.SetField(viewModel, "_routineRuntimeStateLock", new Lock());
        TestPrivateAccess.SetField(viewModel, "_mixerRestoreQueueIdleTcs", CreateCompletedTaskCompletionSource());
        TestPrivateAccess.SetField(viewModel, "_outputDevices", new List<CycleDevice>());
        TestPrivateAccess.SetField(viewModel, "_inputDevices", new List<CycleDevice>());

        if (cachedSettings != null)
        {
            TestPrivateAccess.SetField(viewModel, "_cachedSettings", cachedSettings);
        }

        return viewModel;
    }

    internal static AppViewModel CreateSettingsBackedViewModelShell(
        string primaryDir,
        string fallbackDir,
        Settings cachedSettings,
        Logger? logger = null)
    {
        var viewModel = CreateUninitializedViewModelShell(logger, cachedSettings);
        TestPrivateAccess.SetField(viewModel, "_settings", new SettingsService(primaryDir, fallbackDir));
        return viewModel;
    }

    internal static AppViewModel CreateOrchestrationViewModelShell(
        Logger? logger = null,
        Window? window = null)
    {
        Logger resolvedLogger = logger ?? Logger.Instance;
        AppViewModel viewModel = CreateUninitializedViewModelShell(resolvedLogger);
        Window resolvedWindow = window ?? TestWindowFactory.CreateOffscreenWindow();
        var shellManager = new TestAppMainWindowManager(resolvedWindow);
        var shell = new AppShellService(shellManager, new TestAppTrayIconService());
        var mixer = (MixerViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MixerViewModel));
        var inputMixer = (MixerViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MixerViewModel));
        var windowState = new AppWindowStateCoordinator();
        windowState.MarkStartupVisibilityResolved();
        TestPrivateAccess.SetField(viewModel, "_shell", shell);
        TestPrivateAccess.SetField(viewModel, "_backgroundTasks", new ConcurrentDictionary<int, Task>());
        TestPrivateAccess.SetField(viewModel, "_backgroundWorkCts", new CancellationTokenSource());
        TestPrivateAccess.SetField(viewModel, "_backgroundWorkHelper", new BackgroundWorkQueue(resolvedLogger, () => viewModel.IsCleaningUpForTests()));
        TestPrivateAccess.SetField(viewModel, "_windowState", windowState);

        TestPrivateAccess.SetField(viewModel, "_mixer", mixer);
        TestPrivateAccess.SetField(viewModel, "_inputMixer", inputMixer);
        TestPrivateAccess.SetField(viewModel, "_mixerFactory", () => mixer);
        TestPrivateAccess.SetField(viewModel, "_inputMixerFactory", () => inputMixer);
        TestPrivateAccess.SetField(viewModel, "_mixersConnected", true);
        return viewModel;
    }

    private static TaskCompletionSource<object?> CreateCompletedTaskCompletionSource()
    {
        var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(null);
        return source;
    }

    internal static AppViewModel CreateTrayCapableViewModelShell(AppShellService shell)
    {
        ArgumentNullException.ThrowIfNull(shell);

        AppViewModel viewModel = CreateUninitializedViewModelShell();
        TestPrivateAccess.SetField(viewModel, "_shell", shell);
        TestPrivateAccess.SetField(viewModel, "_windowState", new AppWindowStateCoordinator());
        return viewModel;
    }

    internal static RoutineSaveHarness CreateRoutineSaveHarness(
        TestSettingsWorkspace workspace,
        Dispatcher dispatcher,
        Logger? logger = null,
        IAppDialogService? dialogService = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        var hotkeys = new HotkeyService();
        Logger resolvedLogger = logger ?? Logger.Instance;
        var (audio, deviceCache) = CreateAudioServices(resolvedLogger);
        var overlayPresenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(action => action(), _ => overlayPresenter);
        AppViewModel viewModel = CreateConstructedViewModel(settingsService, hotkeys, audio, deviceCache, dispatcher, resolvedLogger, overlay, dialogService: dialogService);
        return new RoutineSaveHarness(viewModel, settingsService, hotkeys, audio, deviceCache);
    }

    internal static RoutineReconnectHarness CreateRoutineReconnectHarness(
        AudioDeviceService audio,
        FakeBluetoothReconnectService fakeReconnectService,
        Logger logger,
        RecordingOverlayPresenter? overlayPresenter = null)
    {
        var deviceCache = new DeviceCacheHelper(audio, logger: logger);

        var workspace = new TestSettingsWorkspace("AppViewModelRoutineReconnectTests");
        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        var hotkeys = new HotkeyService();
        overlayPresenter ??= new RecordingOverlayPresenter();
        var overlay = new OverlayService(action => action(), _ => overlayPresenter);
        var viewModel = CreateConstructedViewModel(
            settingsService,
            hotkeys,
            audio,
            deviceCache,
            Dispatcher.CurrentDispatcher,
            logger,
            overlay);

        var cachedSettings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                BluetoothReconnectEnabled = true
            }
        };
        settingsService.SaveSettings(cachedSettings);

        TestPrivateAccess.SetField(viewModel, "_cachedSettings", cachedSettings);
        var reconnect = new BluetoothReconnectCoordinator(fakeReconnectService, logger);
        TestPrivateAccess.SetField(viewModel, "_routineBluetoothReconnectCoordinator", reconnect);
        viewModel.RoutineExecutionOperationsForTests = RoutineExecutionOperations.Create(audio, () => reconnect,
            BluetoothReconnectOptions.FromSettings(cachedSettings), logger) with
        {
            GetActiveDevices = static _ => [],
            FindActiveDevice = static (_, _) => null,
            WaitForReconnectAsync = (_, _, result, _, token) => result.Attempted
                ? AppViewModel.RoutineReconnectPostAttemptDelayAsyncForTests(0, token) : Task.CompletedTask,
            SwitchDefaultAsync = static request => ValueTask.FromResult<(bool Success, string? DeviceName)>((false, request.Target.Name)),
            SwitchRolesAsync = null,
        };

        return new RoutineReconnectHarness(viewModel, hotkeys, workspace, deviceCache);
    }

    internal static AppViewModelInteractionHarness CreateInteractionHarness(
        string primaryDir,
        string fallbackDir,
        Dispatcher dispatcher,
        IPerAppAudioRoutingResetter? perAppAudioRoutingResetter = null,
        Logger? logger = null,
        StartupService? startupService = null,
        bool allowBackgroundWork = false,
        IProcessLifecycleMonitor? processLifecycleMonitor = null,
        ISteamBigPictureSignalMonitor? steamBigPictureSignalMonitor = null,
        IAudioEndpointTestService? audioEndpointTestService = null,
        IRoutineProcessSnapshotProvider? routineProcessSnapshotProvider = null)
    {
        Logger resolvedLogger = logger ?? Logger.Instance;
        var settingsService = new SettingsService(primaryDir, fallbackDir, resolvedLogger);
        var hotkeys = new HotkeyService();
        var (audio, deviceCache) = CreateAudioServices(resolvedLogger, perAppAudioRoutingResetter);

        var overlayPresenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(action => action(), _ => overlayPresenter);
        var messages = new RecordingAppDialogService();
        AppViewModel viewModel = CreateConstructedViewModel(
            settingsService,
            hotkeys,
            audio,
            deviceCache,
            dispatcher,
            resolvedLogger,
            overlay,
            startupService,
            processLifecycleMonitor,
            steamBigPictureSignalMonitor,
            cancelBackgroundWork: !allowBackgroundWork,
            audioEndpointTestService: audioEndpointTestService,
                routineProcessSnapshotProvider: routineProcessSnapshotProvider,
            dialogService: messages);

        return new AppViewModelInteractionHarness(viewModel, settingsService, hotkeys, audio, messages, overlayPresenter.Messages, allowBackgroundWork, deviceCache);
    }

    internal static RoutineStatefulHarness CreateRoutineStatefulHarness(
        Dispatcher dispatcher,
        Logger? logger = null,
        IProcessLifecycleMonitor? monitor = null,
        ISteamBigPictureSignalMonitor? steamBigPictureSignalMonitor = null,
        Func<IAudioDeviceEnumerator, AudioSessionService>? audioSessionServiceFactory = null)
    {
        var workspace = new TestSettingsWorkspace("AppViewModelRoutineAppStartTests");
        var hotkeys = new HotkeyService();
        var (audio, deviceCache) = CreateAudioServices(logger ?? Logger.Instance, sessionFactory: audioSessionServiceFactory);

        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        var overlayPresenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(action => action(), _ => overlayPresenter);
        var viewModel = CreateConstructedViewModel(
            settingsService,
            hotkeys,
            audio,
            deviceCache,
            dispatcher,
            logger ?? Logger.Instance,
            overlay,
            processLifecycleMonitor: monitor ?? new FakeProcessLifecycleMonitor(),
            steamBigPictureSignalMonitor: steamBigPictureSignalMonitor ?? new FakeSteamBigPictureSignalMonitor());

        return new RoutineStatefulHarness(viewModel, hotkeys, audio, workspace, deviceCache);
    }

    internal sealed class AppViewModelInteractionHarness(
        AppViewModel viewModel,
        SettingsService settingsService,
        HotkeyService hotkeys,
        AudioDeviceService audio,
        RecordingAppDialogService messages,
        List<(OverlayDeviceKind? kind, string header, string? deviceName)> overlayMessages,
        bool allowBackgroundWork,
        DeviceCacheHelper deviceCache) : IDisposable
    {
        private readonly bool _allowBackgroundWork = allowBackgroundWork;

        public AppViewModel ViewModel { get; } = viewModel;
        public SettingsService SettingsService { get; } = settingsService;
        public HotkeyService Hotkeys { get; } = hotkeys;
        public AudioDeviceService Audio { get; } = audio;
        public DeviceCacheHelper DeviceCache { get; } = deviceCache;
        public RecordingAppDialogService Messages { get; } = messages;
        public List<(OverlayDeviceKind? kind, string header, string? deviceName)> OverlayMessages { get; } = overlayMessages;

        public void SetCachedSettings(Settings settings)
        {
            ReplaceCollection(ViewModel.OutputCycleDevices, [.. settings.DeviceSwitching.Output.CycleDevices.Select(CloneCycleDevice)]);
            ReplaceCollection(ViewModel.InputCycleDevices, [.. settings.DeviceSwitching.Input.CycleDevices.Select(CloneCycleDevice)]);

            ViewModel.Hotkey.LoadFromString(settings.DeviceSwitching.Output.SwitchHotkey);
            ViewModel.OutputReverseHotkey.LoadFromString(settings.DeviceSwitching.Output.ReverseSwitchHotkey);
            ViewModel.InputHotkey.LoadFromString(settings.DeviceSwitching.Input.SwitchHotkey);
            ViewModel.InputReverseHotkey.LoadFromString(settings.DeviceSwitching.Input.ReverseSwitchHotkey);
            ViewModel.OutputHotkeysEnabled = settings.DeviceSwitching.Output.HotkeysEnabled;
            ViewModel.InputHotkeysEnabled = settings.DeviceSwitching.Input.HotkeysEnabled;
            ViewModel.RunAtStartup = settings.RunAtStartup;
            ViewModel.PreserveAudioLevels = settings.DeviceSwitching.PreserveAudioLevels;
            ViewModel.KeepMuteStateWhenSwitching = settings.DeviceSwitching.KeepMuteStateWhenSwitching;
            ViewModel.Theme = settings.Theme;
            ViewModel.OverlayEnabled = settings.Overlay.Enabled;
            ViewModel.OverlayPosition = settings.Overlay.Position;
            ViewModel.OverlayDurationSecondsText = settings.Overlay.DurationSeconds.ToString("0.0");

            ViewModel.SettingsAutoSaveEnabledDraft = settings.Miscellaneous.AutoSaveEnabled;
            ViewModel.SettingsRunAtStartupDraft = settings.RunAtStartup;
            ViewModel.SettingsThemeDraft = settings.Theme;
            ViewModel.SettingsPreserveAudioLevelsDraft = settings.DeviceSwitching.PreserveAudioLevels;
            ViewModel.SettingsKeepMuteStateWhenSwitchingDraft = settings.DeviceSwitching.KeepMuteStateWhenSwitching;
            ViewModel.SettingsAutoScrollToMixerOnRestoreDraft = settings.Miscellaneous.AutoScrollToMixerOnRestore;
            ViewModel.SettingsOverlayEnabledDraft = settings.Overlay.Enabled;
            ViewModel.SettingsBluetoothReconnectEnabledDraft = settings.DeviceSwitching.BluetoothReconnectEnabled;
            ViewModel.SettingsDeviceReferenceFileModeDraft = settings.Miscellaneous.DeviceReferenceFileMode;
            ViewModel.SettingsLogLevelDraft = Enum.TryParse(settings.Miscellaneous.LogLevel, ignoreCase: true, out LogLevel parsed) ? parsed : LogLevel.Info;
            ViewModel.SettingsOverlayPositionDraft = settings.Overlay.Position;
            ViewModel.SettingsOverlayDurationSecondsDraft = settings.Overlay.DurationSeconds.ToString("0.0");
            ViewModel.SettingsListenMonitorOutputDeviceIdDraft = settings.Hotkeys.Listen.MonitorOutputDeviceId;
            ViewModel.SettingsToggleAppVisibilityHotkeyDraft = settings.Hotkeys.App.ToggleAppVisibility;
            ViewModel.SettingsPlayPauseHotkeyDraft = settings.Hotkeys.Media.PlayPause;
            ViewModel.SettingsNextTrackHotkeyDraft = settings.Hotkeys.Media.NextTrack;
            ViewModel.SettingsPreviousTrackHotkeyDraft = settings.Hotkeys.Media.PreviousTrack;
            ViewModel.SettingsMuteMicHotkeyDraft = settings.Hotkeys.Mute.Mic;
            ViewModel.SettingsMuteSoundHotkeyDraft = settings.Hotkeys.Mute.Sound;
            ViewModel.SettingsDeafenHotkeyDraft = settings.Hotkeys.Mute.Deafen;
            ViewModel.SettingsListenToInputHotkeyDraft = settings.Hotkeys.Listen.ListenToInput;
            ViewModel.SettingsOutputRoleMultimediaDraft = settings.DeviceSwitching.Output.SwitchRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase);
            ViewModel.SettingsOutputRoleCommunicationsDraft = settings.DeviceSwitching.Output.SwitchRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase);
            ViewModel.SettingsOutputRoleConsoleDraft = settings.DeviceSwitching.Output.SwitchRoles.Contains("Console", StringComparer.OrdinalIgnoreCase);
            ViewModel.SettingsInputRoleMultimediaDraft = settings.DeviceSwitching.Input.SwitchRoles.Contains("Multimedia", StringComparer.OrdinalIgnoreCase);
            ViewModel.SettingsInputRoleCommunicationsDraft = settings.DeviceSwitching.Input.SwitchRoles.Contains("Communications", StringComparer.OrdinalIgnoreCase);
            ViewModel.SettingsInputRoleConsoleDraft = settings.DeviceSwitching.Input.SwitchRoles.Contains("Console", StringComparer.OrdinalIgnoreCase);

            if (!_allowBackgroundWork)
            {
                TestPrivateAccess.GetField<CancellationTokenSource>(ViewModel, "_backgroundWorkCts").Cancel();
            }

            FieldInfo? field = typeof(AppViewModel).GetField("_cachedSettings", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(ViewModel, settings);
            ViewModel.SetIsInitializingForTests(false);
        }

        public void Dispose()
        {
            try
            {
                ViewModel.Cleanup();
            }
            finally
            {
                Hotkeys.Dispose();
                DeviceCache.Dispose();
                Audio.Dispose();
            }
        }

        private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
        {
            collection.Clear();
            foreach (T item in items)
            {
                collection.Add(item);
            }
        }

        private static CycleDevice CloneCycleDevice(CycleDevice device)
        {
            return new CycleDevice
            {
                Id = device.Id,
                Name = device.Name,
                DisplayOrder = device.DisplayOrder,
            };
        }
    }

    internal sealed class RoutineSaveHarness(
        AppViewModel viewModel,
        SettingsService settingsService,
        HotkeyService hotkeys,
        AudioDeviceService audio,
        DeviceCacheHelper deviceCache) : IDisposable
    {
        public AppViewModel ViewModel { get; } = viewModel;
        public SettingsService SettingsService { get; } = settingsService;
        public HotkeyService Hotkeys { get; } = hotkeys;

        private AudioDeviceService Audio { get; } = audio;

        public void Dispose()
        {
            try
            {
                ViewModel.Cleanup();
            }
            finally
            {
                Hotkeys.Dispose();
                deviceCache.Dispose();
                Audio.Dispose();
            }
        }
    }

    internal sealed class RoutineReconnectHarness(
        AppViewModel viewModel,
        HotkeyService hotkeys,
        TestSettingsWorkspace workspace,
        DeviceCacheHelper deviceCache) : IDisposable
    {
        public AppViewModel ViewModel { get; } = viewModel;

        private HotkeyService Hotkeys { get; } = hotkeys;
        private TestSettingsWorkspace Workspace { get; } = workspace;

        public void Dispose()
        {
            try
            {
                ViewModel.Cleanup();
            }
            finally
            {
                Hotkeys.Dispose();
                deviceCache.Dispose();
                Workspace.Dispose();
            }
        }
    }

    internal sealed class RoutineStatefulHarness(
        AppViewModel viewModel,
        HotkeyService hotkeys,
        AudioDeviceService audio,
        TestSettingsWorkspace workspace,
        DeviceCacheHelper deviceCache) : IDisposable
    {
        public AppViewModel ViewModel { get; } = viewModel;
        public AudioDeviceService Audio { get; } = audio;

        private HotkeyService Hotkeys { get; } = hotkeys;
        private TestSettingsWorkspace Workspace { get; } = workspace;

        public void Dispose()
        {
            try
            {
                ViewModel.Cleanup();
            }
            finally
            {
                Hotkeys.Dispose();
                deviceCache.Dispose();
                Audio.Dispose();
            }
            Workspace.Dispose();
        }
    }
}

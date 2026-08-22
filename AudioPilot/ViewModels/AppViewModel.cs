using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel : INotifyPropertyChanged
    {
        private readonly SettingsService _settings;
        private readonly StartupService _startup;
        private readonly AudioDeviceService _audio;
        private readonly HotkeyService _hotkeys;
        private readonly AppShellService _shell;
        private readonly OverlayService _overlay;
        private readonly Logger _logger;
        private readonly Dispatcher _dispatcher;
        private readonly Func<MixerViewModel> _mixerFactory;
        private readonly Func<MixerViewModel> _inputMixerFactory;
        private readonly DeviceCacheHelper _deviceCache;
        private readonly AppCliOverlayCoordinator _cliOverlayCoordinator;
        private readonly AppWindowStateCoordinator _windowState = new();
        private readonly AppRefreshCoordinator _refreshCoordinator = new();
        private readonly AppHotkeyRegistrationCoordinator _hotkeyRegistrationCoordinator;
        private readonly AppSwitchCommandCoordinator _switchCoordinator;
        private readonly IAppDialogService _dialogs;
        private readonly AppViewModelBackgroundWorkHelper _backgroundWorkHelper;
        private readonly Lazy<ExecutionHistoryService> _executionHistory;
        private BluetoothReconnectCoordinator? _routineBluetoothReconnectCoordinator;
        private readonly IProcessLifecycleMonitor _routineAppProcessMonitor;
        private readonly IRoutineProcessSnapshotProvider _routineProcessSnapshotProvider;
        private readonly DispatcherTimer _routineLastRunRefreshTimer;
        private readonly Lazy<ScheduleTriggerCoordinator> _scheduleTriggerCoordinator;
        private readonly Lazy<NetworkTriggerCoordinator> _networkTriggerCoordinator;
        private readonly Lazy<ApplicationTriggerCoordinator> _applicationTriggerCoordinator;
        private readonly Lock _muteRefreshLock = new();
        private readonly Lock _settingsLock = new();
        private readonly SemaphoreSlim _settingsWriteSemaphore = new(1, 1);
        private readonly CancellationTokenSource _backgroundWorkCts = new();
        private readonly ConcurrentDictionary<int, Task> _backgroundTasks = new();
        private readonly List<IDisposable> _ownedCommands = [];
        private readonly List<(HotkeyViewModel Draft, PropertyChangedEventHandler Handler)> _hotkeyDraftHandlers = [];

        internal CancellationToken ShutdownToken
        {
            get
            {
                try
                {
                    return _backgroundWorkCts.Token;
                }
                catch (ObjectDisposedException)
                {
                    return new CancellationToken(canceled: true);
                }
            }
        }

        private readonly Lock _mixerInitializationLock = new();
        private readonly Lock _mixerRestoreQueueLock = new();
        private readonly Lock _sessionStateProjectionLock = new();
        private readonly Dictionary<string, AudioSessionLifecycleSignal> _pendingSessionStateProjections = new(StringComparer.OrdinalIgnoreCase);
        private bool _sessionStateProjectionScheduled;
        private Task? _muteRefreshProcessorTask;
        private int _pendingMuteRefreshCount;
        private string _pendingMuteRefreshContext = "unspecified";
        private bool _hasPendingMuteRefresh;
        private volatile bool _isWindowVisible;
        private int _cleanupStarted;
        private volatile bool _isCleaningUp;
        private MixerViewModel? _mixer;
        private MixerViewModel? _inputMixer;
        private bool _mixersConnected;
        private int _pendingMixerRestoreQueueCount;
        private TaskCompletionSource<object?> _mixerRestoreQueueIdleTcs = CreateCompletedMixerRestoreQueueSignal();
        private static readonly AudioSessionItem[] EmptyMixerSessions = [];

        public HotkeyViewModel Hotkey { get; }
        public HotkeyViewModel OutputReverseHotkey { get; }
        public HotkeyViewModel InputHotkey { get; }
        public HotkeyViewModel InputReverseHotkey { get; }
        public HotkeyViewModel SettingsToggleAppVisibilityHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsShowCurrentTrackHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsPlayPauseHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsNextTrackHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsPreviousTrackHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsSeekForwardHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsSeekBackwardHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMuteMicHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMuteSoundHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsDeafenHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsListenToInputHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMasterVolumeUpHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMasterVolumeDownHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMicVolumeUpHotkeyDraftCapture { get; }
        public HotkeyViewModel SettingsMicVolumeDownHotkeyDraftCapture { get; }
        public MixerViewModel Mixer
        {
            get
            {
                EnsureMixerInitialized(AudioMixerMode.Output);
                return _mixer!;
            }
        }

        public MixerViewModel InputMixer
        {
            get
            {
                EnsureMixerInitialized(AudioMixerMode.Input);
                return _inputMixer!;
            }
        }

        public MixerViewModel ActiveMixer => IsInputSettingsTab(SelectedSettingsTabIndex) ? InputMixer : Mixer;
        public IEnumerable<AudioSessionItem> ActiveMixerSessions => TryGetActiveMixer()?.Sessions ?? (IEnumerable<AudioSessionItem>)EmptyMixerSessions;
        public string ActiveMixerHeader => IsInputSettingsTab(SelectedSettingsTabIndex) ? "Recording Mixer" : "Volume Mixer";
        public ObservableCollection<string> AvailableOutputDeviceNames { get; } = [];
        public ObservableCollection<CycleDevice> OutputCycleDevices { get; } = [];
        public ObservableCollection<string> AvailableInputDeviceNames { get; } = [];
        public ObservableCollection<CycleDevice> InputCycleDevices { get; } = [];
        public ObservableCollection<CycleDevice> SettingsListenMonitorOutputDevices { get; } = [];

        public bool ShowBalloonAfterSave { get; private set; }

        public Settings? CurrentSettings
        {
            get
            {
                lock (_settingsLock)
                {
                    return _cachedSettings;
                }
            }
        }

        public Settings? Settings => CurrentSettings;

        public UpdateCheckViewModel Updates { get; }

        private bool TryRunBackgroundWork(Func<CancellationToken, Task> operation, string operationName)
        {
            return _backgroundWorkHelper.TryQueue(
                _backgroundTasks,
                _backgroundWorkCts,
                operation,
                operationName);
        }

        private void RunBackgroundWork(Func<CancellationToken, Task> operation, string operationName)
        {
            _ = TryRunBackgroundWork(operation, operationName);
        }

        private static TaskCompletionSource<object?> CreateCompletedMixerRestoreQueueSignal()
        {
            var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult(null);
            return source;
        }

        private async Task ExecuteSettingsWriteAsync(Func<Task> operation)
        {
            bool lockAcquired = false;
            try
            {
                await _settingsWriteSemaphore.WaitAsync();
                lockAcquired = true;
                await operation();
            }
            finally
            {
                if (lockAcquired)
                {
                    _settingsWriteSemaphore.Release();
                }
            }
        }

        private async Task<TResult> ExecuteSettingsWriteAsync<TResult>(Func<Task<TResult>> operation)
        {
            bool lockAcquired = false;
            try
            {
                await _settingsWriteSemaphore.WaitAsync();
                lockAcquired = true;
                return await operation();
            }
            finally
            {
                if (lockAcquired)
                {
                    _settingsWriteSemaphore.Release();
                }
            }
        }

        private async Task InvokeOnDispatcherAsync(Action action, [CallerMemberName] string callerName = "")
        {
            ArgumentNullException.ThrowIfNull(action);

            if (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                await _dispatcher.InvokeAsync(action).Task;
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping dispatcher action because shutdown is in progress", callerName, ex);
            }
        }

        private async Task<TResult> InvokeOnDispatcherAsync<TResult>(Func<TResult> action, TResult fallback, [CallerMemberName] string callerName = "")
        {
            ArgumentNullException.ThrowIfNull(action);

            if (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return fallback;
            }

            if (_dispatcher.CheckAccess())
            {
                return action();
            }

            try
            {
                return await _dispatcher.InvokeAsync(action).Task;
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping dispatcher result action because shutdown is in progress", callerName, ex);
                return fallback;
            }
        }

        private async Task InvokeOnDispatcherAsync(Func<Task> action, [CallerMemberName] string callerName = "")
        {
            ArgumentNullException.ThrowIfNull(action);

            if (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                await action();
                return;
            }

            try
            {
                await _dispatcher.InvokeAsync(action).Task.Unwrap();
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping dispatcher async action because shutdown is in progress", callerName, ex);
            }
        }

        private async Task<TResult> InvokeOnDispatcherAsync<TResult>(Func<Task<TResult>> action, TResult fallback, [CallerMemberName] string callerName = "")
        {
            ArgumentNullException.ThrowIfNull(action);

            if (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return fallback;
            }

            if (_dispatcher.CheckAccess())
            {
                return await action();
            }

            try
            {
                return await _dispatcher.InvokeAsync(action).Task.Unwrap();
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                _logger.Warning("AppViewModel", "Skipping dispatcher async action because shutdown is in progress", callerName, ex);
                return fallback;
            }
        }

        private static List<string> BuildRoleSelections(bool multimedia, bool communications, bool console)
        {
            List<string> roles = [];
            if (multimedia)
            {
                roles.Add("Multimedia");
            }

            if (communications)
            {
                roles.Add("Communications");
            }

            if (console)
            {
                roles.Add("Console");
            }

            return roles;
        }

        private static void ApplyPersistedAdvancedTuning(Settings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            AdvancedTuningSettings advancedTuning = settings.AdvancedTuning ?? new AdvancedTuningSettings();
            BluetoothReconnectRuntimeConfig.Apply(advancedTuning.BluetoothReconnect);

            SteamBigPictureAdvancedTuningSettings steamBigPicture = advancedTuning.SteamBigPicture ?? new SteamBigPictureAdvancedTuningSettings();
            RuntimeTuningConfig.SteamBigPictureMonitorDebounceMs = steamBigPicture.MonitorDebounceMs;
            RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs = steamBigPicture.ConfirmationDelayMs;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            if (ShouldQueueAutoSaveForProperty(name))
            {
                QueueAutoSave(name);
            }
        }

        private static bool ShouldQueueAutoSaveForProperty(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            if (propertyName.EndsWith("Draft", StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(propertyName, nameof(Theme), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(RunAtStartup), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(OutputHotkeysEnabled), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(InputHotkeysEnabled), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(Hotkey), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(OutputReverseHotkey), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(InputHotkey), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(InputReverseHotkey), StringComparison.Ordinal);
        }

        public bool IsRefreshing => _refreshCoordinator.IsRefreshing;
    }
}

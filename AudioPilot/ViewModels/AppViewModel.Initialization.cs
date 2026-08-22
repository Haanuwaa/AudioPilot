using System.IO;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        internal AppViewModel(
            SettingsService settings,
            StartupService startup,
            AudioDeviceService audio,
            HotkeyService hotkeys,
            AppCliOverlayCoordinator cliOverlayCoordinator,
            AppSwitchCommandCoordinator switchCoordinator,
            AppShellService shell,
            Func<MixerViewModel> mixerFactory,
            Func<MixerViewModel> inputMixerFactory,
            OverlayService overlay,
            Dispatcher dispatcher,
            BluetoothReconnectCoordinator? routineBluetoothReconnectCoordinator = null,
            IProcessLifecycleMonitor? processLifecycleMonitor = null,
            ISteamBigPictureSignalMonitor? steamBigPictureSignalMonitor = null,
            Logger? logger = null,
            ExecutionHistoryService? executionHistory = null,
            IRoutineProcessSnapshotProvider? routineProcessSnapshotProvider = null,
            DeviceCacheHelper? deviceCache = null,
            IAudioEndpointTestService? audioEndpointTestService = null,
            IAppDialogService? dialogService = null)
        {
            _logger = logger ?? Logger.Instance;
            _isInitializing = true;
            _settings = settings;
            _startup = startup;
            _audio = audio;
            _hotkeys = hotkeys;
            _shell = shell;
            _mixerFactory = mixerFactory;
            _inputMixerFactory = inputMixerFactory;
            _overlay = overlay;
            _dispatcher = dispatcher;
            Updates = new UpdateCheckViewModel(dispatcher, _logger);
            _deviceCache = deviceCache ?? DeviceCacheHelper.Instance;
            _cliOverlayCoordinator = cliOverlayCoordinator;
            _hotkeyRegistrationCoordinator = new AppHotkeyRegistrationCoordinator(_hotkeys, _logger);
            _switchCoordinator = switchCoordinator;

            _dialogs = dialogService ?? new AppDialogService(_logger);
            _backgroundWorkHelper = new AppViewModelBackgroundWorkHelper(_logger, () => _isCleaningUp);
            _executionHistory = new Lazy<ExecutionHistoryService>(() => executionHistory ?? new ExecutionHistoryService(), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
            _routineBluetoothReconnectCoordinator = routineBluetoothReconnectCoordinator ?? new BluetoothReconnectCoordinator(new BluetoothReconnectService(), _logger);
            _routineAppProcessMonitor = processLifecycleMonitor ?? ProcessLifecycleMonitorFactory.Create(_logger);
            _routineProcessSnapshotProvider = routineProcessSnapshotProvider ?? new RoutineProcessSnapshotProvider(_logger);
            _unmaterializedSteamBigPictureSignalMonitor = steamBigPictureSignalMonitor;
            _steamBigPictureSignalMonitor = new Lazy<ISteamBigPictureSignalMonitor>(() =>
            {
                ISteamBigPictureSignalMonitor monitor = Interlocked.Exchange(ref _unmaterializedSteamBigPictureSignalMonitor, null)
                    ?? new WinEventSteamBigPictureSignalMonitor();
                try
                {
                    monitor.Signaled += OnSteamBigPictureMonitorSignaled;
                    return monitor;
                }
                catch
                {
                    monitor.Dispose();
                    throw;
                }
            }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
            _audio.AudioSessionCreated += OnAudioSessionCreated;
            _audio.AudioSessionLifecycleChanged += OnAudioSessionLifecycleChanged;
            _audio.DefaultAudioDeviceChanged += OnDefaultAudioDeviceChanged;
            _routineLastRunRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(AppConstants.Timing.RoutineLastRunRefreshIntervalSeconds)
            };
            _routineLastRunRefreshTimer.Tick += OnRoutineLastRunRefreshTimerTick;

            _scheduleTriggerCoordinator = new Lazy<ScheduleTriggerCoordinator>(
                () => new ScheduleTriggerCoordinator(
                    Routines,
                    ExecuteRoutineFromScheduler,
                    _logger,
                    routineSnapshotProvider: GetPersistedRoutineSnapshot,
                    notifyUpcomingRoutines: NotifyUpcomingScheduledRoutines),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

            _networkTriggerCoordinator = new Lazy<NetworkTriggerCoordinator>(
                () => new NetworkTriggerCoordinator(
                    Routines,
                    ExecuteRoutineFromNetwork,
                    _logger,
                    routineSnapshotProvider: GetPersistedRoutineSnapshot),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

            _applicationTriggerCoordinator = new Lazy<ApplicationTriggerCoordinator>(
                () => new ApplicationTriggerCoordinator(
                    Routines,
                    ExecuteRoutineFromApplicationTrigger,
                    _logger,
                    deactivateRoutine: DeactivateRoutineFromApplicationTrigger,
                    routineSnapshotProvider: GetPersistedRoutineSnapshot),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

            Hotkey = new HotkeyViewModel();
            OutputReverseHotkey = new HotkeyViewModel();
            InputHotkey = new HotkeyViewModel();
            InputReverseHotkey = new HotkeyViewModel();
            SettingsToggleAppVisibilityHotkeyDraftCapture = new HotkeyViewModel();
            SettingsShowCurrentTrackHotkeyDraftCapture = new HotkeyViewModel();
            SettingsPlayPauseHotkeyDraftCapture = new HotkeyViewModel();
            SettingsNextTrackHotkeyDraftCapture = new HotkeyViewModel();
            SettingsPreviousTrackHotkeyDraftCapture = new HotkeyViewModel();
            SettingsSeekForwardHotkeyDraftCapture = new HotkeyViewModel();
            SettingsSeekBackwardHotkeyDraftCapture = new HotkeyViewModel();
            SettingsMuteMicHotkeyDraftCapture = new HotkeyViewModel();
            SettingsMuteSoundHotkeyDraftCapture = new HotkeyViewModel();
            SettingsDeafenHotkeyDraftCapture = new HotkeyViewModel();
            SettingsListenToInputHotkeyDraftCapture = new HotkeyViewModel();
            SettingsMasterVolumeUpHotkeyDraftCapture = new HotkeyViewModel();
            SettingsMasterVolumeDownHotkeyDraftCapture = new HotkeyViewModel();
            SettingsMicVolumeUpHotkeyDraftCapture = new HotkeyViewModel();
            SettingsMicVolumeDownHotkeyDraftCapture = new HotkeyViewModel();
            Hotkey.BaseHoverText = "Switches to the next output device in your output cycle. Press Delete to clear.";
            OutputReverseHotkey.BaseHoverText = "Switches to the previous output device in your output cycle (reverse direction). Press Delete to clear.";
            InputHotkey.BaseHoverText = "Switches to the next input device in your input cycle. Press Delete to clear.";
            InputReverseHotkey.BaseHoverText = "Switches to the previous input device in your input cycle (reverse direction). Press Delete to clear.";
            SettingsToggleAppVisibilityHotkeyDraftCapture.BaseHoverText = "Shows AudioPilot when hidden and hides it to the system tray when visible. Press Delete to clear.";
            SettingsShowCurrentTrackHotkeyDraftCapture.BaseHoverText = "Shows the current track without sending a media command. Press Delete to clear.";
            SettingsPlayPauseHotkeyDraftCapture.BaseHoverText = "Sends Play/Pause to the active media app. Press Delete to clear.";
            SettingsNextTrackHotkeyDraftCapture.BaseHoverText = "Skips to the next track/video item. Press Delete to clear.";
            SettingsPreviousTrackHotkeyDraftCapture.BaseHoverText = "Returns to the previous track/video item. Press Delete to clear.";
            SettingsSeekForwardHotkeyDraftCapture.BaseHoverText = "Seeks forward by the configured step when the player supports seeking. Press Delete to clear.";
            SettingsSeekBackwardHotkeyDraftCapture.BaseHoverText = "Seeks backward by the configured step when the player supports seeking. Press Delete to clear.";
            SettingsMuteMicHotkeyDraftCapture.BaseHoverText = "Toggles microphone mute. Press Delete to clear.";
            SettingsMuteSoundHotkeyDraftCapture.BaseHoverText = "Toggles output sound mute. Press Delete to clear.";
            SettingsDeafenHotkeyDraftCapture.BaseHoverText = "Toggles deafen (mute both input and output). Press Delete to clear.";
            SettingsListenToInputHotkeyDraftCapture.BaseHoverText = "Routes output audio to input for monitoring. Press Delete to clear.";
            SettingsMasterVolumeUpHotkeyDraftCapture.BaseHoverText = "Increases master output volume by 5%. Press Delete to clear.";
            SettingsMasterVolumeDownHotkeyDraftCapture.BaseHoverText = "Decreases master output volume by 5%. Press Delete to clear.";
            SettingsMicVolumeUpHotkeyDraftCapture.BaseHoverText = "Increases microphone input volume by 5%. Press Delete to clear.";
            SettingsMicVolumeDownHotkeyDraftCapture.BaseHoverText = "Decreases microphone input volume by 5%. Press Delete to clear.";
            UpdateAdditionalStandaloneHotkeyKeys([]);

            WireHotkeyDraft(Hotkey, nameof(Hotkey));
            WireHotkeyDraft(OutputReverseHotkey, nameof(OutputReverseHotkey));
            WireHotkeyDraft(InputHotkey, nameof(InputHotkey));
            WireHotkeyDraft(InputReverseHotkey, nameof(InputReverseHotkey));

            WireHotkeyDraft(SettingsToggleAppVisibilityHotkeyDraftCapture, nameof(SettingsToggleAppVisibilityHotkeyDraft));
            WireHotkeyDraft(SettingsShowCurrentTrackHotkeyDraftCapture, nameof(SettingsShowCurrentTrackHotkeyDraft));
            WireHotkeyDraft(SettingsPlayPauseHotkeyDraftCapture, nameof(SettingsPlayPauseHotkeyDraft));
            WireHotkeyDraft(SettingsNextTrackHotkeyDraftCapture, nameof(SettingsNextTrackHotkeyDraft));
            WireHotkeyDraft(SettingsPreviousTrackHotkeyDraftCapture, nameof(SettingsPreviousTrackHotkeyDraft));
            WireHotkeyDraft(SettingsSeekForwardHotkeyDraftCapture, nameof(SettingsSeekForwardHotkeyDraft));
            WireHotkeyDraft(SettingsSeekBackwardHotkeyDraftCapture, nameof(SettingsSeekBackwardHotkeyDraft));
            WireHotkeyDraft(SettingsMuteMicHotkeyDraftCapture, nameof(SettingsMuteMicHotkeyDraft));
            WireHotkeyDraft(SettingsMuteSoundHotkeyDraftCapture, nameof(SettingsMuteSoundHotkeyDraft));
            WireHotkeyDraft(SettingsDeafenHotkeyDraftCapture, nameof(SettingsDeafenHotkeyDraft));
            WireHotkeyDraft(SettingsListenToInputHotkeyDraftCapture, nameof(SettingsListenToInputHotkeyDraft));
            WireHotkeyDraft(SettingsMasterVolumeUpHotkeyDraftCapture, nameof(SettingsMasterVolumeUpHotkeyDraft));
            WireHotkeyDraft(SettingsMasterVolumeDownHotkeyDraftCapture, nameof(SettingsMasterVolumeDownHotkeyDraft));
            WireHotkeyDraft(SettingsMicVolumeUpHotkeyDraftCapture, nameof(SettingsMicVolumeUpHotkeyDraft));
            WireHotkeyDraft(SettingsMicVolumeDownHotkeyDraftCapture, nameof(SettingsMicVolumeDownHotkeyDraft));

            SettingsToggleAppVisibilityHotkeyDraft = "Ctrl+Alt+H";
            SettingsShowCurrentTrackHotkeyDraft = string.Empty;
            SettingsPlayPauseHotkeyDraft = string.Empty;
            SettingsNextTrackHotkeyDraft = string.Empty;
            SettingsPreviousTrackHotkeyDraft = string.Empty;
            SettingsSeekForwardHotkeyDraft = "";
            SettingsSeekBackwardHotkeyDraft = "";
            SettingsMuteMicHotkeyDraft = string.Empty;
            SettingsMuteSoundHotkeyDraft = string.Empty;
            SettingsDeafenHotkeyDraft = string.Empty;
            SettingsListenToInputHotkeyDraft = string.Empty;
            SettingsMasterVolumeUpHotkeyDraft = string.Empty;
            SettingsMasterVolumeDownHotkeyDraft = string.Empty;
            SettingsMicVolumeUpHotkeyDraft = string.Empty;
            SettingsMicVolumeDownHotkeyDraft = string.Empty;
            SettingsMasterVolumeStepPercentDraft = "5";
            SettingsMicVolumeStepPercentDraft = "5";
            RefreshSettingsHotkeyExpansionState();
            RefreshHotkeyConflictIndicators();
            InitializeRoutineInfrastructure();
            InitializeDeviceCycleSelectionTracking();
            AudioTesting = new AudioTestingViewModel(
                audioEndpointTestService ?? new AudioEndpointTestService(logger: _logger),
                _dispatcher,
                _logger,
                () => (_outputDevices, CurrentSettings?.Hotkeys.Listen.MonitorOutputDeviceId),
                TryRunBackgroundWork,
                ShutdownToken);
            InitializeAudioEndpointTesting();
            InitializeRoutineSelectionTracking();

            var refreshCommand = TrackCommand(new RelayCommand(() => RefreshDevicesAsync(), () => !IsRefreshing, ex => HandleAsyncCommandException("refresh-devices", ex)), observeViewModel: true);
            var saveCommand = TrackCommand(new RelayCommand(() => SaveSettingsAsync(), () => !_isSaving, ex => HandleAsyncCommandException("save-settings", ex)), observeViewModel: true);
            var saveCurrentContextCommand = TrackCommand(new RelayCommand(SaveCurrentContextAsync, () => !_isSaving && !_isApplyingSettings && !IsSavingRoutines, ex => HandleAsyncCommandException("save-current-context", ex)), observeViewModel: true);
            var applySettingsCommand = TrackCommand(new RelayCommand(() => ApplySettingsAsync(), () => !_isApplyingSettings && !_isSaving, ex => HandleAsyncCommandException("apply-settings", ex)), observeViewModel: true);
            var importSettingsCommand = TrackCommand(new RelayCommand(ImportSettingsAsync, () => !_isApplyingSettings && !_isSaving, ex => HandleAsyncCommandException("import-settings", ex)), observeViewModel: true);
            var exportSettingsCommand = TrackCommand(new RelayCommand(ExportSettingsAsync, () => !_isApplyingSettings && !_isSaving, ex => HandleAsyncCommandException("export-settings", ex)), observeViewModel: true);
            var resetPerAppAudioRoutingCommand = TrackCommand(new RelayCommand(ResetPerAppAudioRoutingAsync, () => !_isApplyingSettings && !_isSaving, ex => HandleAsyncCommandException("reset-per-app-audio", ex)), observeViewModel: true);
            var resetCommand = TrackCommand(new RelayCommand(ResetToDefaultsAsync, ex => HandleAsyncCommandException("reset-settings", ex)));
            var addOutputCycleDeviceCommand = TrackCommand(new RelayCommand(AddOutputCycleDevice, CanAddOutputCycleDevice), observeViewModel: true);
            var removeOutputCycleDeviceCommand = TrackCommand(new RelayCommand(RemoveOutputCycleDevice, () => HasSelectedOutputCycleDevices), observeViewModel: true);
            var moveOutputCycleDeviceUpCommand = TrackCommand(new RelayCommand(MoveOutputCycleDeviceUp, () => HasSingleSelectedOutputCycleDevice && SelectedOutputCycleIndex > 0), observeViewModel: true);
            var moveOutputCycleDeviceDownCommand = TrackCommand(new RelayCommand(MoveOutputCycleDeviceDown, () => HasSingleSelectedOutputCycleDevice && SelectedOutputCycleIndex >= 0 && SelectedOutputCycleIndex < OutputCycleDevices.Count - 1), observeViewModel: true);
            var addInputCycleDeviceCommand = TrackCommand(new RelayCommand(AddInputCycleDevice, CanAddInputCycleDevice), observeViewModel: true);
            var removeInputCycleDeviceCommand = TrackCommand(new RelayCommand(RemoveInputCycleDevice, () => HasSelectedInputCycleDevices), observeViewModel: true);
            var moveInputCycleDeviceUpCommand = TrackCommand(new RelayCommand(MoveInputCycleDeviceUp, () => HasSingleSelectedInputCycleDevice && SelectedInputCycleIndex > 0), observeViewModel: true);
            var moveInputCycleDeviceDownCommand = TrackCommand(new RelayCommand(MoveInputCycleDeviceDown, () => HasSingleSelectedInputCycleDevice && SelectedInputCycleIndex >= 0 && SelectedInputCycleIndex < InputCycleDevices.Count - 1), observeViewModel: true);
            var addRoutineCommand = TrackCommand(new RelayCommand(AddRoutine));
            var editRoutineCommand = TrackCommand(new RelayCommand(EditSelectedRoutine, () => CanEditSelectedRoutine), observeViewModel: true);
            var duplicateRoutineCommand = TrackCommand(new RelayCommand(DuplicateSelectedRoutine, () => HasSingleSelectedRoutine), observeViewModel: true);
            var copyRoutineCommand = TrackCommand(new RelayCommand(CopySelectedRoutineAsync, () => HasSingleSelectedRoutine, ex => HandleAsyncCommandException("copy-routine", ex)), observeViewModel: true);
            var removeRoutineCommand = TrackCommand(new RelayCommand(RemoveSelectedRoutine, () => HasSelectedRoutines), observeViewModel: true);
            var moveRoutineUpCommand = TrackCommand(new RelayCommand(MoveSelectedRoutineUp, () => HasSingleSelectedRoutine && SelectedRoutineIndex > 0), observeViewModel: true);
            var moveRoutineDownCommand = TrackCommand(new RelayCommand(MoveSelectedRoutineDown, () => HasSingleSelectedRoutine && SelectedRoutineIndex >= 0 && SelectedRoutineIndex < Routines.Count - 1), observeViewModel: true);
            var enableSelectedRoutinesCommand = TrackCommand(new RelayCommand(EnableSelectedRoutines, () => CanEnableSelectedRoutines), observeViewModel: true);
            var disableSelectedRoutinesCommand = TrackCommand(new RelayCommand(DisableSelectedRoutines, () => CanDisableSelectedRoutines), observeViewModel: true);
            var saveRoutinesCommand = TrackCommand(new RelayCommand(SaveRoutinesFromButtonAsync, () => !IsSavingRoutines && (HasRoutines || HasUnsavedRoutineChanges), ex => HandleAsyncCommandException("save-routines", ex)), observeViewModel: true);
            var nextSettingsTabCommand = TrackCommand(new RelayCommand(SwitchToNextSettingsTab));
            var selectSettingsTabCommand = TrackCommand(new RelayCommand(SelectSettingsTab));

            OutputCycleDevices.CollectionChanged += OnOutputCycleDevicesCollectionChanged;
            InputCycleDevices.CollectionChanged += OnInputCycleDevicesCollectionChanged;

            RefreshDevicesCommand = refreshCommand;
            SaveSettingsCommand = saveCommand;
            SaveCurrentContextCommand = saveCurrentContextCommand;
            ApplySettingsCommand = applySettingsCommand;
            ImportSettingsCommand = importSettingsCommand;
            ExportSettingsCommand = exportSettingsCommand;
            ResetPerAppAudioRoutingCommand = resetPerAppAudioRoutingCommand;
            ShowCommand = TrackCommand(new RelayCommand(ShowWindow));
            MinimizeCommand = TrackCommand(new RelayCommand(MinimizeWindow));
            ExitCommand = TrackCommand(new RelayCommand(ExitApplication));
            ResetToDefaultsCommand = resetCommand;
            AddOutputCycleDeviceCommand = addOutputCycleDeviceCommand;
            RemoveOutputCycleDeviceCommand = removeOutputCycleDeviceCommand;
            MoveOutputCycleDeviceUpCommand = moveOutputCycleDeviceUpCommand;
            MoveOutputCycleDeviceDownCommand = moveOutputCycleDeviceDownCommand;
            AddInputCycleDeviceCommand = addInputCycleDeviceCommand;
            RemoveInputCycleDeviceCommand = removeInputCycleDeviceCommand;
            MoveInputCycleDeviceUpCommand = moveInputCycleDeviceUpCommand;
            MoveInputCycleDeviceDownCommand = moveInputCycleDeviceDownCommand;
            AddRoutineCommand = addRoutineCommand;
            EditRoutineCommand = editRoutineCommand;
            DuplicateRoutineCommand = duplicateRoutineCommand;
            CopyRoutineCommand = copyRoutineCommand;
            RemoveRoutineCommand = removeRoutineCommand;
            MoveRoutineUpCommand = moveRoutineUpCommand;
            MoveRoutineDownCommand = moveRoutineDownCommand;
            EnableSelectedRoutinesCommand = enableSelectedRoutinesCommand;
            DisableSelectedRoutinesCommand = disableSelectedRoutinesCommand;
            SaveRoutinesCommand = saveRoutinesCommand;
            NextSettingsTabCommand = nextSettingsTabCommand;
            SelectSettingsTabCommand = selectSettingsTabCommand;
        }

        private void HandleAsyncCommandException(string operation, Exception exception)
        {
            _logger.Error(
                "AppViewModel",
                () => $"ui-command-failed | operation={operation} reason={exception.GetType().Name}",
                nameof(HandleAsyncCommandException),
                exception);
            RunBackgroundWork(
                token => _dialogs.ShowErrorAsync(
                    "The operation could not be completed. Check the AudioPilot log for details.",
                    DialogText.Captions.Error,
                    cancellationToken: token),
                $"show-{operation}-error-dialog");
        }

        private RelayCommand TrackCommand(RelayCommand command, bool observeViewModel = false)
        {
            if (observeViewModel)
            {
                command.AddObservedSource(this);
            }

            _ownedCommands.Add(command);
            return command;
        }

        private void DisposeOwnedCommands()
        {
            foreach (IDisposable command in _ownedCommands)
            {
                command.Dispose();
            }

            _ownedCommands.Clear();
        }

        public bool RunAtStartup
        {
            get => _runAtStartup;
            set
            {
                if (_runAtStartup == value)
                    return;

                _runAtStartup = value;
                OnPropertyChanged(nameof(RunAtStartup));
                SyncMirroredSettingsDraftsFromLiveState(runAtStartup: value);

                if (_isInitializing)
                {
                    _logger.Trace("AppViewModel", "Skipping startup registry operations during initialization");
                    return;
                }

                CancellationTokenSource cts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
                    ref _startupDebounceCts);
                bool targetValue = value;
                string startupToggleOpId = AppStartupToggleCoordinator.CreateOperationId();
                bool IsStaleRequest() => AppStartupRegistryCoordinator.IsStaleDebounceRequest(_startupDebounceCts, cts, _runAtStartup, targetValue);

                RunBackgroundWork(async shutdownToken =>
                {
                    await AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                        cts,
                        ownedDebounce => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _startupDebounceCts, ownedDebounce),
                        linkedToken => AppStartupToggleCoordinator.ExecuteDebouncedToggleAsync(
                            new StartupToggleExecutionInput(AppConstants.Timing.StartupDebounceMs, startupToggleOpId),
                            new StartupToggleExecutionDependencies(
                                IsStaleRequest,
                                () => ApplyStartupToggleAsync(targetValue, startupToggleOpId, IsStaleRequest)),
                            _logger,
                            linkedToken),
                        shutdownToken);
                }, nameof(RunAtStartup));
            }
        }

        private async Task<bool> ApplyStartupToggleAsync(bool enable, string opId, Func<bool> isStaleRequest)
        {
            if (_isCleaningUp || AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher)) return false;
            try
            {
                return await ExecuteSettingsWriteAsync(async () =>
                {
                    if (_isCleaningUp || AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher)) return false;
                    if (isStaleRequest()) return true;

                    Settings previous = GetCachedSettingsSnapshot() ?? _settings.LoadSettings();
                    Settings updated = AppStartupRegistryCoordinator.CreateStartupUpdatedSettings(previous, enable);
                    bool persistSettings = _settings.SettingsFileExists();
                    await Task.Run(() => _startup.ApplyRegistration(enable, updated.Miscellaneous.UseScheduledStartup,
                        persistSettings ? () => _settings.SaveSettings(updated) : null, opId));

                    lock (_settingsLock)
                    {
                        _cachedSettings = updated;
                    }
                    if (persistSettings) UpdateLastSettingsWriteTime();
                    await InvokeOnDispatcherAsync(NotifyAutoSaveStateChanged);
                    _logger.Info("AppViewModel", () => $"startup-toggle-applied | opId={opId} enabled={enable} settingsPersisted={persistSettings}");
                    return true;
                });
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", () => $"startup-toggle-failed | opId={opId}", nameof(ApplyStartupToggleAsync), ex);
                await InvokeOnDispatcherAsync(async () =>
                {
                    if (isStaleRequest()) return;
                    bool restored = GetCachedSettingsSnapshot()?.RunAtStartup ?? !enable;
                    SetRunAtStartupInternal(restored);
                    OnPropertyChanged(nameof(RunAtStartup));
                    SyncMirroredSettingsDraftsFromLiveState(runAtStartup: restored);
                    await _dialogs.ShowErrorAsync("Failed to update startup state.");
                });
                return false;
            }
        }

        private async Task<bool> UpdateStartupSettingInJsonAsync(bool startupEnabled, string startupToggleOpId)
        {
            if (!_settings.SettingsFileExists())
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupJsonSyncSkip} | opId={startupToggleOpId} reason=settings-file-missing");
                return true;
            }

            try
            {
                return await ExecuteSettingsWriteAsync(async () =>
                {
                    Settings? cachedSettingsCopy;
                    lock (_settingsLock)
                    {
                        if (_cachedSettings == null)
                        {
                            _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupJsonSyncFailed} | opId={startupToggleOpId} reason=cached-settings-null");
                            return false;
                        }
                        cachedSettingsCopy = _cachedSettings;
                    }

                    var updatedSettings = AppStartupRegistryCoordinator.CreateStartupUpdatedSettings(cachedSettingsCopy, startupEnabled);

                    await Task.Run(() => _settings.SaveSettings(updatedSettings));

                    lock (_settingsLock)
                    {
                        _cachedSettings = updatedSettings;
                    }
                    UpdateLastSettingsWriteTime();
                    await InvokeOnDispatcherAsync(NotifyAutoSaveStateChanged);

                    _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.StartupJsonSyncSuccess} | opId={startupToggleOpId} enabled={startupEnabled}");
                    return true;
                });
            }
            catch (Exception ex)
            {
                _logger.Error("AppViewModel", () => $"startup-json-sync-failed | opId={startupToggleOpId} error={ex.GetType().Name}", nameof(UpdateStartupSettingInJsonAsync), ex);
                return false;
            }
        }

        public async Task InitializeAsync(bool noSettingsFileExists)
        {
            var cacheTask = _deviceCache.RefreshAsync();
            var settingsTask = Task.Run(() => _settings.LoadSettings());

            LoadOutputDevices();
            LoadInputDevices();

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("AppViewModel", () => $"startup-device-snapshot | outputAvailable={_outputDevices.Count} inputAvailable={_inputDevices.Count}");
            }

            var loadedSettings = await settingsTask;
            lock (_settingsLock)
            {
                _cachedSettings = loadedSettings;
                UpdateLastSettingsWriteTime();
            }
            NotifyAutoSaveStateChanged();

            _logger.ApplyLogLevel(_cachedSettings);
            ApplyPersistedAdvancedTuning(loadedSettings);

            var s = _cachedSettings;
            if (s == null)
            {
                _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.AppInitFailed} | reason=settings-load-null");
                return;
            }

            Settings loadedSettingsCopy = s;
            UpdateAdditionalStandaloneHotkeyKeys(loadedSettingsCopy.Hotkeys.Global.AdditionalStandaloneKeys);

            ApplyOutputCycleFromSettings(loadedSettingsCopy.DeviceSwitching.Output.CycleDevices);
            ApplyRoutinesFromSettings(loadedSettingsCopy.Routines?.Items);
            Hotkey.LoadFromString(loadedSettingsCopy.DeviceSwitching.Output.SwitchHotkey);
            OutputReverseHotkey.LoadFromString(loadedSettingsCopy.DeviceSwitching.Output.ReverseSwitchHotkey);
            ApplyInputCycleFromSettings(loadedSettingsCopy.DeviceSwitching.Input.CycleDevices);
            InputHotkey.LoadFromString(loadedSettingsCopy.DeviceSwitching.Input.SwitchHotkey);
            InputReverseHotkey.LoadFromString(loadedSettingsCopy.DeviceSwitching.Input.ReverseSwitchHotkey);
            _outputHotkeysEnabledBackingField = loadedSettingsCopy.DeviceSwitching.Output.HotkeysEnabled;
            _inputHotkeysEnabledBackingField = loadedSettingsCopy.DeviceSwitching.Input.HotkeysEnabled;
            _preserveAudioLevelsBackingField = loadedSettingsCopy.DeviceSwitching.PreserveAudioLevels;
            _overlayEnabledBackingField = loadedSettingsCopy.Overlay.Enabled;
            _overlayPositionBackingField = loadedSettingsCopy.Overlay.Position;
            _overlayDurationSecondsTextBackingField = loadedSettingsCopy.Overlay.DurationSeconds.ToString("0.0");
            Theme = loadedSettingsCopy.Theme;
            OnPropertyChanged(nameof(OverlayEnabled));
            OnPropertyChanged(nameof(OverlayPosition));
            OnPropertyChanged(nameof(OverlayDurationSecondsText));
            _audio.UpdateRoleConfiguration(loadedSettingsCopy.DeviceSwitching.Output.SwitchRoles, loadedSettingsCopy.DeviceSwitching.Input.SwitchRoles);
            ApplyOverlayDisplaySettings();

            GenerateDeviceReferenceFile();

            if (noSettingsFileExists)
            {
                _windowState.ShowBalloonOnFirstMinimize = true;
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.AppInitFirstRun} | showBalloonOnFirstMinimize=true");
            }

            try
            {
                string startupRegistryOpId = $"startup-registry:{Guid.NewGuid():N}";
                bool inStartup = _startup.IsInStartup(startupRegistryOpId);
                bool inStartupWithValidPath = _startup.IsInStartupWithValidPath(startupRegistryOpId, loadedSettingsCopy.Miscellaneous.UseScheduledStartup, includeDisabled: true);
                var startupPlan = AppStartupRegistryCoordinator.BuildInitPlan(
                    noSettingsFileExists,
                    loadedSettingsCopy.RunAtStartup,
                    inStartup,
                    inStartupWithValidPath);

                AppStartupRegistryCoordinator.ExecuteInitPlan(
                    startupPlan,
                    noSettingsFileExists,
                    () => _startup.AddToStartup(startupRegistryOpId, loadedSettingsCopy.Miscellaneous.UseScheduledStartup, preserveDisabledTask: true),
                    () => _startup.RemoveFromStartup(startupRegistryOpId),
                    () => _startup.ValidateAndUpdateStartupPath(startupRegistryOpId, loadedSettingsCopy.Miscellaneous.UseScheduledStartup),
                    startupRegistryOpId,
                    _logger);

                if (startupPlan.RunAtStartupValue != loadedSettingsCopy.RunAtStartup)
                {
                    bool settingsSynchronized = await UpdateStartupSettingInJsonAsync(
                        startupPlan.RunAtStartupValue,
                        startupRegistryOpId);
                    if (settingsSynchronized)
                    {
                        lock (_settingsLock)
                        {
                            loadedSettingsCopy = _cachedSettings ?? loadedSettingsCopy;
                        }
                    }
                    else
                    {
                        _logger.Warning("AppViewModel", () => $"startup-registry-init-settings-sync-failed | opId={startupRegistryOpId} enabled={startupPlan.RunAtStartupValue}");
                    }
                }

                SetRunAtStartupInternal(startupPlan.RunAtStartupValue);
            }
            catch (Exception ex)
            {
                _logger.Warning("AppViewModel", () => $"startup-registry-init-sync-failed | error={ex.GetType().Name}", nameof(InitializeAsync), ex);
            }

            await UpdateMuteFlagsFromSystem("init");
            await cacheTask;

            using (SuppressAutoSave())
            {
                _isInitializing = false;
                OnPropertyChanged(nameof(RunAtStartup));
                OnPropertyChanged(nameof(PreserveAudioLevels));
                OnPropertyChanged(nameof(OverlayEnabled));
                OnPropertyChanged(nameof(OverlayPosition));
                OnPropertyChanged(nameof(OverlayDurationSecondsText));
                OnPropertyChanged(nameof(OutputHotkeysEnabled));
                OnPropertyChanged(nameof(InputHotkeysEnabled));
                SyncSettingsDraftFromCurrentState(resetHotkeyExpansion: true);
            }

            RegisterRoutineHotkeysFromSettings(loadedSettingsCopy, context: "init");

        }

        private void UpdateLastSettingsWriteTime()
        {
            try
            {
                string settingsPath = GetSettingsPath();
                DateTime writeTime = File.GetLastWriteTime(settingsPath);
                lock (_settingsLock)
                {
                    _lastSettingsWriteTime = writeTime;
                }
                _logger.Trace("AppViewModel", () => $"settings-file-write-time-updated | writeTime={writeTime:yyyy-MM-dd HH:mm:ss.fff}");
            }
            catch (Exception ex)
            {
                _logger.Warning("AppViewModel", () => $"settings-file-write-time-update-failed | error={ex.GetType().Name}", nameof(UpdateLastSettingsWriteTime), ex);
            }
        }

        private async Task<bool> HasSettingsFileChangedAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    string settingsPath = GetSettingsPath();
                    DateTime currentWriteTime = File.GetLastWriteTime(settingsPath);

                    DateTime lastTime;
                    lock (_settingsLock)
                    {
                        lastTime = _lastSettingsWriteTime;
                    }

                    bool changed = AppRefreshCoordinator.HasSettingsTimestampChanged(currentWriteTime, lastTime);

                    if (_logger.IsEnabled(AudioPilot.Logging.LogLevel.Trace))
                    {
                        string settingsFileName = Path.GetFileName(settingsPath);
                        _logger.Trace("AppViewModel", () => $"settings-file-change-check | file={settingsFileName} lastWriteTime={lastTime:yyyy-MM-dd HH:mm:ss.fff} currentWriteTime={currentWriteTime:yyyy-MM-dd HH:mm:ss.fff}");
                    }

                    if (changed)
                    {
                        _logger.Info("AppViewModel", () => $"settings-file-external-modification-detected | previousWriteTime={lastTime:yyyy-MM-dd HH:mm:ss.fff} currentWriteTime={currentWriteTime:yyyy-MM-dd HH:mm:ss.fff}");
                        lock (_settingsLock)
                        {
                            _lastSettingsWriteTime = currentWriteTime;
                        }
                    }

                    return changed;
                }
                catch (Exception ex)
                {
                    _logger.Warning("AppViewModel", () => $"settings-file-change-check-failed | error={ex.GetType().Name}", nameof(HasSettingsFileChangedAsync), ex);
                    return false;
                }
            });
        }

        internal bool TryBeginRefreshCycle()
        {
            if (!_refreshCoordinator.TryBeginRefreshCycle())
            {
                return false;
            }

            OnPropertyChanged(nameof(IsRefreshing));
            return true;
        }

        internal void EndRefreshCycle()
        {
            _refreshCoordinator.EndRefreshCycle();
            OnPropertyChanged(nameof(IsRefreshing));
        }

        internal bool EndRefreshCycleAndTryRestart()
        {
            bool restarted = _refreshCoordinator.EndRefreshCycleAndTryRestart();
            if (!restarted)
            {
                OnPropertyChanged(nameof(IsRefreshing));
            }

            return restarted;
        }

        private void SetRunAtStartupInternal(bool value)
        {
            if (_runAtStartup == value)
                return;

            _runAtStartup = value;
        }

        private async Task RefreshDevicesAsync(
            bool promptOnPotentialOverwrite = true,
            bool refreshMixerWhenWindowHidden = true,
            bool checkSettingsFileChanges = true)
        {
            if (!_dispatcher.CheckAccess())
            {
                await InvokeOnDispatcherAsync(
                    () => RefreshDevicesAsync(
                        promptOnPotentialOverwrite,
                        refreshMixerWhenWindowHidden,
                        checkSettingsFileChanges));
                return;
            }

            if (!TryBeginRefreshCycle())
            {
                _refreshCoordinator.MarkPendingRefresh();
                _logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RefreshSkip} | reason=in-progress");
                return;
            }

            bool rerunRequested;
            do
            {
                rerunRequested = false;
                string refreshOpId = $"refresh:{Guid.NewGuid():N}";
                try
                {
                    AppRefreshExecutionResult refreshResult = await AppRefreshCycleCoordinator.ExecuteAsync(
                        new AppRefreshExecutionInput(
                            promptOnPotentialOverwrite,
                            refreshMixerWhenWindowHidden,
                            checkSettingsFileChanges,
                            _shell.IsWindowVisible,
                            _isCleaningUp,
                            OutputCycleDevices.Count),
                        new AppRefreshExecutionDependencies(
                            HasPendingLocalEditsForRefresh,
                            HasSettingsFileChangedAsync,
                            () => _dialogs.ShowAsync(AppDialogRequest.Confirm(
                                "Settings were changed outside the app, and you have unsaved local edits.",
                                DialogText.Captions.ExternalSettingsChangeDetected,
                                AppDialogKind.Warning,
                                "_Reload external settings",
                                "_Keep my edits",
                                AppDialogActionStyle.Destructive)),
                            RefreshAvailableDeviceCollectionsAsync,
                            LoadSettingsForRefreshAsync,
                            ApplyExternallyReloadedSettings,
                            HasUiSettingsDivergedFromCachedSettings,
                            () => _dialogs.ShowAsync(AppDialogRequest.Confirm(
                                "You have unsaved local edits. Refresh can reload saved settings and discard those edits.",
                                DialogText.Captions.UnsavedChanges,
                                AppDialogKind.Warning,
                                "_Reload saved settings",
                                "_Keep my edits",
                                AppDialogActionStyle.Destructive)),
                            GetCachedSettingsSnapshot,
                            GenerateDeviceReferenceFile,
                            () => _deviceCache.Refresh(),
                            () => UpdateMuteFlagsFromSystem("refresh-workflow"),
                            RefreshMixerAsync),
                        refreshOpId,
                        _logger);

                    if (refreshResult.WorkflowOutcome == RefreshWorkflowOutcome.AbortSettingsReloadNull)
                    {
                        _logger.Warning("AppViewModel", () => $"refresh-aborted | opId={refreshOpId} reason=settings-reload-null");
                    }
                }
                finally
                {
                    rerunRequested = EndRefreshCycleAndTryRestart();
                    if (rerunRequested)
                    {
                        _logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.RefreshSkip} | opId={refreshOpId} reason=coalesced-rerun");
                    }
                }
            }
            while (rerunRequested);
        }

        private Task<Settings?> LoadSettingsForRefreshAsync()
        {
            return Task.Run<Settings?>(() => _settings.LoadSettings());
        }

        private Settings? GetCachedSettingsSnapshot()
        {
            lock (_settingsLock)
            {
                return _cachedSettings;
            }
        }

        internal string? GetTrayMenuToggleAppVisibilityHotkey()
        {
            lock (_settingsLock)
            {
                return _cachedSettings?.Hotkeys.App.ToggleAppVisibility;
            }
        }

        internal (string? Output, string? Input) GetTrayMenuSwitchHotkeys()
        {
            lock (_settingsLock)
            {
                return (
                    _cachedSettings?.DeviceSwitching.Output.SwitchHotkey,
                    _cachedSettings?.DeviceSwitching.Input.SwitchHotkey);
            }
        }

        private void NotifyUpcomingScheduledRoutines(IReadOnlyList<ScheduledRoutineReminder> reminders)
        {
            if (_isCleaningUp || _backgroundWorkCts.IsCancellationRequested || AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
            {
                return;
            }

            _dispatcher.BeginInvoke(() =>
            {
                if (_isCleaningUp || _backgroundWorkCts.IsCancellationRequested)
                {
                    return;
                }

                IReadOnlyList<AudioRoutine> current = ScheduleTriggerCoordinator.GetCurrentReminders(
                    reminders, GetPersistedRoutineSnapshot(), DateTime.UtcNow);
                _shell.NotifyUpcomingScheduledRoutines([.. current.Select(static routine => routine.DisplayName)]);
            });
        }

        private void ExecuteRoutineFromScheduler(AudioRoutine routine, string executionSource)
        {
            _dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await ExecuteRoutineAsync(routine, showOverlay: true, executionSource: executionSource);
                }
                catch (Exception ex)
                {
                    _logger.Error("AppViewModel", () => $"scheduled-routine-trigger-failed | routineName={AudioPilot.Logging.LogPrivacy.Label(routine.Name)} reason={ex.GetType().Name}", exception: ex);
                }
            });
        }

        private void ExecuteRoutineFromNetwork(AudioRoutine routine, string executionSource)
        {
            _dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await ExecuteRoutineAsync(routine, showOverlay: true, executionSource: executionSource);
                }
                catch (Exception ex)
                {
                    _logger.Error("AppViewModel", () => $"network-routine-trigger-failed | routineName={AudioPilot.Logging.LogPrivacy.Label(routine.Name)} reason={ex.GetType().Name}", exception: ex);
                }
            });
        }

        private Task ExecuteRoutineFromApplicationTrigger(AudioRoutine routine, int processId)
            => ExecuteRoutineFromApplicationTrigger(
                routine,
                () => ExecuteRoutineForResolvedProcessAsync(
                    routine,
                    processId,
                    showOverlay: true,
                    executionSource: "application-focus"));

        private Task ExecuteRoutineFromApplicationTrigger(AudioRoutine routine, Func<Task> executeRoutineAsync)
        {
            ArgumentNullException.ThrowIfNull(routine);
            ArgumentNullException.ThrowIfNull(executeRoutineAsync);

            return _dispatcher.InvokeAsync(async () =>
            {
                if (!routine.Enabled)
                {
                    return;
                }

                if (!routine.HasExecutionTarget)
                {
                    return;
                }

                await executeRoutineAsync();
            }).Task.Unwrap();
        }

        private void UpdateAutomaticRoutineTriggerStates()
        {
            bool hasScheduledRoutines = Routines.Any(r => r.Enabled && r.TriggerKind == RoutineTriggerKind.Scheduled);
            bool hasNetworkTriggeredRoutines = Routines.Any(r => r.Enabled && r.TriggerKind == RoutineTriggerKind.Network);
            bool hasProcessFocusRoutines = Routines.Any(r =>
                r.Enabled &&
                r.TriggerKind == RoutineTriggerKind.Application &&
                r.ApplicationTriggerMode == ApplicationTriggerMode.ProcessFocus);

            if (hasScheduledRoutines)
            {
                _scheduleTriggerCoordinator.Value.Start();
            }
            else
            {
                if (_scheduleTriggerCoordinator.IsValueCreated)
                {
                    _scheduleTriggerCoordinator.Value.Stop();
                }
            }

            if (hasNetworkTriggeredRoutines)
            {
                _networkTriggerCoordinator.Value.Start();
            }
            else
            {
                if (_networkTriggerCoordinator.IsValueCreated)
                {
                    _networkTriggerCoordinator.Value.Stop();
                }
            }

            if (_applicationTriggerCoordinator.IsValueCreated)
            {
                _applicationTriggerCoordinator.Value.RefreshRoutines();
            }

            if (hasProcessFocusRoutines)
            {
                _applicationTriggerCoordinator.Value.Start();
            }
            else
            {
                if (_applicationTriggerCoordinator.IsValueCreated)
                {
                    _applicationTriggerCoordinator.Value.Stop();
                }
            }

            RefreshRoutineRuntimeTriggers();
        }
    }
}

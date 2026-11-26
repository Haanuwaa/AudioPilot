using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys
{
    internal readonly record struct HotkeyRegistrationOptions(bool AllowHookOnlyFallbackWhenOsRegistrationFails = false);

    internal readonly record struct HotkeyOsRegistrationResult(bool Succeeded, int ErrorCode = 0);

    internal enum HotkeyRegistrationOutcomeKind
    {
        None,
        Registered,
        Fallback,
        Reserved,
        ExternalConflict,
        Unsupported,
        Duplicate,
        ParseError,
        KeyboardHookUnavailable,
        MouseHookUnavailable,
    }

    internal readonly record struct HotkeyRegistrationOutcome(
        HotkeyRegistrationOutcomeKind Kind,
        string Detail = "",
        int Win32Error = 0);

    /// <summary>
    /// Registers and dispatches global hotkeys using hybrid OS registration plus dedicated hook hosts.
    /// </summary>
    public partial class HotkeyService : IDisposable
    {
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly Logger _logger;
        private readonly Lock _lock = new();
        private readonly List<HotkeyDefinition> _hotkeys = [];
        private HotkeyDefinition[] _keyboardMessageBindings = [];
        private readonly HashSet<string> _registeredCombos = [with(StringComparer.OrdinalIgnoreCase)];
        private readonly Dictionary<int, string> _registrationDeliveryById = [];
        private readonly Dictionary<int, HotkeyRegistrationOutcome> _registrationOutcomeById = [];
        private readonly HotkeyDispatchCoordinator _dispatchCoordinator;
        private readonly HotkeyCaptureSession _captureSession;
        private readonly Action<Action> _scheduleWindowFallback;
        private readonly Func<nint> _getForegroundWindow;
        [ThreadStatic] private static (HotkeyService? Service, long Generation, int VolumeSteps, nint FallbackForeground) s_dispatchCapture;
        private readonly Lock _wheelVolumeLock = new();
        private Action? _wheelVolumeBinding;
        private long _wheelVolumeTimestamp, _wheelVolumeGeneration;
        private int _wheelVolumeRemainder;
        internal long CaptureGeneration => ReferenceEquals(s_dispatchCapture.Service, this)
            ? s_dispatchCapture.Generation : _captureSession.Generation;
        internal int VolumeDispatchSteps => ReferenceEquals(s_dispatchCapture.Service, this) ? s_dispatchCapture.VolumeSteps : 1;
        internal nint WindowShortcutFallbackForeground => ReferenceEquals(s_dispatchCapture.Service, this) ? s_dispatchCapture.FallbackForeground : 0;
        internal bool CanDispatch(long generation) => Volatile.Read(ref _disposeStarted) == 0
            && !_captureSession.IsActive && _captureSession.Generation == generation;
        internal bool CanDispatchWindowShortcut(long generation, nint foreground) => CanDispatch(generation)
            && (foreground == 0 || _getForegroundWindow() == foreground);
        private readonly IKeyboardHotkeyMessageHost _keyboardMessageHost;
        private readonly IKeyboardHotkeyCaptureHost _keyboardCaptureHost;
        private readonly IMouseHotkeyCaptureHost _mouseCaptureHost;
        private readonly Func<IntPtr, int, uint, uint, HotkeyOsRegistrationResult> _registerHotKeyInvoker;
        private readonly Action<IntPtr, int> _unregisterHotKeyInvoker;

        private int _disposeStarted;
        private int _verboseRegistrationLogSuppressionCount;
        private bool _initialized;

        public event Action? OnHotkeyPressed;
        public event Action? OnToggleAppVisibilityHotkeyPressed;
        public event Action? OnShowAudioStatusPressed;
        public event Action? OnQuickDevicePickerPressed;
        public event Action? OnVolumeMixerPressed;
        public event Action? OnMediaShowCurrentTrackPressed;
        public event Action? OnMediaPlayPausePressed;
        public event Action? OnMediaNextTrackPressed;
        public event Action? OnMediaPrevTrackPressed;
        public event Action? OnMediaSeekForwardPressed;
        public event Action? OnMediaSeekBackwardPressed;
        public event Action? OnMuteMicPressed;
        public event Action? OnMuteSoundPressed;
        public event Action? OnDeafenPressed;
        public event Action? OnListenToInputPressed;
        public event Action? OnMasterVolumeUpPressed;
        public event Action? OnMasterVolumeDownPressed;
        public event Action? OnMicVolumeUpPressed;
        public event Action? OnMicVolumeDownPressed;
        public event Action? OnForegroundVolumeUpPressed;
        public event Action? OnForegroundVolumeDownPressed;
        public event Action? OnForegroundMutePressed;
        public event Action<bool, Func<bool>>? OnMicrophoneHoldPressed;
        public event Action? OnMicrophoneHoldCancelled;
        internal event Action? CaptureRegistrationFailed;
        internal event Action? CaptureRegistrationCompleted;

        public event Action? OnOutputSwitchHotkeyPressed;
        public event Action? OnInputSwitchHotkeyPressed;
        public event Action? OnOutputReverseSwitchHotkeyPressed;
        public event Action? OnInputReverseSwitchHotkeyPressed;

        private sealed class HotkeyDefinition
        {
            private readonly Action _callback;
            public volatile bool IsActive = true;

            public HotkeyDefinition(Action callback)
            {
                _callback = callback;
                Callback = InvokeIfActive;
            }
            public required int Id { get; init; }
            public required HotkeyMainInput MainInput { get; init; }
            public required List<Key> Modifiers { get; init; }
            public required string Combo { get; init; }
            public required bool UseOsRegistration { get; init; }
            public bool AllowHookOnlyFallbackWhenOsRegistrationFails { get; init; }
            public Action Callback { get; }
            public required string Description { get; init; }
            public HotkeyHeldInputState? HoldState { get; init; }
            public WindowHotkeyDelivery? WindowDelivery { get; init; }

            private void InvokeIfActive()
            {
                if (IsActive) _callback();
            }
        }

        private sealed class VerboseRegistrationLogScope(HotkeyService owner) : IDisposable
        {
            private readonly HotkeyService _owner = owner;
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                lock (_owner._lock)
                {
                    if (_owner._verboseRegistrationLogSuppressionCount > 0)
                    {
                        _owner._verboseRegistrationLogSuppressionCount--;
                    }
                }
            }
        }

        public HotkeyService()
            : this(null, null)
        {
        }

        internal HotkeyService(HotkeyParsingService? hotkeyParser, HotkeyDispatchCoordinator? dispatchCoordinator)
            : this(hotkeyParser, dispatchCoordinator, null, null)
        {
        }

        internal HotkeyService(
            HotkeyParsingService? hotkeyParser,
            HotkeyDispatchCoordinator? dispatchCoordinator,
            Func<IntPtr, int, uint, uint, HotkeyOsRegistrationResult>? registerHotKeyInvoker = null,
            Action<IntPtr, int>? unregisterHotKeyInvoker = null,
            Func<IntPtr>? setKeyboardHookInvoker = null,
            Func<IntPtr>? setMouseHookInvoker = null,
            Func<Action<int, KeyboardHotkeyEvent>, IKeyboardHotkeyMessageHost>? keyboardMessageHostFactory = null,
            Func<Action<KeyboardHotkeyBindingSnapshot, KeyboardHotkeyEvent>, IKeyboardHotkeyCaptureHost>? keyboardCaptureHostFactory = null,
            Func<Action<MouseHotkeyBindingSnapshot, long>, IMouseHotkeyCaptureHost>? mouseCaptureHostFactory = null,
            HotkeyCaptureSession? captureSession = null,
            Action<Action>? scheduleWindowFallback = null,
            Func<nint>? getForegroundWindow = null)
        {
            _logger = Logger.Instance;
            _captureSession = captureSession ?? HotkeyCaptureSession.Shared;
            _ = hotkeyParser;
            _dispatchCoordinator = dispatchCoordinator ?? new HotkeyDispatchCoordinator(_logger);
            _scheduleWindowFallback = scheduleWindowFallback ?? ScheduleWindowFallback;
            _getForegroundWindow = getForegroundWindow ?? LowLevelKeyboardHotkeyThreadHost.GetForegroundWindow;
            _registerHotKeyInvoker = registerHotKeyInvoker ?? RegisterHotKeyCore;
            _unregisterHotKeyInvoker = unregisterHotKeyInvoker ?? UnregisterHotKeyCore;
            _keyboardMessageHost = (keyboardMessageHostFactory ?? (dispatch => new MessageOnlyKeyboardHotkeyHost(_logger, dispatch, _registerHotKeyInvoker, _unregisterHotKeyInvoker)))(DispatchKeyboardMessageHotkey);
            _keyboardCaptureHost = (keyboardCaptureHostFactory ?? (dispatch => new LowLevelKeyboardHotkeyThreadHost(_logger, dispatch, setKeyboardHookInvoker)))(DispatchKeyboardHotkeyMatch);
            _mouseCaptureHost = (mouseCaptureHostFactory ?? (dispatch => new LowLevelMouseHotkeyThreadHost(_logger, dispatch, setMouseHookInvoker)))(DispatchMouseHotkeyMatch);
            _captureSession.Changed += OnCaptureSessionChanged;
        }

        /// <summary>
        /// Initializes hotkey infrastructure without depending on a caller-owned window handle.
        /// </summary>
        internal void InitializeInfrastructure()
        {
            bool keyboardMessageHostInstalled = false;
            bool keyboardHookInstalled = false;
            bool mouseHookInstalled = false;
            bool cancelMicrophoneHold = false;
            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;

                cancelMicrophoneHold |= ActivateInitializedKeyboardRegistrationsUnderLock();
                PublishOrStopKeyboardMessageHostUnderLock();
                keyboardMessageHostInstalled = _keyboardMessageHost.IsRunning;
                keyboardHookInstalled = EnsureKeyboardHookInstalledIfNeededUnderLock();
                if (!keyboardHookInstalled)
                {
                    cancelMicrophoneHold |= DeactivateUnavailableFallbackKeyboardRegistrationsUnderLock();
                }
                mouseHookInstalled = EnsureMouseHookInstalledIfNeededUnderLock();
            }

            if (cancelMicrophoneHold)
            {
                OnMicrophoneHoldCancelled?.Invoke();
            }

            _logger.Info(
                "HotkeyService",
                () => $"hotkey-service-ready | mode={GetInitializationMode(keyboardMessageHostInstalled, keyboardHookInstalled, mouseHookInstalled)} messageHost={keyboardMessageHostInstalled} keyboardHook={keyboardHookInstalled} mouseHook={mouseHookInstalled}");
        }

        internal IDisposable SuppressVerboseRegistrationLogs()
        {
            lock (_lock)
            {
                _verboseRegistrationLogSuppressionCount++;
            }

            return new VerboseRegistrationLogScope(this);
        }

        public bool RegisterHotkey(List<Key> modifierKeys, Key mainKey)
        {
            string hotkey = HotkeyParsingService.BuildComboKey(modifierKeys, HotkeyMainInput.FromKeyboard(mainKey));
            bool registered = RegisterGenericHotkey(
                AppConstants.Hotkeys.HotkeyId,
                hotkey,
                null,
                () => OnHotkeyPressed?.Invoke(),
                "User Hotkey");
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        internal HotkeyRegistrationOutcome GetLastRegistrationOutcome(int id)
        {
            lock (_lock)
            {
                return _registrationOutcomeById.TryGetValue(id, out HotkeyRegistrationOutcome outcome)
                    ? outcome
                    : new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.None);
            }
        }

        internal HotkeyRuntimeSnapshot CaptureRuntimeSnapshot()
        {
            lock (_lock)
            {
                return new(_initialized, Volatile.Read(ref _disposeStarted) != 0, _captureSession.IsActive,
                    _hotkeys.Count, _keyboardMessageHost.IsRunning, _keyboardCaptureHost.IsRunning, _mouseCaptureHost.IsRunning,
                    [.. _registrationOutcomeById.OrderBy(static entry => entry.Key).Select(entry => new HotkeyRegistrationSnapshot(
                        entry.Key, entry.Value.Kind.ToString(), _registrationDeliveryById.GetValueOrDefault(entry.Key), entry.Value.Win32Error))]);
            }
        }

        public bool RegisterToggleAppVisibilityHotkey(string? toggleAppVisibilityHotkey)
        {
            bool registered = RegisterGenericHotkey(
                AppConstants.Hotkeys.ToggleAppVisibilityHotkeyId,
                toggleAppVisibilityHotkey,
                AppConstants.Hotkeys.DefaultToggleAppVisibility,
                () => OnToggleAppVisibilityHotkeyPressed?.Invoke(),
                "Show / Hide App",
                default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterShowAudioStatusHotkey(string? hotkey)
        {
            bool registered = RegisterGenericHotkey(
                AppConstants.Hotkeys.ShowAudioStatusHotkeyId,
                hotkey,
                null,
                () => OnShowAudioStatusPressed?.Invoke(),
                "Show Audio Status",
                default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterQuickDevicePickerHotkey(string? hotkey)
        {
            bool registered = RegisterGenericHotkey(
                AppConstants.Hotkeys.QuickDevicePickerHotkeyId,
                hotkey,
                null,
                () => OnQuickDevicePickerPressed?.Invoke(),
                "Quick Device Picker",
                default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }
        public bool RegisterVolumeMixerHotkey(string? hotkey)
        {
            bool registered = RegisterGenericHotkey(
                AppConstants.Hotkeys.VolumeMixerHotkeyId,
                hotkey,
                null,
                () => OnVolumeMixerPressed?.Invoke(),
                "Quick Mixer",
                default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterMediaHotkeys(string? showCurrent, string? playPause, string? next, string? prev, string? seekForward = null, string? seekBackward = null)
        {
            HotkeyRegistrationOptions options = new(AllowHookOnlyFallbackWhenOsRegistrationFails: true);
            bool showCurrentRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaShowCurrentTrackId, showCurrent, null, () => OnMediaShowCurrentTrackPressed?.Invoke(), "Show Current Track", options);
            bool playPauseRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaPlayPauseId, playPause, null, () => OnMediaPlayPausePressed?.Invoke(), "Play/Pause", options);
            bool nextRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaNextTrackId, next, null, () => OnMediaNextTrackPressed?.Invoke(), "Next Track", options);
            bool prevRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaPrevTrackId, prev, null, () => OnMediaPrevTrackPressed?.Invoke(), "Prev Track", options);
            bool forwardRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaSeekForwardId, seekForward, null, () => OnMediaSeekForwardPressed?.Invoke(), "Seek Forward", options);
            bool backwardRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaSeekBackwardId, seekBackward, null, () => OnMediaSeekBackwardPressed?.Invoke(), "Seek Backward", options);
            FinalizeKeyboardMessageHostLifecycle();

            LogRegistrationGroupDeliverySummary(
                "media",
                AppConstants.Hotkeys.MediaShowCurrentTrackId,
                AppConstants.Hotkeys.MediaPlayPauseId,
                AppConstants.Hotkeys.MediaNextTrackId,
                AppConstants.Hotkeys.MediaPrevTrackId,
                AppConstants.Hotkeys.MediaSeekForwardId,
                AppConstants.Hotkeys.MediaSeekBackwardId);

            return showCurrentRegistered && playPauseRegistered && nextRegistered && prevRegistered && forwardRegistered && backwardRegistered;
        }

        public bool RegisterMuteHotkeys(string? muteMic, string? muteSound, string? deafen, string? pushToTalk = "", string? holdToMute = "")
        {
            bool muteMicRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MuteMicId, muteMic, null, () => OnMuteMicPressed?.Invoke(), "Mute Mic", default);
            bool muteSoundRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MuteSoundId, muteSound, null, () => OnMuteSoundPressed?.Invoke(), "Mute Sound", default);
            bool deafenRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.DeafenId, deafen, null, () => OnDeafenPressed?.Invoke(), "Deafen", default);
            bool pushRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.PushToTalkHotkeyId, pushToTalk, null, static () => { }, "Push-to-talk", default);
            bool holdRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.HoldToMuteHotkeyId, holdToMute, null, static () => { }, "Hold-to-mute", default);
            FinalizeKeyboardMessageHostLifecycle();
            return muteMicRegistered && muteSoundRegistered && deafenRegistered && pushRegistered && holdRegistered;
        }

        public bool RegisterListenToInputHotkey(string? hotkey)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.ListenToInputHotkeyId, hotkey, null, () => OnListenToInputPressed?.Invoke(), "Listen To Input", default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterVolumeStepHotkeys(string? masterUp, string? masterDown, string? micUp, string? micDown, string? foregroundUp = "", string? foregroundDown = "", string? foregroundMute = "")
        {
            bool masterUpRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MasterVolumeUpHotkeyId, masterUp, null, () => OnMasterVolumeUpPressed?.Invoke(), "Master Volume Up", default);
            bool masterDownRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MasterVolumeDownHotkeyId, masterDown, null, () => OnMasterVolumeDownPressed?.Invoke(), "Master Volume Down", default);
            bool micUpRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MicVolumeUpHotkeyId, micUp, null, () => OnMicVolumeUpPressed?.Invoke(), "Mic Volume Up", default);
            bool micDownRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MicVolumeDownHotkeyId, micDown, null, () => OnMicVolumeDownPressed?.Invoke(), "Mic Volume Down", default);

            bool foregroundUpRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.ForegroundVolumeUpHotkeyId, foregroundUp, null, () => OnForegroundVolumeUpPressed?.Invoke(), "Foreground app volume up", default);
            bool foregroundDownRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.ForegroundVolumeDownHotkeyId, foregroundDown, null, () => OnForegroundVolumeDownPressed?.Invoke(), "Foreground app volume down", default);
            bool foregroundMuteRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.ForegroundMuteHotkeyId, foregroundMute, null, () => OnForegroundMutePressed?.Invoke(), "Foreground app mute", default);
            FinalizeKeyboardMessageHostLifecycle();
            return masterUpRegistered && masterDownRegistered && micUpRegistered && micDownRegistered && foregroundUpRegistered && foregroundDownRegistered && foregroundMuteRegistered;
        }

        public bool RegisterOutputSwitchHotkey(string? hotkey)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.OutputSwitchHotkeyId, hotkey, null, () => OnOutputSwitchHotkeyPressed?.Invoke(), "Output Switch", default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterInputSwitchHotkey(string? hotkey)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.InputSwitchHotkeyId, hotkey, null, () => OnInputSwitchHotkeyPressed?.Invoke(), "Input Switch", default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterOutputReverseSwitchHotkey(string? hotkey)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.OutputReverseSwitchHotkeyId, hotkey, null, () => OnOutputReverseSwitchHotkeyPressed?.Invoke(), "Output Reverse Switch", default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterInputReverseSwitchHotkey(string? hotkey)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.InputReverseSwitchHotkeyId, hotkey, null, () => OnInputReverseSwitchHotkeyPressed?.Invoke(), "Input Reverse Switch", default);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterDynamicHotkey(int id, string? hotkey, Action callback, string name)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);

            ArgumentNullException.ThrowIfNull(callback);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            bool registered = RegisterGenericHotkey(id, hotkey, null, callback, name,
                new HotkeyRegistrationOptions(AllowHookOnlyFallbackWhenOsRegistrationFails: true), replaceCallback: true);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public void UnregisterHotkey(int id)
        {
            if (id <= 0)
            {
                return;
            }

            bool cancelMicrophoneHold;
            lock (_lock)
            {
                cancelMicrophoneHold = RemoveHotkeyById(id);
            }

            if (cancelMicrophoneHold)
            {
                OnMicrophoneHoldCancelled?.Invoke();
            }
        }

        internal void ReleaseChangedHotkeys(IEnumerable<(int Id, string? Hotkey)> desiredBindings)
        {
            bool cancelMicrophoneHold = false;
            lock (_lock)
            {
                foreach (var (id, hotkey) in desiredBindings)
                {
                    HotkeyDefinition? current = _hotkeys.Find(binding => binding.Id == id);
                    if (current != null && !MatchesShortcut(current, HotkeyParsingService.ParseHotkeyString(hotkey ?? string.Empty)))
                        cancelMicrophoneHold |= RemoveHotkeyById(id);
                }
            }

            if (cancelMicrophoneHold)
            {
                OnMicrophoneHoldCancelled?.Invoke();
            }
        }

        public void UnregisterAllHotkeys()
        {
            lock (_lock)
            {
                foreach (HotkeyDefinition definition in _hotkeys)
                {
                    definition.IsActive = false;
                    definition.HoldState?.Release();
                }
                if (_keyboardMessageHost.IsRunning)
                {
                    foreach (var hk in _hotkeys)
                    {
                        if (!hk.UseOsRegistration)
                        {
                            continue;
                        }

                        try { _keyboardMessageHost.UnregisterHotkey(hk.Id); }
                        catch (Exception ex)
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.Trace("HotkeyService", () => $"hotkey-unregister-ignored | id={hk.Id} description={LogPrivacy.Label(hk.Description)} reason={ex.GetType().Name}");
                            }
                        }
                    }
                }

                _hotkeys.Clear();
                Volatile.Write(ref _keyboardMessageBindings, []);
                _registeredCombos.Clear();
                _registrationDeliveryById.Clear();
                _registrationOutcomeById.Clear();
                _dispatchCoordinator.Reset();
                ReleaseKeyboardMessageHostIfUnusedUnderLock();
                ReleaseKeyboardHookIfUnusedUnderLock();
                ReleaseMouseHookIfUnusedUnderLock();
                _logger.Info("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.Unregister} | scope=all");
            }

            OnMicrophoneHoldCancelled?.Invoke();
        }

        /// <summary>
        /// Releases installed hooks and unregisters hotkeys.
        /// </summary>
        /// <remarks>
        /// Disposal is idempotent and safe to call during shutdown races.
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _captureSession.Changed -= OnCaptureSessionChanged;
            _logger.Info("HotkeyService", "hotkey-service-dispose-start");

            lock (_lock)
            {
                foreach (HotkeyDefinition definition in _hotkeys)
                {
                    definition.IsActive = false;
                    definition.HoldState?.Release();
                }
            }

            bool keyboardMessageHostWasRunning = _keyboardMessageHost.IsRunning;
            TryDisposeHost(_keyboardMessageHost, "keyboard-message-host");
            if (keyboardMessageHostWasRunning)
            {
                _logger.Info("HotkeyService", () => "keyboard-message-host-uninstalled | type=WM_HOTKEY reason=dispose");
            }

            bool keyboardHookWasRunning = _keyboardCaptureHost.IsRunning;
            TryDisposeHost(_keyboardCaptureHost, "keyboard-hook-host");
            if (keyboardHookWasRunning)
            {
                _logger.Info("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.KeyboardHookUninstalled} | type=WH_KEYBOARD_LL");
            }

            bool mouseHookWasRunning = _mouseCaptureHost.IsRunning;
            TryDisposeHost(_mouseCaptureHost, "mouse-hook-host");
            if (mouseHookWasRunning)
            {
                _logger.Info("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.MouseHookUninstalled} | type=WH_MOUSE_LL");
            }

            lock (_lock)
            {
                _initialized = false;
                _hotkeys.Clear();
                Volatile.Write(ref _keyboardMessageBindings, []);
                _registeredCombos.Clear();
                _registrationDeliveryById.Clear();
                _registrationOutcomeById.Clear();
                _dispatchCoordinator.Reset();
                HotkeyParsingService.ClearCache();
            }

            GC.SuppressFinalize(this);
        }

        private void DispatchKeyboardMessageHotkey(int hotkeyId, KeyboardHotkeyEvent hotkeyEvent)
        {
            long generation = _captureSession.Generation;
            if (!CanDispatch(generation))
            {
                return;
            }

            foreach (HotkeyDefinition matched in Volatile.Read(ref _keyboardMessageBindings))
            {
                if (matched.Id != hotkeyId || !matched.IsActive) continue;
                if (matched.WindowDelivery is { } delivery)
                {
                    if (!delivery.ClaimNative(hotkeyEvent.MessageTime, CurrentMessageTime, generation)) return;
                    _logger.Debug("HotkeyService", () => $"window-shortcut-dispatch | id={hotkeyId} delivery=native");
                }
                _dispatchCoordinator.ExecuteCallback(matched.Id, matched.Description, PrepareDispatchCallback(matched.Id, matched.Callback, generation),
                    hotkeyEvent.Timestamp, debounce: matched.WindowDelivery == null);
                return;
            }
        }

        private void DispatchKeyboardHotkeyMatch(KeyboardHotkeyBindingSnapshot binding, KeyboardHotkeyEvent hotkeyEvent)
        {
            long generation = _captureSession.Generation;
            if (!CanDispatch(generation))
            {
                return;
            }

            if (binding.UseOsDelivery)
            {
                ObserveWindowShortcut(binding, hotkeyEvent, generation);
                return;
            }
            _dispatchCoordinator.ExecuteCallback(binding.Id, binding.Description, PrepareDispatchCallback(binding.Id, binding.Callback, generation), hotkeyEvent.Timestamp);
        }

        private static uint CurrentMessageTime => unchecked((uint)Environment.TickCount);

        private void ObserveWindowShortcut(KeyboardHotkeyBindingSnapshot binding, KeyboardHotkeyEvent hotkeyEvent, long generation)
        {
            nint foreground = hotkeyEvent.FallbackForeground;
            foreach (HotkeyDefinition registered in Volatile.Read(ref _keyboardMessageBindings))
            {
                if (registered.Id != binding.Id || !ReferenceEquals(registered.Callback, binding.Callback) || !registered.IsActive) continue;
                if (registered.WindowDelivery is not { } delivery) return;
                if (hotkeyEvent.ReleasedPressTime is uint releasedPressTime)
                {
                    delivery.ObserveRelease(releasedPressTime, hotkeyEvent.MessageTime, generation);
                    return;
                }
                WindowHotkeyDelivery.Press? press = delivery.ObserveHook(hotkeyEvent.MessageTime, CurrentMessageTime, generation);
                if (press == null || foreground == 0) return;
                _scheduleWindowFallback(() =>
                {
                    if (!registered.IsActive || !CanDispatchWindowShortcut(generation, foreground) || !delivery.ClaimFallback(press, CurrentMessageTime, generation)) return;
                    _logger.Debug("HotkeyService", () => $"window-shortcut-dispatch | id={binding.Id} delivery=hook-fallback");
                    // Waiting for native delivery is intentional, so keep this delay out of the hook latency metric.
                    _dispatchCoordinator.ExecuteCallback(binding.Id, binding.Description,
                        PrepareDispatchCallback(binding.Id, binding.Callback, generation, fallbackForeground: foreground), debounce: false);
                });
                return;
            }
        }

        private void ScheduleWindowFallback(Action dispatch) => _ = DispatchWindowFallbackAsync(dispatch);

        private async Task DispatchWindowFallbackAsync(Action dispatch)
        {
            await Task.Delay(WindowHotkeyDelivery.NativeWaitMs).ConfigureAwait(false);
            try { dispatch(); }
            catch (Exception ex) { _logger.Error("HotkeyService", "window-shortcut-fallback-failed", nameof(DispatchWindowFallbackAsync), ex); }
        }

        private void DispatchMouseHotkeyMatch(MouseHotkeyBindingSnapshot binding, long enqueueTimestamp)
        {
            long generation = _captureSession.Generation;
            if (!CanDispatch(generation))
            {
                return;
            }

            HotkeyDispatchCoordinator.WheelVolumeBatch? wheelBatch = null;
            if (binding.WheelDelta != 0 && IsVolumeStepHotkey(binding.Id))
            {
                const int wheelDeltaPerStep = 120;
                int steps;
                lock (_wheelVolumeLock)
                {
                    if (!ReferenceEquals(_wheelVolumeBinding, binding.Callback) || _wheelVolumeGeneration != generation
                        || Stopwatch.GetElapsedTime(_wheelVolumeTimestamp, enqueueTimestamp) > TimeSpan.FromMilliseconds(250))
                        _wheelVolumeRemainder = 0;
                    _wheelVolumeBinding = binding.Callback;
                    _wheelVolumeGeneration = generation;
                    _wheelVolumeTimestamp = enqueueTimestamp;
                    int delta = _wheelVolumeRemainder + Math.Abs(binding.WheelDelta);
                    steps = delta / wheelDeltaPerStep;
                    _wheelVolumeRemainder = delta % wheelDeltaPerStep;
                }
                if (steps == 0) return;
                // Foreground steps retain their original target and merge later by verified process identity.
                Action? mergeKey = binding.Id is AppConstants.Hotkeys.ForegroundVolumeUpHotkeyId or AppConstants.Hotkeys.ForegroundVolumeDownHotkeyId
                    ? null : binding.Callback;
                wheelBatch = new(Math.Min(steps, 100), mergeKey, generation);
            }
            else if (binding.WheelDelta != 0)
            {
                lock (_wheelVolumeLock) _wheelVolumeRemainder = 0;
            }
            _dispatchCoordinator.ExecuteCallback(binding.Id, binding.Description,
                PrepareDispatchCallback(binding.Id, binding.Callback, generation, wheelBatch), enqueueTimestamp, wheelBatch);
        }

        private Action PrepareDispatchCallback(int id, Action? callback, long generation, HotkeyDispatchCoordinator.WheelVolumeBatch? wheelBatch = null,
            nint fallbackForeground = 0)
        {
            Action? dispatch = id is AppConstants.Hotkeys.ForegroundVolumeUpHotkeyId or AppConstants.Hotkeys.ForegroundVolumeDownHotkeyId or AppConstants.Hotkeys.ForegroundMuteHotkeyId
                ? ForegroundAudioControlService.CaptureDispatch(callback) : callback;
            return () =>
            {
                if (!CanDispatchWindowShortcut(generation, fallbackForeground)) return;
                var previous = s_dispatchCapture;
                s_dispatchCapture = (this, generation, wheelBatch?.Steps ?? 1, fallbackForeground);
                try { dispatch?.Invoke(); }
                finally { s_dispatchCapture = previous; }
            };
        }

        private void OnCaptureSessionChanged()
        {
            bool registrationLost = false;
            bool registrationCompleted = false;
            bool cancelMicrophoneHold = false;
            lock (_lock)
            {
                if (Volatile.Read(ref _disposeStarted) != 0) return;
                _logger.Debug("HotkeyService", () => $"hotkey-capture-state | suspended={_captureSession.IsActive} registeredCount={_hotkeys.Count}");
                _dispatchCoordinator.Reset();
                if (_captureSession.IsActive)
                {
                    foreach (HotkeyDefinition hotkey in _hotkeys)
                    {
                        hotkey.HoldState?.Release();
                        if (!hotkey.UseOsRegistration || !_keyboardMessageHost.IsRunning) continue;
                        try { _keyboardMessageHost.UnregisterHotkey(hotkey.Id); }
                        catch (Exception ex)
                        {
                            _logger.Warning("HotkeyService", () => $"hotkey-capture-unregister-failed | id={hotkey.Id} error={ex.GetType().Name}");
                        }
                    }
                    cancelMicrophoneHold = true;
                }
                else if (_initialized)
                {
                    int previousCount = _hotkeys.Count;
                    cancelMicrophoneHold |= ActivateInitializedKeyboardRegistrationsUnderLock();
                    if (!EnsureKeyboardHookInstalledIfNeededUnderLock())
                    {
                        cancelMicrophoneHold |= DeactivateUnavailableFallbackKeyboardRegistrationsUnderLock();
                    }
                    registrationLost = _hotkeys.Count < previousCount;
                    registrationCompleted = true;
                }
            }
            if (cancelMicrophoneHold) OnMicrophoneHoldCancelled?.Invoke();
            if (registrationCompleted) CaptureRegistrationCompleted?.Invoke();
            if (registrationLost) CaptureRegistrationFailed?.Invoke();
        }

        /// <summary>
        /// Registers a logical hotkey action, replacing any existing registration for the same id.
        /// </summary>
        private bool RegisterGenericHotkey(int id, string? hotkeyString, string? defaultKey, Action callback, string name,
            HotkeyRegistrationOptions options = default, bool replaceCallback = false)
        {
            bool cancelMicrophoneHold = false;
            bool registered;
            try
            {
                registered = RegisterGenericHotkeyUnderLock(id, hotkeyString, defaultKey, callback, name, options, replaceCallback, out cancelMicrophoneHold);
            }
            finally
            {
                if (cancelMicrophoneHold)
                {
                    OnMicrophoneHoldCancelled?.Invoke();
                }
            }

            return registered;
        }

        private bool RegisterGenericHotkeyUnderLock(int id, string? hotkeyString, string? defaultKey, Action callback, string name,
            HotkeyRegistrationOptions options, bool replaceCallback, out bool cancelMicrophoneHold)
        {
            cancelMicrophoneHold = false;
            lock (_lock)
            {
                if (Volatile.Read(ref _disposeStarted) != 0)
                {
                    _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Unsupported, "service-disposed");
                    return false;
                }

                string? finalString = hotkeyString;
                if (string.IsNullOrEmpty(finalString))
                {
                    if (finalString == null && defaultKey != null)
                    {
                        finalString = defaultKey;
                        if (ShouldLogVerboseRegistrationEventsUnderLock())
                        {
                            _logger.Debug("HotkeyService", () => $"hotkey-default-applied | id={id} hotkey={LogPrivacy.Label(finalString)}");
                        }
                    }
                    else
                    {
                        cancelMicrophoneHold |= RemoveHotkeyById(id);
                        _registrationOutcomeById.Remove(id);
                        return true;
                    }
                }

                var parsed = HotkeyParsingService.ParseHotkeyString(finalString);
                if (!replaceCallback && _hotkeys.Find(binding => binding.Id == id) is { } current && MatchesShortcut(current, parsed))
                    return true;

                cancelMicrophoneHold |= RemoveHotkeyById(id);
                if (parsed.HasValue)
                {
                    if (id is AppConstants.Hotkeys.PushToTalkHotkeyId or AppConstants.Hotkeys.HoldToMuteHotkeyId &&
                        !MicrophoneHoldService.IsHoldable(parsed.Value.mainInput))
                    {
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Unsupported, "hold-requires-sustained-input");
                        return false;
                    }
                    if (HotkeyReservedShortcutPolicy.IsReserved(parsed.Value.mainInput, parsed.Value.modifiers, out string reservedShortcutName))
                    {
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Reserved, reservedShortcutName);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=reserved reserved={reservedShortcutName} hotkey={LogPrivacy.Label(finalString)}");
                        return false;
                    }

                    if (!parsed.Value.mainInput.IsSupportedModifierCount(parsed.Value.modifiers.Count))
                    {
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Unsupported);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=unsupported hotkey={LogPrivacy.Label(finalString)}");
                        return false;
                    }

                    string combo = HotkeyParsingService.BuildComboKey(parsed.Value.modifiers, parsed.Value.mainInput);
                    if (_registeredCombos.Contains(combo))
                    {
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Duplicate);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-skipped | id={id} reason=duplicate hotkey={LogPrivacy.Label(combo)}");
                        return false;
                    }

                    HotkeyHeldInputState? holdState = id is AppConstants.Hotkeys.PushToTalkHotkeyId or AppConstants.Hotkeys.HoldToMuteHotkeyId
                        ? new(HotkeyModifierHelper.GetModifierMaskFromKeys(parsed.Value.modifiers)) : null;
                    var definition = new HotkeyDefinition(callback)
                    {
                        Id = id,
                        MainInput = parsed.Value.mainInput,
                        Modifiers = parsed.Value.modifiers,
                        Combo = combo,
                        UseOsRegistration = ShouldUseOsRegistration(parsed.Value.mainInput),
                        AllowHookOnlyFallbackWhenOsRegistrationFails = options.AllowHookOnlyFallbackWhenOsRegistrationFails,
                        Description = $"{name} ({finalString})",
                        HoldState = holdState,
                        WindowDelivery = IsWindowShortcut(id) && parsed.Value.mainInput.CanUseOsRegistration ? new() : null,
                    };
                    holdState?.OnPressed = held =>
                        {
                            if (definition.IsActive)
                                OnMicrophoneHoldPressed?.Invoke(id == AppConstants.Hotkeys.HoldToMuteHotkeyId, held);
                        };

                    _hotkeys.Add(definition);
                    _registeredCombos.Add(combo);
                    HotkeyOsRegistrationResult osRegistration = definition.UseOsRegistration
                        ? RegisterOsHotkey(definition)
                        : new HotkeyOsRegistrationResult(Succeeded: true);
                    if (definition.UseOsRegistration && !osRegistration.Succeeded && !options.AllowHookOnlyFallbackWhenOsRegistrationFails)
                    {
                        definition.IsActive = false;
                        _hotkeys.Remove(definition);
                        _registeredCombos.Remove(combo);
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.ExternalConflict, Win32Error: osRegistration.ErrorCode);
                        LogExternalConflictRegistration(id, name, osRegistration.ErrorCode);

                        return false;
                    }

                    if (definition.UseOsRegistration && !osRegistration.Succeeded)
                    {
                        _logger.Warning("HotkeyService", () => $"hotkey-register-hook-fallback | id={id} description={LogPrivacy.Label(definition.Description)} win32={osRegistration.ErrorCode}");
                    }

                    string delivery = !definition.UseOsRegistration
                        ? "hook-only"
                        : osRegistration.Succeeded ? "hybrid" : "hook-only-fallback";
                    _registrationDeliveryById[id] = delivery;

                    if (holdState != null && definition.MainInput.CanUseOsRegistration && !EnsureKeyboardHookInstalledIfNeededUnderLock())
                    {
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.KeyboardHookUnavailable);
                        cancelMicrophoneHold |= RemoveHotkeyById(id, preserveOutcome: true);
                        return false;
                    }

                    if (RequiresKeyboardFallback(definition, osRegistration) && !EnsureKeyboardHookInstalledIfNeededUnderLock())
                    {
                        definition.IsActive = false;
                        _hotkeys.Remove(definition);
                        _registeredCombos.Remove(combo);
                        _registrationDeliveryById.Remove(id);
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.KeyboardHookUnavailable);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=keyboard-hook-unavailable hotkey={LogPrivacy.Label(finalString)}");
                        return false;
                    }

                    if (!definition.UseOsRegistration && definition.MainInput.CanUseOsRegistration && !EnsureKeyboardHookInstalledIfNeededUnderLock())
                    {
                        definition.IsActive = false;
                        _hotkeys.Remove(definition);
                        _registeredCombos.Remove(combo);
                        _registrationDeliveryById.Remove(id);
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.KeyboardHookUnavailable);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=keyboard-hook-unavailable hotkey={LogPrivacy.Label(finalString)}");
                        return false;
                    }

                    if (definition.UseOsRegistration && osRegistration.Succeeded && !EnsureKeyboardHookInstalledIfNeededUnderLock())
                    {
                        _logger.Warning("HotkeyService", () => $"keyboard-hook-compatibility-unavailable | id={id} description={LogPrivacy.Label(definition.Description)} delivery=hybrid");
                    }

                    if (definition.MainInput.IsMouseInput && !EnsureMouseHookInstalledIfNeededUnderLock())
                    {
                        definition.IsActive = false;
                        _hotkeys.Remove(definition);
                        _registeredCombos.Remove(combo);
                        _registrationDeliveryById.Remove(id);
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.MouseHookUnavailable);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=mouse-hook-unavailable hotkey={LogPrivacy.Label(finalString)}");
                        return false;
                    }

                    _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(
                        !definition.UseOsRegistration || osRegistration.Succeeded
                            ? HotkeyRegistrationOutcomeKind.Registered
                            : HotkeyRegistrationOutcomeKind.Fallback);
                    if (ShouldLogVerboseRegistrationEventsUnderLock())
                    {
                        _logger.Debug("HotkeyService", () => $"hotkey-register-success | id={id} hotkey={LogPrivacy.Label(finalString)} delivery={delivery}");
                    }

                    return true;
                }
                else
                {
                    _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.ParseError);
                    _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=parse hotkey={LogPrivacy.Label(finalString)}");
                    return false;
                }
            }
        }

        private static bool MatchesShortcut(HotkeyDefinition current, (HotkeyMainInput mainInput, List<Key> modifiers)? parsed) =>
            parsed is { } shortcut &&
            string.Equals(current.Combo,
                HotkeyParsingService.BuildComboKey(shortcut.modifiers, shortcut.mainInput), StringComparison.OrdinalIgnoreCase);

        private HotkeyOsRegistrationResult RegisterOsHotkey(HotkeyDefinition hotkey)
        {
            if (!hotkey.UseOsRegistration)
            {
                return new HotkeyOsRegistrationResult(Succeeded: false);
            }

            if (!_initialized)
            {
                return new HotkeyOsRegistrationResult(Succeeded: true);
            }

            if (!EnsureKeyboardMessageHostStartedIfNeededUnderLock())
            {
                _logger.Warning("HotkeyService", () => $"keyboard-message-host-unavailable | id={hotkey.Id} description={LogPrivacy.Label(hotkey.Description)}");
                return new HotkeyOsRegistrationResult(Succeeded: false);
            }

            uint fsModifiers = AllowsKeyboardRepeat(hotkey.Id) ? 0 : MOD_NOREPEAT;
            foreach (var mod in hotkey.Modifiers)
            {
                fsModifiers |= mod switch
                {
                    Key.LeftCtrl or Key.RightCtrl => MOD_CONTROL,
                    Key.LeftAlt or Key.RightAlt => MOD_ALT,
                    Key.LeftShift or Key.RightShift => MOD_SHIFT,
                    Key.LWin or Key.RWin => MOD_WIN,
                    _ => 0
                };
            }

            uint vk = hotkey.MainInput.KeyboardVirtualKey;

            try
            {
                HotkeyOsRegistrationResult result = _keyboardMessageHost.RegisterHotkey(hotkey.Id, fsModifiers, vk);
                if (!result.Succeeded)
                {
                    _logger.Warning("HotkeyService", () => $"hotkey-register-os-failed | id={hotkey.Id} description={LogPrivacy.Label(hotkey.Description)} win32={result.ErrorCode} reason=in-use-or-unavailable");
                    return result;
                }

                if (_captureSession.IsActive) _keyboardMessageHost.UnregisterHotkey(hotkey.Id);
                return result;
            }
            catch (Exception ex)
            {
                _logger.Warning("HotkeyService", () => $"hotkey-register-os-failed | id={hotkey.Id} description={LogPrivacy.Label(hotkey.Description)} reason={ex.GetType().Name}");
                return new HotkeyOsRegistrationResult(Succeeded: false);
            }
        }

        private bool ActivateInitializedKeyboardRegistrationsUnderLock()
        {
            List<int> registrationsToRemove = [];
            foreach (HotkeyDefinition hotkey in _hotkeys)
            {
                if (!hotkey.UseOsRegistration)
                {
                    continue;
                }

                HotkeyOsRegistrationResult osRegistration = RegisterOsHotkey(hotkey);
                if (!osRegistration.Succeeded && !hotkey.AllowHookOnlyFallbackWhenOsRegistrationFails)
                {
                    _registrationDeliveryById.Remove(hotkey.Id);
                    _registrationOutcomeById[hotkey.Id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.ExternalConflict, Win32Error: osRegistration.ErrorCode);
                    LogExternalConflictRegistration(hotkey.Id, hotkey.Description, osRegistration.ErrorCode);
                    registrationsToRemove.Add(hotkey.Id);
                    continue;
                }

                if (!osRegistration.Succeeded)
                {
                    _logger.Warning("HotkeyService", () => $"hotkey-register-hook-fallback | id={hotkey.Id} description={LogPrivacy.Label(hotkey.Description)} win32={osRegistration.ErrorCode}");
                }

                _registrationDeliveryById[hotkey.Id] = osRegistration.Succeeded ? "hybrid" : "hook-only-fallback";
                _registrationOutcomeById[hotkey.Id] = new HotkeyRegistrationOutcome(
                    osRegistration.Succeeded
                        ? HotkeyRegistrationOutcomeKind.Registered
                        : HotkeyRegistrationOutcomeKind.Fallback);
            }

            PublishOrStopKeyboardMessageHostUnderLock();

            bool cancelMicrophoneHold = false;
            foreach (int id in registrationsToRemove)
            {
                cancelMicrophoneHold |= RemoveHotkeyById(id, preserveOutcome: true);
            }

            return cancelMicrophoneHold;
        }

        private bool DeactivateUnavailableFallbackKeyboardRegistrationsUnderLock()
        {
            List<int> registrationsToRemove = [];
            foreach (HotkeyDefinition hotkey in _hotkeys)
            {
                if (!hotkey.MainInput.CanUseOsRegistration ||
                    !_registrationDeliveryById.TryGetValue(hotkey.Id, out string? delivery) ||
                    (hotkey.HoldState == null && string.Equals(delivery, "hybrid", StringComparison.Ordinal)))
                {
                    continue;
                }

                _registrationOutcomeById[hotkey.Id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.KeyboardHookUnavailable);
                registrationsToRemove.Add(hotkey.Id);
            }

            bool cancelMicrophoneHold = false;
            foreach (int id in registrationsToRemove)
            {
                cancelMicrophoneHold |= RemoveHotkeyById(id, preserveOutcome: true);
            }

            return cancelMicrophoneHold;
        }

        private bool RemoveHotkeyById(int id, bool preserveOutcome = false)
        {
            if (!preserveOutcome) _registrationOutcomeById.Remove(id);
            int index = _hotkeys.FindIndex(binding => binding.Id == id);
            if (index < 0) return false;
            var hotkey = _hotkeys[index];
            hotkey.IsActive = false;
            hotkey.HoldState?.Release();
            bool cancelMicrophoneHold = id is AppConstants.Hotkeys.PushToTalkHotkeyId or AppConstants.Hotkeys.HoldToMuteHotkeyId;

            if (hotkey.UseOsRegistration && _keyboardMessageHost.IsRunning)
            {
                try
                {
                    _keyboardMessageHost.UnregisterHotkey(id);
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.Trace("HotkeyService", () => $"Ignored unregister failure for hotkey id={id}: {ex.GetType().Name}");
                }
            }

            _registeredCombos.Remove(hotkey.Combo);
            _registrationDeliveryById.Remove(id);
            _hotkeys.RemoveAt(index);
            PublishOrStopKeyboardMessageHostUnderLock();
            PublishOrStopKeyboardCaptureUnderLock();
            PublishOrStopMouseCaptureUnderLock();
            return cancelMicrophoneHold;
        }

        private bool EnsureKeyboardHookInstalledIfNeededUnderLock()
        {
            KeyboardHotkeySnapshot snapshot = BuildKeyboardHotkeySnapshotUnderLock();
            if (!snapshot.HasBindings)
            {
                return false;
            }

            if (!_initialized)
            {
                return true;
            }

            if (_keyboardCaptureHost.IsRunning)
            {
                _keyboardCaptureHost.UpdateSnapshot(snapshot);
                return true;
            }

            if (_keyboardCaptureHost.TryStart(snapshot))
            {
                if (ShouldLogVerboseRegistrationEventsUnderLock())
                {
                    _logger.Info("HotkeyService", () => "keyboard-hook-installed | type=WH_KEYBOARD_LL reason=registered-hook-delivered-keyboard-hotkeys");
                }

                return true;
            }

            return false;
        }

        private bool EnsureMouseHookInstalledIfNeededUnderLock()
        {
            MouseHotkeySnapshot snapshot = BuildMouseHotkeySnapshotUnderLock();
            if (!snapshot.HasBindings)
            {
                return false;
            }

            if (!_initialized)
            {
                return true;
            }

            if (_mouseCaptureHost.IsRunning)
            {
                _mouseCaptureHost.UpdateSnapshot(snapshot);
                return true;
            }

            if (_mouseCaptureHost.TryStart(snapshot))
            {
                _logger.Info("HotkeyService", () => "mouse-hook-installed | type=WH_MOUSE_LL reason=registered-mouse-hotkeys");
                return true;
            }

            return false;
        }

        private void FinalizeKeyboardMessageHostLifecycle()
        {
            lock (_lock)
            {
                PublishOrStopKeyboardMessageHostUnderLock();
            }
        }

        private bool EnsureKeyboardMessageHostStartedIfNeededUnderLock()
        {
            if (!HasRegisteredKeyboardHotkeysUnderLock())
            {
                return false;
            }

            if (!_initialized)
            {
                return true;
            }

            if (_keyboardMessageHost.IsRunning)
            {
                return true;
            }

            if (_keyboardMessageHost.TryStart())
            {
                if (ShouldLogVerboseRegistrationEventsUnderLock())
                {
                    _logger.Info("HotkeyService", () => "keyboard-message-host-installed | type=WM_HOTKEY reason=registered-os-keyboard-hotkeys");
                }

                return true;
            }

            _logger.Warning("HotkeyService", () => "keyboard-message-host-unavailable | reason=start-failed");
            return false;
        }

        private void ReleaseKeyboardMessageHostIfUnusedUnderLock()
        {
            if (_keyboardMessageHost.IsRunning && !HasActiveOsRegisteredKeyboardHotkeysUnderLock())
            {
                _keyboardMessageHost.Stop();
                _logger.Info("HotkeyService", () => "keyboard-message-host-uninstalled | type=WM_HOTKEY reason=no-active-os-registered-keyboard-hotkeys");
            }
        }

        private void ReleaseKeyboardHookIfUnusedUnderLock()
        {
            if (_keyboardCaptureHost.IsRunning && !HasRegisteredKeyboardHotkeysUnderLock())
            {
                _keyboardCaptureHost.Stop();
                _logger.Info("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.KeyboardHookUninstalled} | type=WH_KEYBOARD_LL reason=no-registered-keyboard-hotkeys");
            }
        }

        private void ReleaseMouseHookIfUnusedUnderLock()
        {
            if (_mouseCaptureHost.IsRunning && !HasRegisteredMouseHotkeysUnderLock())
            {
                _mouseCaptureHost.Stop();
                _logger.Info("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.MouseHookUninstalled} | type=WH_MOUSE_LL reason=no-registered-mouse-hotkeys");
            }
        }

        private bool HasRegisteredKeyboardHotkeysUnderLock()
        {
            foreach (HotkeyDefinition hotkey in _hotkeys)
            {
                if (hotkey.MainInput.CanUseOsRegistration)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasActiveOsRegisteredKeyboardHotkeysUnderLock()
        {
            foreach (HotkeyDefinition hotkey in _hotkeys)
            {
                if (!hotkey.UseOsRegistration)
                {
                    continue;
                }

                if (_registrationDeliveryById.TryGetValue(hotkey.Id, out string? delivery) &&
                    string.Equals(delivery, "hybrid", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasRegisteredMouseHotkeysUnderLock()
        {
            foreach (HotkeyDefinition hotkey in _hotkeys)
            {
                if (hotkey.MainInput.IsMouseInput)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Leaves OS-registered window shortcuts on WM_HOTKEY for normal foreground activation.
        /// The hook observes those presses without consuming their input.
        /// </summary>
        private KeyboardHotkeySnapshot BuildKeyboardHotkeySnapshotUnderLock()
        {
            List<KeyboardHotkeyBindingSnapshot> bindings = [];
            foreach (HotkeyDefinition hotkey in _hotkeys)
            {
                if (!hotkey.MainInput.CanUseOsRegistration)
                {
                    continue;
                }

                bindings.Add(
                    new KeyboardHotkeyBindingSnapshot(
                        hotkey.Id,
                        hotkey.MainInput,
                        HotkeyModifierHelper.GetModifierMaskFromKeys(hotkey.Modifiers),
                        hotkey.Callback,
                        hotkey.Description,
                        UseOsDelivery: hotkey.WindowDelivery != null
                            && _registrationDeliveryById.TryGetValue(hotkey.Id, out string? delivery)
                            && string.Equals(delivery, "hybrid", StringComparison.Ordinal),
                        HoldState: hotkey.HoldState,
                        AllowRepeat: AllowsKeyboardRepeat(hotkey.Id)));
            }

            return KeyboardHotkeySnapshot.Create(bindings, _captureSession);
        }

        private static bool IsWindowShortcut(int id) => id is AppConstants.Hotkeys.ToggleAppVisibilityHotkeyId
            or AppConstants.Hotkeys.QuickDevicePickerHotkeyId or AppConstants.Hotkeys.VolumeMixerHotkeyId;

        private MouseHotkeySnapshot BuildMouseHotkeySnapshotUnderLock()
        {
            List<MouseHotkeyBindingSnapshot> bindings = [];
            foreach (HotkeyDefinition hotkey in _hotkeys)
            {
                if (!hotkey.MainInput.IsMouseInput)
                {
                    continue;
                }

                bindings.Add(
                    new MouseHotkeyBindingSnapshot(
                        hotkey.Id,
                        hotkey.MainInput,
                        HotkeyModifierHelper.GetModifierMaskFromKeys(hotkey.Modifiers),
                        hotkey.Callback,
                        hotkey.Description,
                        hotkey.HoldState));
            }

            return MouseHotkeySnapshot.Create(bindings, _captureSession);
        }

        private void PublishOrStopKeyboardCaptureUnderLock()
        {
            if (HasRegisteredKeyboardHotkeysUnderLock())
            {
                if (_initialized)
                {
                    _keyboardCaptureHost.UpdateSnapshot(BuildKeyboardHotkeySnapshotUnderLock());
                }

                return;
            }

            ReleaseKeyboardHookIfUnusedUnderLock();
        }

        private void PublishOrStopKeyboardMessageHostUnderLock()
        {
            // The message thread cannot take _lock while registration waits for its synchronous reply.
            Volatile.Write(ref _keyboardMessageBindings, [.. _hotkeys.Where(h => h.IsActive && h.UseOsRegistration &&
                _registrationDeliveryById.GetValueOrDefault(h.Id) == "hybrid")]);
            if (HasActiveOsRegisteredKeyboardHotkeysUnderLock())
            {
                return;
            }

            if (_initialized &&
                HasRegisteredKeyboardHotkeysUnderLock() &&
                !_keyboardMessageHost.IsRunning)
            {
                _logger.Info("HotkeyService", () => "keyboard-message-host-skipped | reason=no-active-os-registered-keyboard-hotkeys");
            }

            ReleaseKeyboardMessageHostIfUnusedUnderLock();
        }

        private void PublishOrStopMouseCaptureUnderLock()
        {
            if (HasRegisteredMouseHotkeysUnderLock())
            {
                if (_initialized)
                {
                    _mouseCaptureHost.UpdateSnapshot(BuildMouseHotkeySnapshotUnderLock());
                }

                return;
            }

            ReleaseMouseHookIfUnusedUnderLock();
        }

        private static bool RequiresKeyboardFallback(HotkeyDefinition definition, HotkeyOsRegistrationResult osRegistration)
        {
            return definition.UseOsRegistration && !osRegistration.Succeeded;
        }

        private static bool IsVolumeStepHotkey(int id) => id is
            AppConstants.Hotkeys.MasterVolumeUpHotkeyId or AppConstants.Hotkeys.MasterVolumeDownHotkeyId or
            AppConstants.Hotkeys.MicVolumeUpHotkeyId or AppConstants.Hotkeys.MicVolumeDownHotkeyId or
            AppConstants.Hotkeys.ForegroundVolumeUpHotkeyId or AppConstants.Hotkeys.ForegroundVolumeDownHotkeyId;

        private static bool AllowsKeyboardRepeat(int id) => IsVolumeStepHotkey(id) || id is
            AppConstants.Hotkeys.MediaSeekForwardId or AppConstants.Hotkeys.MediaSeekBackwardId;

        private static bool ShouldUseOsRegistration(HotkeyMainInput input)
        {
            if (!input.CanUseOsRegistration)
            {
                return false;
            }

            return input.Key is not (
                Key.MediaPlayPause or
                Key.MediaNextTrack or
                Key.MediaPreviousTrack);
        }

        private void TryDisposeHost(IDisposable host, string hostKind)
        {
            try
            {
                host.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "HotkeyService",
                    () => $"hotkey-host-dispose-failed | host={hostKind} error={ex.GetType().Name}");
            }
        }

        private bool ShouldLogVerboseRegistrationEventsUnderLock()
        {
            return _verboseRegistrationLogSuppressionCount == 0;
        }

        private void LogExternalConflictRegistration(int id, string? name, int win32Error)
        {
            _logger.Warning(
                "HotkeyService",
                () => $"hotkey-register-conflict | id={id} scope=external win32={win32Error} name={LogPrivacy.Label(name)}");
        }

        internal void LogRegistrationGroupDeliverySummary(string group, params int[] hotkeyIds)
        {
            int hybridCount = 0;
            int hookOnlyFallbackCount = 0;

            lock (_lock)
            {
                foreach (int hotkeyId in hotkeyIds)
                {
                    if (!_registrationDeliveryById.TryGetValue(hotkeyId, out string? delivery))
                    {
                        continue;
                    }

                    if (string.Equals(delivery, "hook-only-fallback", StringComparison.Ordinal))
                    {
                        hookOnlyFallbackCount++;
                        continue;
                    }

                    if (string.Equals(delivery, "hybrid", StringComparison.Ordinal))
                    {
                        hybridCount++;
                    }
                }
            }

            if (hookOnlyFallbackCount == 0)
            {
                return;
            }

            _logger.Info(
                "HotkeyService",
                () => $"hotkey-register-group-delivery | group={group} hybrid={hybridCount} hookOnlyFallback={hookOnlyFallbackCount} degraded=true");
        }

        private static HotkeyOsRegistrationResult RegisterHotKeyCore(IntPtr windowHandle, int id, uint fsModifiers, uint vk)
        {
            bool success = RegisterHotKey(windowHandle, id, fsModifiers, vk);
            return success
                ? new HotkeyOsRegistrationResult(Succeeded: true)
                : new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: Marshal.GetLastWin32Error());
        }

        private static void UnregisterHotKeyCore(IntPtr windowHandle, int id)
        {
            _ = UnregisterHotKey(windowHandle, id);
        }

        private static string GetInitializationMode(bool keyboardMessageHostInstalled, bool keyboardHookInstalled, bool mouseHookInstalled)
        {
            if (keyboardMessageHostInstalled && keyboardHookInstalled && mouseHookInstalled)
            {
                return "hybrid";
            }

            if (keyboardMessageHostInstalled && (keyboardHookInstalled || mouseHookInstalled))
            {
                return "message-plus-hooks";
            }

            if (keyboardHookInstalled || mouseHookInstalled)
            {
                return "hook-only";
            }

            if (keyboardMessageHostInstalled)
            {
                return "message-host-only";
            }

            return "inactive";
        }
    }
}

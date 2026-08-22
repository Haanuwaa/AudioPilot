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
        private readonly HashSet<string> _registeredCombos = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> _registrationDeliveryById = [];
        private readonly Dictionary<int, HotkeyRegistrationOutcome> _registrationOutcomeById = [];
        private readonly HotkeyParsingService _hotkeyParser;
        private readonly HotkeyDispatchCoordinator _dispatchCoordinator;
        private readonly IKeyboardHotkeyMessageHost _keyboardMessageHost;
        private readonly IKeyboardHotkeyCaptureHost _keyboardCaptureHost;
        private readonly IMouseHotkeyCaptureHost _mouseCaptureHost;
        private readonly Func<IntPtr, int, uint, uint, HotkeyOsRegistrationResult> _registerHotKeyInvoker;
        private readonly Action<IntPtr, int> _unregisterHotKeyInvoker;
        private IReadOnlyList<string> _additionalStandaloneHotkeyKeys = [];

        private int _disposeStarted;
        private int _verboseRegistrationLogSuppressionCount;
        private bool _initialized;

        public event Action? OnHotkeyPressed;
        public event Action? OnToggleAppVisibilityHotkeyPressed;
        public event Action? OnMediaShowCurrentTrackPressed;
        public event Action? OnMediaPlayPausePressed;
        public event Action? OnMediaNextTrackPressed;
        public event Action? OnMediaPrevTrackPressed;
        public event Action? OnMuteMicPressed;
        public event Action? OnMuteSoundPressed;
        public event Action? OnDeafenPressed;
        public event Action? OnListenToInputPressed;
        public event Action? OnMasterVolumeUpPressed;
        public event Action? OnMasterVolumeDownPressed;
        public event Action? OnMicVolumeUpPressed;
        public event Action? OnMicVolumeDownPressed;
        public event Action? OnOutputSwitchHotkeyPressed;
        public event Action? OnInputSwitchHotkeyPressed;
        public event Action? OnOutputReverseSwitchHotkeyPressed;
        public event Action? OnInputReverseSwitchHotkeyPressed;

        private class HotkeyDefinition
        {
            public required int Id { get; init; }
            public required HotkeyMainInput MainInput { get; init; }
            public required List<Key> Modifiers { get; init; }
            public required bool UseOsRegistration { get; init; }
            public bool AllowHookOnlyFallbackWhenOsRegistrationFails { get; init; }
            public Action? Callback { get; init; }
            public required string Description { get; init; }
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
            Func<Action<int, long>, IKeyboardHotkeyMessageHost>? keyboardMessageHostFactory = null,
            Func<Action<KeyboardHotkeyBindingSnapshot, long>, IKeyboardHotkeyCaptureHost>? keyboardCaptureHostFactory = null,
            Func<Action<MouseHotkeyBindingSnapshot, long>, IMouseHotkeyCaptureHost>? mouseCaptureHostFactory = null)
        {
            _logger = Logger.Instance;
            _hotkeyParser = hotkeyParser ?? new HotkeyParsingService();
            _dispatchCoordinator = dispatchCoordinator ?? new HotkeyDispatchCoordinator(_logger);
            _registerHotKeyInvoker = registerHotKeyInvoker ?? RegisterHotKeyCore;
            _unregisterHotKeyInvoker = unregisterHotKeyInvoker ?? UnregisterHotKeyCore;
            _keyboardMessageHost = (keyboardMessageHostFactory ?? (dispatch => new MessageOnlyKeyboardHotkeyHost(_logger, dispatch, _registerHotKeyInvoker, _unregisterHotKeyInvoker)))(DispatchKeyboardMessageHotkey);
            _keyboardCaptureHost = (keyboardCaptureHostFactory ?? (dispatch => new LowLevelKeyboardHotkeyThreadHost(_logger, dispatch, setKeyboardHookInvoker)))(DispatchKeyboardHotkeyMatch);
            _mouseCaptureHost = (mouseCaptureHostFactory ?? (dispatch => new LowLevelMouseHotkeyThreadHost(_logger, dispatch, setMouseHookInvoker)))(DispatchMouseHotkeyMatch);
        }

        /// <summary>
        /// Initializes hotkey infrastructure without depending on a caller-owned window handle.
        /// </summary>
        internal void InitializeInfrastructure()
        {
            bool keyboardMessageHostInstalled = false;
            bool keyboardHookInstalled = false;
            bool mouseHookInstalled = false;
            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;

                ActivateInitializedKeyboardRegistrationsUnderLock();
                PublishOrStopKeyboardMessageHostUnderLock();
                keyboardMessageHostInstalled = _keyboardMessageHost.IsRunning;
                keyboardHookInstalled = EnsureKeyboardHookInstalledIfNeededUnderLock();
                if (!keyboardHookInstalled)
                {
                    DeactivateUnavailableFallbackKeyboardRegistrationsUnderLock();
                }
                mouseHookInstalled = EnsureMouseHookInstalledIfNeededUnderLock();
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

        public void UpdateAdditionalStandaloneHotkeyKeys(IEnumerable<string>? additionalStandaloneHotkeyKeys)
        {
            lock (_lock)
            {
                _additionalStandaloneHotkeyKeys = [.. HotkeyStandaloneKeyPolicy.Analyze(additionalStandaloneHotkeyKeys).EffectiveTokens];
            }
        }

        public bool RegisterToggleAppVisibilityHotkey(string? toggleAppVisibilityHotkey, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            const string Default = "Ctrl+Alt+H";
            bool registered = RegisterGenericHotkey(
                AppConstants.Hotkeys.ToggleAppVisibilityHotkeyId,
                toggleAppVisibilityHotkey,
                Default,
                () => OnToggleAppVisibilityHotkeyPressed?.Invoke(),
                "Show / Hide App",
                default,
                additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterMediaHotkeys(string? showCurrent, string? playPause, string? next, string? prev, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            HotkeyRegistrationOptions options = new(AllowHookOnlyFallbackWhenOsRegistrationFails: true);
            bool showCurrentRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaShowCurrentTrackId, showCurrent, null, () => OnMediaShowCurrentTrackPressed?.Invoke(), "Show Current Track", options, additionalStandaloneHotkeyKeys);
            bool playPauseRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaPlayPauseId, playPause, "Ctrl+Alt+P", () => OnMediaPlayPausePressed?.Invoke(), "Play/Pause", options, additionalStandaloneHotkeyKeys);
            bool nextRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaNextTrackId, next, "Ctrl+Alt+.", () => OnMediaNextTrackPressed?.Invoke(), "Next Track", options, additionalStandaloneHotkeyKeys);
            bool prevRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MediaPrevTrackId, prev, "Ctrl+Alt+,", () => OnMediaPrevTrackPressed?.Invoke(), "Prev Track", options, additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();

            LogRegistrationGroupDeliverySummary(
                "media",
                AppConstants.Hotkeys.MediaShowCurrentTrackId,
                AppConstants.Hotkeys.MediaPlayPauseId,
                AppConstants.Hotkeys.MediaNextTrackId,
                AppConstants.Hotkeys.MediaPrevTrackId);

            return showCurrentRegistered && playPauseRegistered && nextRegistered && prevRegistered;
        }

        public bool RegisterMuteHotkeys(string? muteMic, string? muteSound, string? deafen, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            bool muteMicRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MuteMicId, muteMic, null, () => OnMuteMicPressed?.Invoke(), "Mute Mic", default, additionalStandaloneHotkeyKeys);
            bool muteSoundRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MuteSoundId, muteSound, null, () => OnMuteSoundPressed?.Invoke(), "Mute Sound", default, additionalStandaloneHotkeyKeys);
            bool deafenRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.DeafenId, deafen, null, () => OnDeafenPressed?.Invoke(), "Deafen", default, additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();

            return muteMicRegistered && muteSoundRegistered && deafenRegistered;
        }

        public bool RegisterListenToInputHotkey(string? hotkey, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.ListenToInputHotkeyId, hotkey, null, () => OnListenToInputPressed?.Invoke(), "Listen To Input", default, additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterVolumeStepHotkeys(string? masterUp, string? masterDown, string? micUp, string? micDown, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            bool masterUpRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MasterVolumeUpHotkeyId, masterUp, null, () => OnMasterVolumeUpPressed?.Invoke(), "Master Volume Up", default, additionalStandaloneHotkeyKeys);
            bool masterDownRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MasterVolumeDownHotkeyId, masterDown, null, () => OnMasterVolumeDownPressed?.Invoke(), "Master Volume Down", default, additionalStandaloneHotkeyKeys);
            bool micUpRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MicVolumeUpHotkeyId, micUp, null, () => OnMicVolumeUpPressed?.Invoke(), "Mic Volume Up", default, additionalStandaloneHotkeyKeys);
            bool micDownRegistered = RegisterGenericHotkey(AppConstants.Hotkeys.MicVolumeDownHotkeyId, micDown, null, () => OnMicVolumeDownPressed?.Invoke(), "Mic Volume Down", default, additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();

            return masterUpRegistered && masterDownRegistered && micUpRegistered && micDownRegistered;
        }

        public bool RegisterOutputSwitchHotkey(string? hotkey, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.OutputSwitchHotkeyId, hotkey, null, () => OnOutputSwitchHotkeyPressed?.Invoke(), "Output Switch", default, additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterInputSwitchHotkey(string? hotkey, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.InputSwitchHotkeyId, hotkey, null, () => OnInputSwitchHotkeyPressed?.Invoke(), "Input Switch", default, additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterOutputReverseSwitchHotkey(string? hotkey, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.OutputReverseSwitchHotkeyId, hotkey, null, () => OnOutputReverseSwitchHotkeyPressed?.Invoke(), "Output Reverse Switch", default, additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterInputReverseSwitchHotkey(string? hotkey, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            bool registered = RegisterGenericHotkey(AppConstants.Hotkeys.InputReverseSwitchHotkeyId, hotkey, null, () => OnInputReverseSwitchHotkeyPressed?.Invoke(), "Input Reverse Switch", default, additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public bool RegisterDynamicHotkey(int id, string? hotkey, Action callback, string name, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);

            ArgumentNullException.ThrowIfNull(callback);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            bool registered = RegisterGenericHotkey(id, hotkey, null, callback, name, new HotkeyRegistrationOptions(AllowHookOnlyFallbackWhenOsRegistrationFails: true), additionalStandaloneHotkeyKeys);
            FinalizeKeyboardMessageHostLifecycle();
            return registered;
        }

        public void UnregisterHotkey(int id)
        {
            if (id <= 0)
            {
                return;
            }

            lock (_lock)
            {
                RemoveHotkeyById(id);
            }
        }

        public void UnregisterUserHotkey()
        {
            lock (_lock)
            {
                RemoveHotkeyById(AppConstants.Hotkeys.HotkeyId);
                _logger.Debug("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.Unregister} | scope=user");
            }
        }

        public void UnregisterAllHotkeys()
        {
            lock (_lock)
            {
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
                                _logger.Trace("HotkeyService", () => $"hotkey-unregister-ignored | id={hk.Id} description={FormatHotkeyDescriptionForLog(hk.Description)} reason={ex.GetType().Name}");
                            }
                        }
                    }
                }

                _hotkeys.Clear();
                _registeredCombos.Clear();
                _registrationDeliveryById.Clear();
                _registrationOutcomeById.Clear();
                _dispatchCoordinator.Reset();
                ReleaseKeyboardMessageHostIfUnusedUnderLock();
                ReleaseKeyboardHookIfUnusedUnderLock();
                ReleaseMouseHookIfUnusedUnderLock();
                _logger.Info("HotkeyService", () => $"{AppConstants.Audio.LogEvents.Hotkey.Unregister} | scope=all");
            }
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

            _logger.Info("HotkeyService", "hotkey-service-dispose-start");

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
                _registeredCombos.Clear();
                _registrationDeliveryById.Clear();
                _registrationOutcomeById.Clear();
                _dispatchCoordinator.Reset();
                _hotkeyParser.ClearCache();
            }

            GC.SuppressFinalize(this);
        }

        private void DispatchKeyboardMessageHotkey(int hotkeyId, long enqueueTimestamp)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return;
            }

            HotkeyDefinition? matched = null;
            lock (_lock)
            {
                matched = _hotkeys.FirstOrDefault(h => h.Id == hotkeyId);
            }

            if (matched != null)
            {
                _dispatchCoordinator.ExecuteCallback(matched.Id, matched.Description, matched.Callback, enqueueTimestamp);
            }
        }

        private void DispatchKeyboardHotkeyMatch(KeyboardHotkeyBindingSnapshot binding, long enqueueTimestamp)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return;
            }

            _dispatchCoordinator.ExecuteCallback(binding.Id, binding.Description, binding.Callback, enqueueTimestamp);
        }

        private void DispatchMouseHotkeyMatch(MouseHotkeyBindingSnapshot binding, long enqueueTimestamp)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return;
            }

            _dispatchCoordinator.ExecuteCallback(binding.Id, binding.Description, binding.Callback, enqueueTimestamp);
        }

        /// <summary>
        /// Registers a logical hotkey action, replacing any existing registration for the same id.
        /// </summary>
        private bool RegisterGenericHotkey(int id, string? hotkeyString, string? defaultKey, Action callback, string name, HotkeyRegistrationOptions options = default, IEnumerable<string>? additionalStandaloneHotkeyKeys = null)
        {
            lock (_lock)
            {
                if (Volatile.Read(ref _disposeStarted) != 0)
                {
                    _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Unsupported, "service-disposed");
                    return false;
                }

                RemoveHotkeyById(id);

                string? finalString = hotkeyString;
                if (string.IsNullOrEmpty(finalString))
                {
                    if (finalString == null && defaultKey != null)
                    {
                        finalString = defaultKey;
                        if (ShouldLogVerboseRegistrationEventsUnderLock())
                        {
                            _logger.Debug("HotkeyService", () => $"hotkey-default-applied | id={id} hotkey={FormatHotkeyForLog(finalString)}");
                        }
                    }
                    else
                    {
                        _registrationOutcomeById.Remove(id);
                        return true;
                    }
                }

                var parsed = ParseHotkeyString(finalString);
                if (parsed.HasValue)
                {
                    IReadOnlyList<string> effectiveAdditionalStandaloneHotkeyKeys = additionalStandaloneHotkeyKeys == null
                        ? _additionalStandaloneHotkeyKeys
                        : [.. HotkeyStandaloneKeyPolicy.Analyze(additionalStandaloneHotkeyKeys).EffectiveTokens];

                    if (HotkeyReservedShortcutPolicy.IsReserved(parsed.Value.mainInput, parsed.Value.modifiers, out string reservedShortcutName))
                    {
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Reserved, reservedShortcutName);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=reserved reserved={reservedShortcutName} hotkey={FormatHotkeyForLog(finalString)}");
                        return false;
                    }

                    if (!parsed.Value.mainInput.IsSupportedModifierCount(parsed.Value.modifiers.Count, effectiveAdditionalStandaloneHotkeyKeys))
                    {
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Unsupported);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=unsupported hotkey={FormatHotkeyForLog(finalString)}");
                        return false;
                    }

                    string combo = HotkeyParsingService.BuildComboKey(parsed.Value.modifiers, parsed.Value.mainInput);
                    if (_registeredCombos.Contains(combo))
                    {
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.Duplicate);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-skipped | id={id} reason=duplicate hotkey={FormatHotkeyForLog(combo)}");
                        return false;
                    }

                    var definition = new HotkeyDefinition
                    {
                        Id = id,
                        MainInput = parsed.Value.mainInput,
                        Modifiers = parsed.Value.modifiers,
                        UseOsRegistration = ShouldUseOsRegistration(parsed.Value.mainInput),
                        AllowHookOnlyFallbackWhenOsRegistrationFails = options.AllowHookOnlyFallbackWhenOsRegistrationFails,
                        Callback = callback,
                        Description = $"{name} ({finalString})"
                    };

                    _hotkeys.Add(definition);
                    _registeredCombos.Add(combo);
                    HotkeyOsRegistrationResult osRegistration = definition.UseOsRegistration
                        ? RegisterOsHotkey(definition)
                        : new HotkeyOsRegistrationResult(Succeeded: true);
                    if (definition.UseOsRegistration && !osRegistration.Succeeded && !options.AllowHookOnlyFallbackWhenOsRegistrationFails)
                    {
                        _hotkeys.Remove(definition);
                        _registeredCombos.Remove(combo);
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.ExternalConflict, Win32Error: osRegistration.ErrorCode);
                        LogExternalConflictRegistration(id, name, osRegistration.ErrorCode);

                        return false;
                    }

                    if (definition.UseOsRegistration && !osRegistration.Succeeded)
                    {
                        _logger.Warning("HotkeyService", () => $"hotkey-register-hook-fallback | id={id} description={FormatHotkeyDescriptionForLog(definition.Description)} win32={osRegistration.ErrorCode}");
                    }

                    string delivery = !definition.UseOsRegistration
                        ? "hook-only"
                        : osRegistration.Succeeded ? "hybrid" : "hook-only-fallback";
                    _registrationDeliveryById[id] = delivery;

                    if (RequiresKeyboardFallback(definition, osRegistration) && !EnsureKeyboardHookInstalledIfNeededUnderLock())
                    {
                        _hotkeys.Remove(definition);
                        _registeredCombos.Remove(combo);
                        _registrationDeliveryById.Remove(id);
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.KeyboardHookUnavailable);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=keyboard-hook-unavailable hotkey={FormatHotkeyForLog(finalString)}");
                        return false;
                    }

                    if (!definition.UseOsRegistration && definition.MainInput.CanUseOsRegistration && !EnsureKeyboardHookInstalledIfNeededUnderLock())
                    {
                        _hotkeys.Remove(definition);
                        _registeredCombos.Remove(combo);
                        _registrationDeliveryById.Remove(id);
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.KeyboardHookUnavailable);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=keyboard-hook-unavailable hotkey={FormatHotkeyForLog(finalString)}");
                        return false;
                    }

                    if (definition.UseOsRegistration && osRegistration.Succeeded && !EnsureKeyboardHookInstalledIfNeededUnderLock())
                    {
                        _logger.Warning("HotkeyService", () => $"keyboard-hook-compatibility-unavailable | id={id} description={FormatHotkeyDescriptionForLog(definition.Description)} delivery=hybrid");
                    }

                    if (definition.MainInput.IsMouseInput && !EnsureMouseHookInstalledIfNeededUnderLock())
                    {
                        _hotkeys.Remove(definition);
                        _registeredCombos.Remove(combo);
                        _registrationDeliveryById.Remove(id);
                        _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.MouseHookUnavailable);
                        _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=mouse-hook-unavailable hotkey={FormatHotkeyForLog(finalString)}");
                        return false;
                    }

                    _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(
                        !definition.UseOsRegistration || osRegistration.Succeeded
                            ? HotkeyRegistrationOutcomeKind.Registered
                            : HotkeyRegistrationOutcomeKind.Fallback);
                    if (ShouldLogVerboseRegistrationEventsUnderLock())
                    {
                        _logger.Debug("HotkeyService", () => $"hotkey-register-success | id={id} hotkey={FormatHotkeyForLog(finalString)} delivery={delivery}");
                    }

                    return true;
                }
                else
                {
                    _registrationOutcomeById[id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.ParseError);
                    _logger.Warning("HotkeyService", () => $"hotkey-register-failed | id={id} reason=parse hotkey={FormatHotkeyForLog(finalString)}");
                    return false;
                }
            }
        }

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
                _logger.Warning("HotkeyService", () => $"keyboard-message-host-unavailable | id={hotkey.Id} description={FormatHotkeyDescriptionForLog(hotkey.Description)}");
                return new HotkeyOsRegistrationResult(Succeeded: false);
            }

            uint fsModifiers = MOD_NOREPEAT;
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
                    _logger.Warning("HotkeyService", () => $"hotkey-register-os-failed | id={hotkey.Id} description={FormatHotkeyDescriptionForLog(hotkey.Description)} win32={result.ErrorCode} reason=in-use-or-unavailable");
                    return result;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Warning("HotkeyService", () => $"hotkey-register-os-failed | id={hotkey.Id} description={FormatHotkeyDescriptionForLog(hotkey.Description)} reason={ex.GetType().Name}");
                return new HotkeyOsRegistrationResult(Succeeded: false);
            }
        }

        private void ActivateInitializedKeyboardRegistrationsUnderLock()
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
                    _logger.Warning("HotkeyService", () => $"hotkey-register-hook-fallback | id={hotkey.Id} description={FormatHotkeyDescriptionForLog(hotkey.Description)} win32={osRegistration.ErrorCode}");
                }

                _registrationDeliveryById[hotkey.Id] = osRegistration.Succeeded ? "hybrid" : "hook-only-fallback";
                _registrationOutcomeById[hotkey.Id] = new HotkeyRegistrationOutcome(
                    osRegistration.Succeeded
                        ? HotkeyRegistrationOutcomeKind.Registered
                        : HotkeyRegistrationOutcomeKind.Fallback);
            }

            PublishOrStopKeyboardMessageHostUnderLock();

            foreach (int id in registrationsToRemove)
            {
                RemoveHotkeyById(id, preserveOutcome: true);
            }
        }

        private void DeactivateUnavailableFallbackKeyboardRegistrationsUnderLock()
        {
            List<int> registrationsToRemove = [];
            foreach (HotkeyDefinition hotkey in _hotkeys)
            {
                if (!hotkey.MainInput.CanUseOsRegistration ||
                    !_registrationDeliveryById.TryGetValue(hotkey.Id, out string? delivery) ||
                    string.Equals(delivery, "hybrid", StringComparison.Ordinal))
                {
                    continue;
                }

                _registrationOutcomeById[hotkey.Id] = new HotkeyRegistrationOutcome(HotkeyRegistrationOutcomeKind.KeyboardHookUnavailable);
                registrationsToRemove.Add(hotkey.Id);
            }

            foreach (int id in registrationsToRemove)
            {
                RemoveHotkeyById(id, preserveOutcome: true);
            }
        }

        private void RemoveHotkeyById(int id, bool preserveOutcome = false)
        {
            for (int index = 0; index < _hotkeys.Count; index++)
            {
                var hk = _hotkeys[index];
                if (hk.Id != id)
                {
                    continue;
                }

                if (hk.UseOsRegistration && _keyboardMessageHost.IsRunning)
                {
                    try
                    {
                        _keyboardMessageHost.UnregisterHotkey(id);
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("HotkeyService", () => $"Ignored unregister failure for hotkey id={id}: {ex.GetType().Name}");
                        }
                    }
                }

                string combo = HotkeyParsingService.BuildComboKey(hk.Modifiers, hk.MainInput);
                _registeredCombos.Remove(combo);
                _registrationDeliveryById.Remove(id);
                if (!preserveOutcome)
                {
                    _registrationOutcomeById.Remove(id);
                }
            }
            _hotkeys.RemoveAll(x => x.Id == id);

            PublishOrStopKeyboardMessageHostUnderLock();
            PublishOrStopKeyboardCaptureUnderLock();
            PublishOrStopMouseCaptureUnderLock();
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
                        LowLevelKeyboardHotkeyThreadHost.GetModifierMaskFromKeys(hotkey.Modifiers),
                        hotkey.Callback,
                        hotkey.Description));
            }

            return KeyboardHotkeySnapshot.Create(bindings);
        }

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
                        LowLevelMouseHotkeyThreadHost.GetModifierMaskFromKeys(hotkey.Modifiers),
                        hotkey.Callback,
                        hotkey.Description));
            }

            return MouseHotkeySnapshot.Create(bindings);
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

        private (HotkeyMainInput mainInput, List<Key> modifiers)? ParseHotkeyString(string hotkeyString)
        {
            return _hotkeyParser.ParseHotkeyString(hotkeyString);
        }

        private static string FormatHotkeyForLog(string? hotkey)
        {
            return LogPrivacy.Label(hotkey);
        }

        private static string FormatHotkeyDescriptionForLog(string? description)
        {
            return LogPrivacy.Label(description);
        }

        private bool ShouldLogVerboseRegistrationEventsUnderLock()
        {
            return _verboseRegistrationLogSuppressionCount == 0;
        }

        private void LogExternalConflictRegistration(int id, string? name, int win32Error)
        {
            _logger.Warning(
                "HotkeyService",
                () => $"hotkey-register-conflict | id={id} scope=external win32={win32Error} name={FormatHotkeyDescriptionForLog(name)}");
        }

        internal void LogRegistrationGroupDeliverySummary(string group, params int[] hotkeyIds)
        {
            int hybridCount = 0;
            int hookOnlyFallbackCount = 0;

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

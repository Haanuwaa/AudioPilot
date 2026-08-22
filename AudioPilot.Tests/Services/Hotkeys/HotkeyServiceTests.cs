using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Hotkeys;

public sealed class HotkeyServiceTests : IDisposable
{
    private long _utcNowTicks = TimeSpan.TicksPerSecond;
    private FakeKeyboardHotkeyMessageHost _messageHost = null!;
    private readonly HotkeyService _service;

    public HotkeyServiceTests()
    {
        _service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            keyboardMessageHostFactory: dispatch => _messageHost = new FakeKeyboardHotkeyMessageHost(dispatch));
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_ReturnsFalse_WhenHotkeyStringInvalid()
    {
        bool registered = _service.RegisterOutputSwitchHotkey("Ctrl+NotARealKey");

        Assert.False(registered);
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_ReturnsFalse_WhenHotkeyUsesBareTextKey()
    {
        bool registered = _service.RegisterOutputSwitchHotkey("A");

        Assert.False(registered);
    }

    [Theory]
    [InlineData("Alt+Tab")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+Shift+Esc")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Win+R")]
    [InlineData("Win+Shift+S")]
    [InlineData("Win+Space")]
    [InlineData("Win+P")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Space")]
    [InlineData("Win+K")]
    [InlineData("Win+S")]
    [InlineData("Win+A")]
    [InlineData("Win+V")]
    [InlineData("Win+G")]
    [InlineData("Win+Left")]
    [InlineData("Win+Shift+Right")]
    [InlineData("Ctrl+Win+Down")]
    [InlineData("Win+Tab")]
    [InlineData("Ctrl+Win+Tab")]
    [InlineData("Win+Shift+Tab")]
    public void RegisterOutputSwitchHotkey_ReturnsFalse_WhenHotkeyUsesReservedWindowsShortcut(string hotkey)
    {
        bool registered = _service.RegisterOutputSwitchHotkey(hotkey);

        Assert.False(registered);
    }

    [Theory]
    [InlineData("Ctrl+Win+E")]
    [InlineData("Shift+Win+R")]
    [InlineData("Alt+Win+X")]
    [InlineData("Ctrl+Shift+Win+Tab")]
    public void RegisterOutputSwitchHotkey_ReturnsTrue_ForModifiedWinShortcutsOutsideReservedSet(string hotkey)
    {
        bool registered = _service.RegisterOutputSwitchHotkey(hotkey);

        Assert.True(registered);
    }

    [Fact]
    public void RegisterVolumeStepHotkeys_ReturnsFalse_WhenAnyHotkeyRegistrationFails()
    {
        bool registered = _service.RegisterVolumeStepHotkeys("Ctrl+Alt+1", "Ctrl+Alt+1", "Ctrl+Alt+2", "Ctrl+Alt+3");

        Assert.False(registered);
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_ReturnsFalse_WhenComboAlreadyRegistered()
    {
        bool mainRegistered = _service.RegisterHotkey([Key.LeftCtrl, Key.LeftAlt], Key.H);
        bool outputRegistered = _service.RegisterOutputSwitchHotkey("Ctrl+Alt+H");

        Assert.True(mainRegistered);
        Assert.False(outputRegistered);
    }

    [Fact]
    public void RegisterOutputReverseSwitchHotkey_ReturnsFalse_WhenComboAlreadyRegistered()
    {
        bool outputRegistered = _service.RegisterOutputSwitchHotkey("Ctrl+Alt+H");
        bool reverseRegistered = _service.RegisterOutputReverseSwitchHotkey("Ctrl+Alt+H");

        Assert.True(outputRegistered);
        Assert.False(reverseRegistered);
    }

    [Fact]
    public void RegisterMediaHotkeys_ReturnsFalse_WhenAnyHotkeyRegistrationFails()
    {
        bool registered = _service.RegisterMediaHotkeys(null, "Ctrl+Alt+P", "Ctrl+Alt+P", "Ctrl+Alt+,", null);

        Assert.False(registered);
    }

    [Fact]
    public void RegisterMediaHotkeys_UsesHookOnlyFallback_WhenOsRegistrationUnavailable()
    {
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { });
        service.InitializeInfrastructure();

        bool registered = service.RegisterMediaHotkeys("F7", "F8", "F9", "F10");

        Assert.True(registered);
        List<(int Id, string Description)> hotkeys = TestPrivateAccess.GetRegisteredHotkeys(service);
        Assert.Contains(hotkeys, hotkey => hotkey.Id == AppConstants.Hotkeys.MediaShowCurrentTrackId);
        Assert.Contains(hotkeys, hotkey => hotkey.Id == AppConstants.Hotkeys.MediaPlayPauseId);
        Assert.Contains(hotkeys, hotkey => hotkey.Id == AppConstants.Hotkeys.MediaNextTrackId);
        Assert.Contains(hotkeys, hotkey => hotkey.Id == AppConstants.Hotkeys.MediaPrevTrackId);
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_ReturnsFalse_WhenOsRegistrationUnavailable_AndFallbackNotAllowed()
    {
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { });
        service.InitializeInfrastructure();

        bool registered = service.RegisterOutputSwitchHotkey("F8");

        Assert.False(registered);
        Assert.Empty(TestPrivateAccess.GetRegisteredHotkeys(service));
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_LogsExternalConflictWithWin32Code_WithoutRawHotkeyText()
    {
        using var loggerScope = new TestLoggerScope("hotkey-external-conflict", "app.log");
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(loggerScope.Logger, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { });
        TestPrivateAccess.SetField(service, "_logger", loggerScope.Logger);
        service.InitializeInfrastructure();

        bool registered = service.RegisterOutputSwitchHotkey("F8");

        Assert.False(registered);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.DoesNotContain("hotkey=F8", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Output Switch (F8)", logText, StringComparison.Ordinal);
        Assert.Contains("hotkey-register-conflict", logText, StringComparison.Ordinal);
        Assert.Contains("scope=external", logText, StringComparison.Ordinal);
        Assert.Contains("win32=1409", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterDynamicHotkey_UsesHookOnlyFallback_WhenOsRegistrationUnavailable()
    {
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { });
        service.InitializeInfrastructure();

        bool registered = service.RegisterDynamicHotkey(10000, "F8", static () => { }, "Routine");

        Assert.True(registered);
        Assert.Contains(TestPrivateAccess.GetRegisteredHotkeys(service), hotkey => hotkey.Id == 10000);
    }

    [Fact]
    public void InitializeInfrastructure_CalledTwice_StartsKeyboardFallbackHostOnlyOnce_WhenFallbackHotkeysExist()
    {
        FakeKeyboardHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { },
            keyboardCaptureHostFactory: dispatch => host = new FakeKeyboardHotkeyCaptureHost(dispatch));
        Assert.True(service.RegisterDynamicHotkey(10020, "F8", static () => { }, "Fallback Test"));

        service.InitializeInfrastructure();
        service.InitializeInfrastructure();

        Assert.NotNull(host);
        Assert.True(host!.IsRunning);
        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public async Task InitializeInfrastructure_ConcurrentCalls_StartKeyboardFallbackHostOnlyOnce_WhenFallbackHotkeysExist()
    {
        FakeKeyboardHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { },
            keyboardCaptureHostFactory: dispatch => host = new FakeKeyboardHotkeyCaptureHost(dispatch));
        Assert.True(service.RegisterDynamicHotkey(10021, "F8", static () => { }, "Fallback Test"));

        using var start = new ManualResetEventSlim(false);
        Task first = Task.Run(() =>
        {
            start.Wait();
            service.InitializeInfrastructure();
        }, TestContext.Current.CancellationToken);
        Task second = Task.Run(() =>
        {
            start.Wait();
            service.InitializeInfrastructure();
        }, TestContext.Current.CancellationToken);

        start.Set();
        await Task.WhenAll(first, second);

        Assert.NotNull(host);
        Assert.True(host!.IsRunning);
        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_AfterInitialize_RegistersThroughMessageHost()
    {
        IntPtr usedWindowHandle = IntPtr.Zero;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: (windowHandle, _, _, _) =>
            {
                usedWindowHandle = windowHandle;
                return new HotkeyOsRegistrationResult(Succeeded: true);
            },
            unregisterHotKeyInvoker: static (_, _) => { },
            setKeyboardHookInvoker: static () => (IntPtr)1);

        service.InitializeInfrastructure();

        bool registered = service.RegisterOutputSwitchHotkey("F8");

        Assert.True(registered);
        Assert.NotEqual(IntPtr.Zero, usedWindowHandle);
        Assert.NotEqual((IntPtr)1234, usedWindowHandle);
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_BeforeInitialize_ActivatesThroughMessageHostOnInitializeInfrastructure()
    {
        IntPtr usedWindowHandle = IntPtr.Zero;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: (windowHandle, _, _, _) =>
            {
                usedWindowHandle = windowHandle;
                return new HotkeyOsRegistrationResult(Succeeded: true);
            },
            unregisterHotKeyInvoker: static (_, _) => { },
            setKeyboardHookInvoker: static () => (IntPtr)1);

        bool registered = service.RegisterOutputSwitchHotkey("F8");

        Assert.True(registered);
        Assert.Equal(IntPtr.Zero, usedWindowHandle);

        service.InitializeInfrastructure();

        Assert.NotEqual(IntPtr.Zero, usedWindowHandle);
        Assert.NotEqual((IntPtr)1234, usedWindowHandle);
        Assert.Equal(HotkeyRegistrationOutcomeKind.Registered, service.GetLastRegistrationOutcome(AppConstants.Hotkeys.OutputSwitchHotkeyId).Kind);
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_BeforeInitialize_UpdatesOutcomeWhenOsRegistrationFails()
    {
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { });

        bool registered = service.RegisterOutputSwitchHotkey("F8");

        Assert.True(registered);
        Assert.Contains(TestPrivateAccess.GetRegisteredHotkeys(service), hotkey => hotkey.Id == AppConstants.Hotkeys.OutputSwitchHotkeyId);

        service.InitializeInfrastructure();

        Assert.Empty(TestPrivateAccess.GetRegisteredHotkeys(service));
        HotkeyRegistrationOutcome outcome = service.GetLastRegistrationOutcome(AppConstants.Hotkeys.OutputSwitchHotkeyId);
        Assert.Equal(HotkeyRegistrationOutcomeKind.ExternalConflict, outcome.Kind);
        Assert.Equal(1409, outcome.Win32Error);
    }

    [Fact]
    public async Task RegisterHotkey_UsesKeyboardHostCompatibilityPath()
    {
        FakeKeyboardHotkeyCaptureHost? host = null;
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: true),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch),
            keyboardCaptureHostFactory: dispatch => host = new FakeKeyboardHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnHotkeyPressed += () => activation.TrySetResult(true);

        Assert.True(service.RegisterHotkey([Key.LeftCtrl, Key.LeftAlt], Key.H));
        Assert.NotNull(messageHost);
        Assert.True(messageHost!.IsRunning);
        Assert.Equal(1, messageHost.RegisterCount);
        Assert.NotNull(host);
        Assert.True(host!.IsRunning);

        _utcNowTicks += AppConstants.Timing.HotkeyDebounceTicks + TimeSpan.TicksPerMillisecond;
        Assert.True(host.Trigger(HotkeyMainInput.FromKeyboard(Key.H), HotkeyModifierMask.Ctrl | HotkeyModifierMask.Alt));
        await Task.Yield();

        await activation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ParseHotkeyString_AcceptsSlashSymbolAlias()
    {
        var parser = new HotkeyParsingService();
        var parsed = parser.ParseHotkeyString("Ctrl+Alt+/");

        Assert.True(parsed.HasValue);
        Assert.Equal(Key.Oem2, parsed.Value.mainInput.Key);
        Assert.Contains(Key.LeftCtrl, parsed.Value.modifiers);
        Assert.Contains(Key.LeftAlt, parsed.Value.modifiers);
    }

    [Theory]
    [InlineData("Ctrl++", Key.OemPlus, "Ctrl++")]
    [InlineData("Ctrl + +", Key.OemPlus, "Ctrl++")]
    public void ParseHotkeyString_AcceptsLiteralPlusMainKey(string value, Key expectedKey, string expectedCanonical)
    {
        var parser = new HotkeyParsingService();

        var parsed = parser.ParseHotkeyString(value);

        Assert.True(parsed.HasValue);
        Assert.Equal(expectedKey, parsed.Value.mainInput.Key);
        Assert.Equal(expectedCanonical, HotkeyParsingService.BuildComboKey(parsed.Value.modifiers, parsed.Value.mainInput));
    }

    [Fact]
    public void ParseHotkeyString_AcceptsMouseAliases()
    {
        var parser = new HotkeyParsingService();

        var parsed = parser.ParseHotkeyString("Ctrl+Mouse4");

        Assert.True(parsed.HasValue);
        Assert.True(parsed.Value.mainInput.IsMouseInput);
        Assert.Contains(Key.LeftCtrl, parsed.Value.modifiers);
        Assert.Equal("Ctrl+MouseX1", HotkeyParsingService.BuildComboKey(parsed.Value.modifiers, parsed.Value.mainInput));
    }

    [Theory]
    [InlineData("Ctrl+WheelLeft", "Ctrl+WheelLeft")]
    [InlineData("Ctrl+ScrollRight", "Ctrl+WheelRight")]
    public void ParseHotkeyString_AcceptsHorizontalWheelAliases(string value, string expectedCanonical)
    {
        var parser = new HotkeyParsingService();

        var parsed = parser.ParseHotkeyString(value);

        Assert.True(parsed.HasValue);
        Assert.Equal(expectedCanonical, HotkeyParsingService.BuildComboKey(parsed.Value.modifiers, parsed.Value.mainInput));
    }

    [Fact]
    public void RegisterMediaKeyMainInput_UsesHookOnlySoSyntheticFallbackCanPassThrough()
    {
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        FakeKeyboardHotkeyCaptureHost? captureHost = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch),
            keyboardCaptureHostFactory: dispatch => captureHost = new FakeKeyboardHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        bool registered = service.RegisterMediaHotkeys(null, "MediaPlayPause", string.Empty, string.Empty);

        Assert.True(registered);
        Assert.NotNull(captureHost);
        Assert.True(captureHost!.IsRunning);
        Assert.NotNull(messageHost);
        Assert.Equal(0, messageHost!.RegisterCount);
        Assert.False(messageHost.IsRunning);
    }

    [Fact]
    public void RegisterDynamicHotkey_RegistersMouseHotkeyAsHookOnly()
    {
        bool registered = _service.RegisterDynamicHotkey(10002, "Ctrl+WheelUp", static () => { }, "Wheel Test");

        Assert.True(registered);
        Assert.Contains(TestPrivateAccess.GetRegisteredHotkeys(_service), hotkey => hotkey.Id == 10002);
    }

    [Fact]
    public void RegisterVolumeStepHotkeys_LogsMouseHookInstalled_WhenWheelHotkeysAreRegisteredAfterInitialization()
    {
        using var loggerScope = new TestLoggerScope(nameof(RegisterVolumeStepHotkeys_LogsMouseHookInstalled_WhenWheelHotkeysAreRegisteredAfterInitialization), "hotkey-mouse-hook-installed.log", LogLevel.Debug);
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(loggerScope.Logger, () => _utcNowTicks),
            registerHotKeyInvoker: null,
            unregisterHotKeyInvoker: null,
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        TestPrivateAccess.SetField(service, "_logger", loggerScope.Logger);
        service.InitializeInfrastructure();

        bool registered = service.RegisterVolumeStepHotkeys("Alt+WheelUp", "Alt+WheelDown", null, null);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.True(registered);
        Assert.NotNull(host);
        Assert.True(host!.IsRunning);
        Assert.Contains("mouse-hook-installed | type=WH_MOUSE_LL reason=registered-mouse-hotkeys", logText, StringComparison.Ordinal);
        Assert.Contains("hotkey-register-success", logText, StringComparison.Ordinal);
        Assert.Contains("delivery=hook-only", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_DoesNotStartMouseHook()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        bool registered = service.RegisterOutputSwitchHotkey("Ctrl+Alt+H");

        Assert.True(registered);
        Assert.NotNull(host);
        Assert.False(host!.IsRunning);
        Assert.Equal(0, host.StartCount);
    }

    [Fact]
    public void UnregisterHotkey_ReleasesMouseHookWhenLastMouseHotkeyIsRemoved()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        bool registered = service.RegisterDynamicHotkey(10003, "Ctrl+WheelUp", static () => { }, "Wheel Test");

        Assert.True(registered);
        Assert.NotNull(host);
        Assert.True(host!.IsRunning);

        service.UnregisterHotkey(10003);

        Assert.False(host.IsRunning);
        Assert.Equal(1, host.StopCount);
        Assert.Empty(TestPrivateAccess.GetRegisteredHotkeys(service));
    }

    [Fact]
    public void UnregisterAllHotkeys_ReleasesMouseHookWhenLastMouseHotkeyIsRemoved()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        bool registered = service.RegisterDynamicHotkey(10007, "Ctrl+WheelUp", static () => { }, "Wheel Test");

        Assert.True(registered);
        Assert.NotNull(host);
        Assert.True(host!.IsRunning);

        service.UnregisterAllHotkeys();

        Assert.False(host.IsRunning);
        Assert.Equal(1, host.StopCount);
        Assert.Empty(TestPrivateAccess.GetRegisteredHotkeys(service));
    }

    [Fact]
    public void InitializeInfrastructure_DoesNotStartMouseHook_WhenNoMouseHotkeysAreRegistered()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));

        service.InitializeInfrastructure();

        Assert.NotNull(host);
        Assert.False(host!.IsRunning);
        Assert.Equal(0, host.StartCount);
    }

    [Fact]
    public void RegisterDynamicMouseHotkey_DefersHookStartUntilInitialization()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        bool registered = service.RegisterDynamicHotkey(10004, "Ctrl+WheelUp", static () => { }, "Wheel Test");

        Assert.True(registered);
        Assert.NotNull(host);
        Assert.False(host!.IsRunning);
        Assert.Equal(0, host.StartCount);
    }

    [Fact]
    public void RegisterDynamicMouseHotkey_StartsHookAfterInitialization()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();
        bool registered = service.RegisterDynamicHotkey(10009, "Ctrl+WheelUp", static () => { }, "Wheel Test");

        Assert.True(registered);
        Assert.NotNull(host);
        Assert.True(host!.IsRunning);
        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public async Task MouseHotkeyHost_DispatchesWheelHotkeyAndPreservesConsumptionSemantics()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        int activationCount = 0;
        Assert.True(service.RegisterDynamicHotkey(10010, "Alt+WheelUp", () => Interlocked.Increment(ref activationCount), "Wheel Test"));

        Assert.NotNull(host);
        Assert.True(host!.Trigger(HotkeyMainInput.WheelUp, HotkeyModifierMask.Alt));
        Assert.False(host.Trigger(HotkeyMainInput.WheelUp, HotkeyModifierMask.None));

        await TestExecutionGuards.WaitUntilAsync(
            () => Volatile.Read(ref activationCount) == 1,
            "Mouse wheel hotkey callback did not complete within the allotted timeout.");
        Assert.Equal(1, activationCount);
    }

    [Fact]
    public async Task MouseHotkeyHost_UsesStableRegistrationOrder_ForSharedMainInput()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        int callbackCount = 0;
        Assert.True(service.RegisterDynamicHotkey(10011, "Ctrl+WheelUp", () => Interlocked.Increment(ref callbackCount), "First Wheel"));
        Assert.True(service.RegisterDynamicHotkey(10012, "Alt+WheelUp", () => Interlocked.Add(ref callbackCount, 10), "Second Wheel"));

        Assert.NotNull(host);
        Assert.True(host!.Trigger(HotkeyMainInput.WheelUp, HotkeyModifierMask.Ctrl));
        Assert.True(host.Trigger(HotkeyMainInput.WheelUp, HotkeyModifierMask.Alt));

        await TestExecutionGuards.WaitUntilAsync(
            () => Volatile.Read(ref callbackCount) == 11,
            "Mouse hotkey callbacks did not complete within the allotted timeout.");
        Assert.Equal(11, callbackCount);
    }

    [Fact]
    public async Task RegisteringAdditionalMouseHotkey_UpdatesRunningHostSnapshot()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        int callbackCount = 0;
        Assert.True(service.RegisterDynamicHotkey(10014, "Ctrl+MouseX1", () => Interlocked.Increment(ref callbackCount), "Mouse One"));
        Assert.True(service.RegisterDynamicHotkey(10015, "Alt+MouseX2", () => Interlocked.Add(ref callbackCount, 10), "Mouse Two"));

        Assert.NotNull(host);
        Assert.True(host!.UpdateCount >= 1);
        Assert.True(host.Trigger(HotkeyMainInput.FromMouseButton(MouseButton.XButton2), HotkeyModifierMask.Alt));

        await TestExecutionGuards.WaitUntilAsync(
            () => Volatile.Read(ref callbackCount) == 10,
            "Updated mouse host snapshot did not dispatch the new binding within the allotted timeout.");
        Assert.Equal(10, callbackCount);
    }

    [Fact]
    public void Dispose_StopsMouseHotkeyHost()
    {
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();
        Assert.True(service.RegisterDynamicHotkey(10013, "Ctrl+MouseX1", static () => { }, "Mouse Test"));

        Assert.NotNull(host);
        Assert.True(host!.IsRunning);

        service.Dispose();

        Assert.True(host.Disposed);
        Assert.False(host.IsRunning);
        Assert.True(host.StopCount >= 1);
    }

    [Fact]
    public async Task RapidOutputSwitchHotkeyBurst_IsDebounced()
    {
        _service.InitializeInfrastructure();
        bool registered = _service.RegisterOutputSwitchHotkey("Ctrl+Alt+H");
        Assert.True(registered);

        int callbackCount = 0;
        var firstActivation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondActivation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.OnOutputSwitchHotkeyPressed += () =>
        {
            int count = Interlocked.Increment(ref callbackCount);
            if (count == 1)
            {
                firstActivation.TrySetResult(true);
            }
            else if (count == 2)
            {
                secondActivation.TrySetResult(true);
            }
        };

        for (int i = 0; i < 100; i++)
        {
            Assert.True(_messageHost.Trigger(AppConstants.Hotkeys.OutputSwitchHotkeyId));
        }

        await firstActivation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, callbackCount);

        _utcNowTicks += AppConstants.Timing.HotkeyDebounceTicks + TimeSpan.TicksPerMillisecond;
        Assert.True(_messageHost.Trigger(AppConstants.Hotkeys.OutputSwitchHotkeyId));
        await secondActivation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public async Task KeyboardHost_PreservesHybridGameCompatibility_WithoutDoubleDispatch()
    {
        FakeKeyboardHotkeyCaptureHost? host = null;
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: true),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch),
            keyboardCaptureHostFactory: dispatch => host = new FakeKeyboardHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        int callbackCount = 0;
        var firstActivation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondActivation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnOutputSwitchHotkeyPressed += () =>
        {
            int count = Interlocked.Increment(ref callbackCount);
            if (count == 1)
            {
                firstActivation.TrySetResult(true);
            }
            else if (count == 2)
            {
                secondActivation.TrySetResult(true);
            }
        };

        Assert.True(service.RegisterOutputSwitchHotkey("F8"));

        Assert.NotNull(host);
        Assert.True(host!.IsRunning);
        Assert.True(host.Trigger(HotkeyMainInput.FromKeyboard(Key.F8), HotkeyModifierMask.None));
        Assert.NotNull(messageHost);
        Assert.True(messageHost!.Trigger(AppConstants.Hotkeys.OutputSwitchHotkeyId));
        await Task.Yield();

        await firstActivation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await TestExecutionGuards.AssertDoesNotCompleteWithinAsync(
            secondActivation.Task,
            TimeSpan.FromMilliseconds(150),
            "Hybrid keyboard delivery unexpectedly dispatched a duplicate activation before debounce expiry.");

        Assert.Equal(1, callbackCount);
        Assert.False(secondActivation.Task.IsCompleted);

        _utcNowTicks += AppConstants.Timing.HotkeyDebounceTicks + TimeSpan.TicksPerMillisecond;
        Assert.True(messageHost.Trigger(AppConstants.Hotkeys.OutputSwitchHotkeyId));
        await Task.Yield();
        await secondActivation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public async Task KeyboardFallbackHost_DispatchesRegisteredHotkey_WhenOsRegistrationUnavailable()
    {
        FakeKeyboardHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { },
            keyboardCaptureHostFactory: dispatch => host = new FakeKeyboardHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(service.RegisterDynamicHotkey(10005, "F8", () => activation.TrySetResult(true), "Keyboard Cache Test"));

        Assert.NotNull(host);
        Assert.True(host!.IsRunning);
        Assert.True(host.Trigger(HotkeyMainInput.FromKeyboard(Key.F8), HotkeyModifierMask.None));
        Assert.False(host.Trigger(HotkeyMainInput.FromKeyboard(Key.F8), HotkeyModifierMask.Ctrl));

        await activation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TryParseMouseHookInput_ReturnsLeftButtonWithoutPointerData()
    {
        bool parsed = LowLevelMouseHotkeyThreadHost.TryParseMouseHookInput(
            (IntPtr)0x0201,
            IntPtr.Zero,
            out HotkeyMainInput mainInput);

        Assert.True(parsed);
        Assert.Equal(HotkeyMainInput.FromMouseButton(MouseButton.Left), mainInput);
    }

    [Theory]
    [InlineData(120, "WheelRight")]
    [InlineData(-120, "WheelLeft")]
    public void TryParseMouseHookInput_ParsesHorizontalWheel(short delta, string expectedToken)
    {
        IntPtr hookData = Marshal.AllocHGlobal(32);
        try
        {
            for (int offset = 0; offset < 32; offset += sizeof(int))
            {
                Marshal.WriteInt32(hookData, offset, 0);
            }

            Marshal.WriteInt32(hookData, 8, unchecked((ushort)delta << 16));

            Assert.True(LowLevelMouseHotkeyThreadHost.TryParseMouseHookInput((IntPtr)0x020E, hookData, out HotkeyMainInput input));
            Assert.Equal(expectedToken, input.SerializationToken);
        }
        finally
        {
            Marshal.FreeHGlobal(hookData);
        }
    }

    [Fact]
    public void RegisterDynamicHotkey_ReturnsFalse_WhenComboAlreadyRegistered()
    {
        bool outputRegistered = _service.RegisterOutputSwitchHotkey("Ctrl+Alt+H");
        bool routineRegistered = _service.RegisterDynamicHotkey(10000, "Ctrl+Alt+H", () => { }, "Routine");

        Assert.True(outputRegistered);
        Assert.False(routineRegistered);
    }

    [Fact]
    public void MessageHost_AfterDispose_DoesNotDispatchHotkey()
    {
        _service.InitializeInfrastructure();
        bool registered = _service.RegisterOutputSwitchHotkey("Ctrl+Alt+H");
        Assert.True(registered);

        int callbackCount = 0;
        _service.OnOutputSwitchHotkeyPressed += () => callbackCount++;

        _service.Dispose();
        Assert.False(_messageHost.Trigger(AppConstants.Hotkeys.OutputSwitchHotkeyId));

        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public void Dispose_WithoutWindowHandle_ClearsRegistrationState()
    {
        bool registered = _service.RegisterOutputSwitchHotkey("Ctrl+Alt+H");
        Assert.True(registered);
        Assert.NotEmpty(TestPrivateAccess.GetField<Dictionary<int, string>>(_service, "_registrationDeliveryById"));
        Assert.NotEqual(HotkeyRegistrationOutcomeKind.None, _service.GetLastRegistrationOutcome(AppConstants.Hotkeys.OutputSwitchHotkeyId).Kind);

        _service.Dispose();

        Assert.Empty(TestPrivateAccess.GetField<Dictionary<int, string>>(_service, "_registrationDeliveryById"));
        Assert.Empty(TestPrivateAccess.GetField<Dictionary<int, HotkeyRegistrationOutcome>>(_service, "_registrationOutcomeById"));
        HotkeyDispatchCoordinator dispatchCoordinator = TestPrivateAccess.GetField<HotkeyDispatchCoordinator>(_service, "_dispatchCoordinator");
        Assert.Equal(0, dispatchCoordinator.DebounceTimestampCountForTests);
    }

    [Fact]
    public void RegisterOutputSwitchHotkey_RedactsHotkeyValues_InServiceAndDispatchLogs()
    {
        using var loggerScope = new TestLoggerScope(nameof(RegisterOutputSwitchHotkey_RedactsHotkeyValues_InServiceAndDispatchLogs), "hotkey-service-privacy.log");
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(loggerScope.Logger, () => _utcNowTicks),
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch));
        TestPrivateAccess.SetField(service, "_logger", loggerScope.Logger);
        service.InitializeInfrastructure();

        bool registered = service.RegisterOutputSwitchHotkey("Ctrl+Alt+H");
        bool duplicateRegistered = service.RegisterOutputReverseSwitchHotkey("Ctrl+Alt+H");
        bool invalidRegistered = service.RegisterInputSwitchHotkey("Ctrl+NotARealKey");
        Assert.NotNull(messageHost);
        Assert.True(messageHost!.Trigger(AppConstants.Hotkeys.OutputSwitchHotkeyId));

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.True(registered);
        Assert.False(duplicateRegistered);
        Assert.False(invalidRegistered);
        Assert.Contains("hotkey-register-success", logText, StringComparison.Ordinal);
        Assert.Contains("hotkey-register-skipped", logText, StringComparison.Ordinal);
        Assert.Contains("hotkey-register-failed", logText, StringComparison.Ordinal);
        Assert.Contains(AppConstants.Audio.LogEvents.Hotkey.Execute, logText, StringComparison.Ordinal);
        Assert.Contains("hotkey=len=10 hash=", logText, StringComparison.Ordinal);
        Assert.Contains($"{AppConstants.Audio.LogEvents.Hotkey.Execute} | id={AppConstants.Hotkeys.OutputSwitchHotkeyId} description=len=", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+Alt+H", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+NotARealKey", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterMediaHotkeys_LogsDegradedDeliverySummary_WhenFallbackIsUsed()
    {
        using var loggerScope = new TestLoggerScope(nameof(RegisterMediaHotkeys_LogsDegradedDeliverySummary_WhenFallbackIsUsed), "hotkey-media-fallback.log", LogLevel.Info);
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(loggerScope.Logger, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { });
        TestPrivateAccess.SetField(service, "_logger", loggerScope.Logger);
        service.InitializeInfrastructure();

        bool registered = service.RegisterMediaHotkeys("F7", "F8", "F9", "F10");

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.True(registered);
        Assert.Contains("hotkey-register-group-delivery", logText, StringComparison.Ordinal);
        Assert.Contains("group=media", logText, StringComparison.Ordinal);
        Assert.Contains("hookOnlyFallback=4", logText, StringComparison.Ordinal);
        Assert.Contains("degraded=true", logText, StringComparison.Ordinal);
        Assert.Contains("hybrid=0", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnregisterHotkey_LogsMouseHookUninstalled_WithMouseSpecificEventName()
    {
        using var loggerScope = new TestLoggerScope(nameof(UnregisterHotkey_LogsMouseHookUninstalled_WithMouseSpecificEventName), "hotkey-mouse-uninstalled.log", LogLevel.Info);
        FakeMouseHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(loggerScope.Logger, () => _utcNowTicks),
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        TestPrivateAccess.SetField(service, "_logger", loggerScope.Logger);
        service.InitializeInfrastructure();

        Assert.True(service.RegisterDynamicHotkey(10016, "Ctrl+WheelUp", static () => { }, "Wheel Test"));
        Assert.NotNull(host);
        Assert.True(host!.IsRunning);

        service.UnregisterHotkey(10016);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains(AppConstants.Audio.LogEvents.Hotkey.MouseHookUninstalled, logText, StringComparison.Ordinal);
        Assert.DoesNotContain($"{AppConstants.Audio.LogEvents.Hotkey.KeyboardHookUninstalled} | type=WH_MOUSE_LL", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnregisterHotkey_ReleasesKeyboardFallbackHostWhenLastFallbackHotkeyIsRemoved()
    {
        FakeKeyboardHotkeyCaptureHost? host = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { },
            keyboardCaptureHostFactory: dispatch => host = new FakeKeyboardHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        Assert.True(service.RegisterDynamicHotkey(10017, "F8", static () => { }, "Keyboard Test"));

        Assert.NotNull(host);
        Assert.True(host!.IsRunning);

        service.UnregisterHotkey(10017);

        Assert.False(host.IsRunning);
        Assert.Equal(1, host.StopCount);
    }

    [Fact]
    public void RegisterMediaHotkeys_FallbackOnly_DoesNotKeepMessageHostAlive()
    {
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        FakeKeyboardHotkeyCaptureHost? keyboardHost = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409),
            unregisterHotKeyInvoker: static (_, _) => { },
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch, () => new HotkeyOsRegistrationResult(Succeeded: false, ErrorCode: 1409)),
            keyboardCaptureHostFactory: dispatch => keyboardHost = new FakeKeyboardHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        bool registered = service.RegisterMediaHotkeys("F7", "F8", "F9", "F10");

        Assert.True(registered);
        Assert.NotNull(messageHost);
        Assert.False(messageHost!.IsRunning);
        Assert.True(messageHost.StartCount >= 1);
        Assert.True(messageHost.StopCount >= 1);
        Assert.NotNull(keyboardHost);
        Assert.True(keyboardHost!.IsRunning);
    }

    [Fact]
    public void RegisterDynamicHotkey_HybridToFallback_ReleasesMessageHost()
    {
        bool osRegistrationSucceeds = true;
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        FakeKeyboardHotkeyCaptureHost? keyboardHost = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: osRegistrationSucceeds, ErrorCode: osRegistrationSucceeds ? 0 : 1409),
            unregisterHotKeyInvoker: static (_, _) => { },
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch, () => new HotkeyOsRegistrationResult(Succeeded: osRegistrationSucceeds, ErrorCode: osRegistrationSucceeds ? 0 : 1409)),
            keyboardCaptureHostFactory: dispatch => keyboardHost = new FakeKeyboardHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        Assert.True(service.RegisterDynamicHotkey(10030, "F8", static () => { }, "Lifecycle Test"));
        Assert.NotNull(messageHost);
        Assert.True(messageHost!.IsRunning);

        osRegistrationSucceeds = false;
        Assert.True(service.RegisterDynamicHotkey(10030, "F9", static () => { }, "Lifecycle Test"));

        Assert.False(messageHost.IsRunning);
        Assert.True(messageHost.StopCount >= 1);
        Assert.NotNull(keyboardHost);
        Assert.True(keyboardHost!.IsRunning);
    }

    [Fact]
    public void RegisterDynamicHotkey_FallbackToHybrid_StartsMessageHostAgain()
    {
        bool osRegistrationSucceeds = false;
        FakeKeyboardHotkeyMessageHost? messageHost = null;
        FakeKeyboardHotkeyCaptureHost? keyboardHost = null;
        using var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: new HotkeyDispatchCoordinator(Logger.Instance, () => _utcNowTicks),
            registerHotKeyInvoker: (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: osRegistrationSucceeds, ErrorCode: osRegistrationSucceeds ? 0 : 1409),
            unregisterHotKeyInvoker: static (_, _) => { },
            keyboardMessageHostFactory: dispatch => messageHost = new FakeKeyboardHotkeyMessageHost(dispatch, () => new HotkeyOsRegistrationResult(Succeeded: osRegistrationSucceeds, ErrorCode: osRegistrationSucceeds ? 0 : 1409)),
            keyboardCaptureHostFactory: dispatch => keyboardHost = new FakeKeyboardHotkeyCaptureHost(dispatch));
        service.InitializeInfrastructure();

        Assert.True(service.RegisterDynamicHotkey(10031, "F8", static () => { }, "Lifecycle Test"));
        Assert.NotNull(messageHost);
        Assert.False(messageHost!.IsRunning);
        int initialStartCount = messageHost.StartCount;

        osRegistrationSucceeds = true;
        Assert.True(service.RegisterDynamicHotkey(10031, "F9", static () => { }, "Lifecycle Test"));

        Assert.True(messageHost.IsRunning);
        Assert.True(messageHost.StartCount > initialStartCount);
        Assert.NotNull(keyboardHost);
        Assert.True(keyboardHost!.IsRunning);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    private sealed class FakeKeyboardHotkeyMessageHost(Action<int, long> dispatchHotkeyId, Func<HotkeyOsRegistrationResult>? registerResultProvider = null) : IKeyboardHotkeyMessageHost
    {
        private readonly Action<int, long> _dispatchHotkeyId = dispatchHotkeyId;
        private readonly Func<HotkeyOsRegistrationResult> _registerResultProvider = registerResultProvider ?? (() => new HotkeyOsRegistrationResult(Succeeded: true));
        private readonly HashSet<int> _registeredHotkeyIds = [];

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public bool IsRunning { get; private set; }

        public bool TryStart()
        {
            if (IsRunning)
            {
                return true;
            }

            StartCount++;
            IsRunning = true;
            return true;
        }

        public HotkeyOsRegistrationResult RegisterHotkey(int id, uint fsModifiers, uint vk)
        {
            RegisterCount++;
            HotkeyOsRegistrationResult result = _registerResultProvider();
            if (result.Succeeded)
            {
                _registeredHotkeyIds.Add(id);
            }

            return result;
        }

        public void UnregisterHotkey(int id)
        {
            UnregisterCount++;
            _registeredHotkeyIds.Remove(id);
        }

        public void Stop()
        {
            if (!IsRunning)
            {
                _registeredHotkeyIds.Clear();
                return;
            }

            StopCount++;
            IsRunning = false;
            _registeredHotkeyIds.Clear();
        }

        public void Dispose()
        {
            Stop();
        }

        public bool Trigger(int id)
        {
            if (!_registeredHotkeyIds.Contains(id))
            {
                return false;
            }

            _dispatchHotkeyId(id, Stopwatch.GetTimestamp());
            return true;
        }
    }

    private sealed class FakeMouseHotkeyCaptureHost(Action<MouseHotkeyBindingSnapshot, long> dispatchMatch) : IMouseHotkeyCaptureHost
    {
        private readonly Action<MouseHotkeyBindingSnapshot, long> _dispatchMatch = dispatchMatch;

        public MouseHotkeySnapshot Snapshot { get; private set; } = MouseHotkeySnapshot.Empty;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int UpdateCount { get; private set; }
        public bool Disposed { get; private set; }

        public bool IsRunning { get; private set; }

        public bool TryStart(MouseHotkeySnapshot snapshot)
        {
            Snapshot = snapshot;
            StartCount++;
            IsRunning = true;
            return true;
        }

        public void UpdateSnapshot(MouseHotkeySnapshot snapshot)
        {
            Snapshot = snapshot;
            UpdateCount++;
        }

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
            Snapshot = MouseHotkeySnapshot.Empty;
        }

        public void Dispose()
        {
            Disposed = true;
            Stop();
        }

        public bool Trigger(HotkeyMainInput mainInput, HotkeyModifierMask modifiers)
        {
            if (!Snapshot.TryMatch(mainInput, modifiers, out MouseHotkeyBindingSnapshot binding))
            {
                return false;
            }

            _dispatchMatch(binding, Stopwatch.GetTimestamp());
            return true;
        }
    }

    private sealed class FakeKeyboardHotkeyCaptureHost(Action<KeyboardHotkeyBindingSnapshot, long> dispatchMatch) : IKeyboardHotkeyCaptureHost
    {
        private readonly Action<KeyboardHotkeyBindingSnapshot, long> _dispatchMatch = dispatchMatch;

        public KeyboardHotkeySnapshot Snapshot { get; private set; } = KeyboardHotkeySnapshot.Empty;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int UpdateCount { get; private set; }
        public bool Disposed { get; private set; }

        public bool IsRunning { get; private set; }

        public bool TryStart(KeyboardHotkeySnapshot snapshot)
        {
            Snapshot = snapshot;
            StartCount++;
            IsRunning = true;
            return true;
        }

        public void UpdateSnapshot(KeyboardHotkeySnapshot snapshot)
        {
            Snapshot = snapshot;
            UpdateCount++;
        }

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
            Snapshot = KeyboardHotkeySnapshot.Empty;
        }

        public void Dispose()
        {
            Disposed = true;
            Stop();
        }

        public bool Trigger(HotkeyMainInput mainInput, HotkeyModifierMask modifiers)
        {
            if (!Snapshot.TryMatch(mainInput, modifiers, out KeyboardHotkeyBindingSnapshot binding))
            {
                return false;
            }

            _dispatchMatch(binding, Stopwatch.GetTimestamp());
            return true;
        }
    }
}


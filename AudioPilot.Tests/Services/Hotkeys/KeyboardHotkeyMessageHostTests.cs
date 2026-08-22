using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Hotkeys;

public sealed partial class KeyboardHotkeyMessageHostTests
{
    [Fact]
    public void StartupSignal_RemainsUsableUntilBothCallerAndWorkerReleaseOwnership()
    {
        var signal = new ThreadedWin32StartupSignal();
        signal.AddReference();
        signal.Set();

        signal.Dispose();

        Assert.True(signal.Wait(0));

        signal.Dispose();

        Assert.Throws<ObjectDisposedException>(() => signal.Wait(0));
    }

    private const int WM_HOTKEY = 0x0312;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int ErrorTimeout = 1460;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out nuint lpdwResult);

    [Fact]
    public void RegisterHotkey_WhenNativeRegistrationCallbackThrows_ReturnsContainedFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(RegisterHotkey_WhenNativeRegistrationCallbackThrows_ReturnsContainedFailure), "keyboard-message-host-register-throws.log", LogLevel.Info);
        using var host = new MessageOnlyKeyboardHotkeyHost(
            loggerScope.Logger,
            static (_, _) => { },
            static (_, _, _, _) => throw new InvalidOperationException("injected register failure"),
            static (_, _) => { });

        Assert.True(host.TryStart());

        HotkeyOsRegistrationResult result = host.RegisterHotkey(9005, 0, 0x7B);
        string logText = loggerScope.DisposeAndReadLogText();

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.ErrorCode);
        Assert.Contains("keyboard-message-window-callback-failed", logText, StringComparison.Ordinal);
        Assert.Contains("exceptionType=InvalidOperationException", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("injected register failure", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchHotkey_WhenDispatchCallbackThrows_RemainsContainedAndHostResponsive()
    {
        using var loggerScope = new TestLoggerScope(nameof(DispatchHotkey_WhenDispatchCallbackThrows_RemainsContainedAndHostResponsive), "keyboard-message-host-dispatch-throws.log", LogLevel.Info);
        IntPtr registeredWindowHandle = IntPtr.Zero;
        int dispatchCount = 0;
        using var host = new MessageOnlyKeyboardHotkeyHost(
            loggerScope.Logger,
            (_, _) =>
            {
                if (Interlocked.Increment(ref dispatchCount) == 1)
                {
                    throw new InvalidOperationException("injected dispatch failure");
                }
            },
            (windowHandle, _, _, _) =>
            {
                registeredWindowHandle = windowHandle;
                return new HotkeyOsRegistrationResult(Succeeded: true);
            },
            static (_, _) => { });

        Assert.True(host.TryStart());
        Assert.True(host.RegisterHotkey(9006, 0, 0x7C).Succeeded);

        SendHotkeyMessage(registeredWindowHandle, 9006);
        SendHotkeyMessage(registeredWindowHandle, 9006);
        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(2, Volatile.Read(ref dispatchCount));
        Assert.Contains("keyboard-message-window-callback-failed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("injected dispatch failure", logText, StringComparison.Ordinal);
    }

    private static void SendHotkeyMessage(IntPtr windowHandle, int hotkeyId)
    {
        Assert.NotEqual(
            IntPtr.Zero,
            SendMessageTimeout(
                windowHandle,
                WM_HOTKEY,
                (IntPtr)hotkeyId,
                IntPtr.Zero,
                SMTO_ABORTIFHUNG,
                (uint)AppConstants.Timing.CleanupWaitMs,
                out _));
    }

    [Fact]
    public void RegisterAndUnregister_RunOnHostThread()
    {
        int callerThreadId = Environment.CurrentManagedThreadId;
        int registerThreadId = 0;
        int unregisterThreadId = 0;
        IntPtr registeredWindowHandle = IntPtr.Zero;

        using var host = new MessageOnlyKeyboardHotkeyHost(
            Logger.Instance,
            static (_, _) => { },
            (windowHandle, _, _, _) =>
            {
                registeredWindowHandle = windowHandle;
                registerThreadId = Environment.CurrentManagedThreadId;
                return new HotkeyOsRegistrationResult(Succeeded: true);
            },
            (windowHandle, _) =>
            {
                registeredWindowHandle = windowHandle;
                unregisterThreadId = Environment.CurrentManagedThreadId;
            });

        Assert.True(host.TryStart());
        Assert.True(host.RegisterHotkey(9001, 0, 0x77).Succeeded);
        host.UnregisterHotkey(9001);

        Assert.NotEqual(IntPtr.Zero, registeredWindowHandle);
        Assert.NotEqual(callerThreadId, registerThreadId);
        Assert.Equal(registerThreadId, unregisterThreadId);
    }

    [Fact]
    public async Task DispatchesHotkeyMessage_AfterSuccessfulRegistration()
    {
        IntPtr registeredWindowHandle = IntPtr.Zero;
        var activation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = new MessageOnlyKeyboardHotkeyHost(
            Logger.Instance,
            (id, _) => activation.TrySetResult(id),
            (windowHandle, _, _, _) =>
            {
                registeredWindowHandle = windowHandle;
                return new HotkeyOsRegistrationResult(Succeeded: true);
            },
            static (_, _) => { });

        Assert.True(host.TryStart());
        Assert.True(host.RegisterHotkey(9002, 0, 0x78).Succeeded);
        Assert.NotEqual(IntPtr.Zero, registeredWindowHandle);

        Assert.NotEqual(
            IntPtr.Zero,
            SendMessageTimeout(
                registeredWindowHandle,
                WM_HOTKEY,
                (IntPtr)9002,
                IntPtr.Zero,
                SMTO_ABORTIFHUNG,
                (uint)AppConstants.Timing.CleanupWaitMs,
                out _));

        int activatedId = await activation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(9002, activatedId);
    }

    [Fact]
    public async Task RegisterDispatchAndUnregister_RemainsStableAcrossRepeatedCycles()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            IntPtr registeredWindowHandle = IntPtr.Zero;
            var activation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var host = new MessageOnlyKeyboardHotkeyHost(
                Logger.Instance,
                (id, _) => activation.TrySetResult(id),
                (windowHandle, _, _, _) =>
                {
                    registeredWindowHandle = windowHandle;
                    return new HotkeyOsRegistrationResult(Succeeded: true);
                },
                static (_, _) => { });

            Assert.True(host.TryStart());
            int hotkeyId = 9100 + iteration;
            Assert.True(host.RegisterHotkey(hotkeyId, 0, (uint)(0x70 + iteration)).Succeeded);
            Assert.NotEqual(IntPtr.Zero, registeredWindowHandle);

            Assert.NotEqual(
                IntPtr.Zero,
                SendMessageTimeout(
                    registeredWindowHandle,
                    WM_HOTKEY,
                    (IntPtr)hotkeyId,
                    IntPtr.Zero,
                    SMTO_ABORTIFHUNG,
                    (uint)AppConstants.Timing.CleanupWaitMs,
                    out _));

            int activatedId = await activation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(hotkeyId, activatedId);

            host.UnregisterHotkey(hotkeyId);
        }
    }

    [Fact]
    public void RegisterHotkey_WhenHostThreadIsHung_LogsTimeoutAndReturnsFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(RegisterHotkey_WhenHostThreadIsHung_LogsTimeoutAndReturnsFailure), "keyboard-message-host-register-timeout.log", LogLevel.Info);
        using var releaseRegistration = new ManualResetEventSlim(initialState: false);
        using var registrationFinished = new ManualResetEventSlim(initialState: false);
        using var host = new MessageOnlyKeyboardHotkeyHost(
            loggerScope.Logger,
            static (_, _) => { },
            (_, _, _, _) =>
            {
                try
                {
                    releaseRegistration.Wait(TestContext.Current.CancellationToken);
                    return new HotkeyOsRegistrationResult(Succeeded: true);
                }
                finally
                {
                    registrationFinished.Set();
                }
            },
            static (_, _) => { });

        Assert.True(host.TryStart());

        HotkeyOsRegistrationResult result = host.RegisterHotkey(9003, 0, 0x79);
        releaseRegistration.Set();
        Assert.True(registrationFinished.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        string logText = loggerScope.DisposeAndReadLogText();

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorTimeout, result.ErrorCode);
        Assert.Contains("keyboard-message-host-register-timeout", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnregisterHotkey_WhenHostThreadIsHung_LogsTimeout()
    {
        using var loggerScope = new TestLoggerScope(nameof(UnregisterHotkey_WhenHostThreadIsHung_LogsTimeout), "keyboard-message-host-unregister-timeout.log", LogLevel.Info);
        using var releaseUnregistration = new ManualResetEventSlim(initialState: false);
        using var unregistrationFinished = new ManualResetEventSlim(initialState: false);
        using var host = new MessageOnlyKeyboardHotkeyHost(
            loggerScope.Logger,
            static (_, _) => { },
            static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: true),
            (_, _) =>
            {
                try
                {
                    releaseUnregistration.Wait(TestContext.Current.CancellationToken);
                }
                finally
                {
                    unregistrationFinished.Set();
                }
            });

        Assert.True(host.TryStart());
        Assert.True(host.RegisterHotkey(9004, 0, 0x7A).Succeeded);

        host.UnregisterHotkey(9004);
        releaseUnregistration.Set();
        Assert.True(unregistrationFinished.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains("keyboard-message-host-unregister-timeout", logText, StringComparison.Ordinal);
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys;

/// <summary>Retains a consumed input's physical press until release, independently of binding snapshots.</summary>
internal sealed class HotkeyHeldInputState(HotkeyModifierMask requiredModifiers)
{
    private int _down;
    private long _generation;
    public Action<Func<bool>>? OnPressed { get; set; }
    public bool Press()
    {
        if (Interlocked.Exchange(ref _down, 1) != 0) return false;
        Interlocked.Increment(ref _generation);
        return true;
    }
    public void Release() => Volatile.Write(ref _down, 0);
    public Action CapturePressCallback()
    {
        long generation = Volatile.Read(ref _generation);
        return () => OnPressed?.Invoke(() => Volatile.Read(ref _generation) == generation && IsHeld());
    }
    public bool IsHeld() => Volatile.Read(ref _down) != 0 &&
        (requiredModifiers == HotkeyModifierMask.None ||
         (LowLevelKeyboardHotkeyThreadHost.GetActiveModifierMask() & requiredModifiers) == requiredModifiers);
}

/// <summary>Releases held inputs when Windows switches desktops, including secure prompts and the lock screen.</summary>
internal sealed partial class HotkeyDesktopSwitchMonitor : IDisposable
{
    private readonly WinEventProc _callback;
    private readonly TimerProc _timerCallback;
    private readonly Func<bool> _recover;
    private nint _handle;
    private nuint _timer;
    private bool _disposed;

    public HotkeyDesktopSwitchMonitor(Action release, Func<bool> recover)
    {
        _recover = recover;
        _timerCallback = (_, _, _, _) => TryRecover();
        _callback = (_, _, _, _, _, _, _) =>
        {
            if (_disposed) return;
            try
            {
                StopTimer();
                release();
                TryRecover();
            }
            catch (Exception ex) { Logger.Instance.Warning("HotkeyService", "held-input-desktop-release-failed", nameof(HotkeyDesktopSwitchMonitor), ex); }
        };
        _handle = SetWinEventHook(0x0020, 0x0020, 0, _callback, 0, 0, 0);
        if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not monitor desktop changes for held shortcuts.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) _ = UnhookWinEvent(handle);
        // The native hook may still call this delegate until UnhookWinEvent returns.
        GC.KeepAlive(_callback);
        GC.KeepAlive(_timerCallback);
    }

    private void TryRecover()
    {
        if (_disposed) return;
        try
        {
            if (!IsInputDesktop() || _recover())
            {
                StopTimer();
                return;
            }
            if (_timer == 0)
            {
                _timer = SetTimer(0, 0, 25, _timerCallback);
                if (_timer == 0) Logger.Instance.Warning("HotkeyService", "held-input-recovery-timer-unavailable");
            }
        }
        catch (Exception ex)
        {
            StopTimer();
            Logger.Instance.Warning("HotkeyService", "held-input-desktop-recovery-failed", nameof(TryRecover), ex);
        }
    }

    private void StopTimer()
    {
        nuint timer = _timer;
        _timer = 0;
        if (timer != 0) _ = KillTimer(0, timer);
    }

    private static unsafe bool IsInputDesktop()
    {
        nint input = OpenInputDesktop(0, false, 1);
        if (input == 0) return false;
        try
        {
            char* inputName = stackalloc char[256];
            char* threadName = stackalloc char[256];
            if (!GetUserObjectInformation(input, 2, inputName, 512, out uint inputBytes)
                || !GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, threadName, 512, out uint threadBytes))
                return false;
            return inputBytes == threadBytes && new ReadOnlySpan<char>(inputName, (int)inputBytes / 2)
                .Equals(new ReadOnlySpan<char>(threadName, (int)threadBytes / 2), StringComparison.OrdinalIgnoreCase);
        }
        finally { _ = CloseDesktop(input); }
    }

    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);
    private delegate void TimerProc(nint window, uint message, nuint timer, uint time);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint window, nuint timer, uint interval, TimerProc callback);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint window, nuint timer);

    [LibraryImport("user32.dll")]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll")]
    private static partial nint GetThreadDesktop(uint threadId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetUserObjectInformation(nint handle, int index, char* value, uint length, out uint needed);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint handle);
}

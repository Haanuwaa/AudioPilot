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
    private nint _handle;

    public HotkeyDesktopSwitchMonitor(Action release)
    {
        _callback = (_, _, _, _, _, _, _) =>
        {
            try { release(); }
            catch (Exception ex) { Logger.Instance.Warning("HotkeyService", "held-input-desktop-release-failed", nameof(HotkeyDesktopSwitchMonitor), ex); }
        };
        _handle = SetWinEventHook(0x0020, 0x0020, 0, _callback, 0, 0, 0);
        if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not monitor desktop changes for held shortcuts.");
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) _ = UnhookWinEvent(handle);
        GC.KeepAlive(_callback);
    }

    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint handle);
}

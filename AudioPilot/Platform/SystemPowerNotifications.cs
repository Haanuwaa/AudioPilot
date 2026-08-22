using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AudioPilot.Platform;

/// <summary>Receives power broadcasts independently of main-window creation and visibility.</summary>
internal sealed partial class SystemPowerNotifications : IDisposable
{
    private const int PowerBroadcastMessage = 0x0218;
    private const int Suspend = 4;
    private const int ResumeCritical = 6;
    private const int ResumeInteractive = 7;
    private const int ResumeStandby = 8;
    private const int ResumeAutomatic = 18;
    private const int DeviceNotifyWindowHandle = 0;
    private static readonly nint MessageOnlyWindow = new(-3);
    private readonly HwndSource _source;
    private readonly Action<PowerModes> _onPowerModeChanged;
    private nint _registration;

    public SystemPowerNotifications(Action<PowerModes> onPowerModeChanged)
    {
        _onPowerModeChanged = onPowerModeChanged;
        _source = new(new HwndSourceParameters("AudioPilotPowerNotifications") { ParentWindow = MessageOnlyWindow, WindowStyle = 0 });
        try
        {
            _source.AddHook(WindowProc);
            _registration = RegisterSuspendResumeNotification(_source.Handle, DeviceNotifyWindowHandle);
            if (_registration == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }

    internal static PowerModes? GetPowerMode(nint notification) => notification.ToInt64() switch
    {
        Suspend => PowerModes.Suspend,
        ResumeCritical or ResumeInteractive or ResumeStandby or ResumeAutomatic => PowerModes.Resume,
        _ => null,
    };

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != PowerBroadcastMessage || Volatile.Read(ref _registration) == 0 || GetPowerMode(wParam) is not PowerModes mode)
            return 0;
        _onPowerModeChanged(mode);
        handled = true;
        return 1;
    }

    public void Dispose()
    {
        nint registration = Interlocked.Exchange(ref _registration, 0);
        if (registration == 0) return;
        _ = UnregisterSuspendResumeNotification(registration);
        if (_source.Dispatcher.CheckAccess()) _source.Dispose();
        else _ = _source.Dispatcher.InvokeAsync(_source.Dispose, DispatcherPriority.Send);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterSuspendResumeNotification(nint recipient, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterSuspendResumeNotification(nint registration);
}

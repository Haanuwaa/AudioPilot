using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Logging;
using Microsoft.Win32;

namespace AudioPilot.Platform;

internal enum SystemPowerNotificationKind
{
    Suspend,
    ResumeAutomatic,
    ResumeInteractive,
    ResumeCritical,
}

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
    private const int DeviceNotifyCallback = 2;
    private static readonly nint MessageOnlyWindow = new(-3);
    private readonly HwndSource _source;
    private readonly Action<SystemPowerNotificationKind> _onPowerNotification;
    private nint _registration;

    public SystemPowerNotifications(Action<PowerModes> onPowerModeChanged)
        : this(kind => onPowerModeChanged(ToPowerMode(kind)))
    {
    }

    public SystemPowerNotifications(Action<SystemPowerNotificationKind> onPowerNotification)
    {
        _onPowerNotification = onPowerNotification;
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

    internal static SystemPowerNotificationKind? GetPowerNotificationKind(nint notification) => notification.ToInt64() switch
    {
        Suspend => SystemPowerNotificationKind.Suspend,
        ResumeCritical => SystemPowerNotificationKind.ResumeCritical,
        ResumeInteractive or ResumeStandby => SystemPowerNotificationKind.ResumeInteractive,
        ResumeAutomatic => SystemPowerNotificationKind.ResumeAutomatic,
        _ => null,
    };

    internal static PowerModes ToPowerMode(SystemPowerNotificationKind kind) =>
        kind == SystemPowerNotificationKind.Suspend ? PowerModes.Suspend : PowerModes.Resume;

    internal static PowerModes? GetPowerMode(nint notification) =>
        GetPowerNotificationKind(notification) is { } kind ? ToPowerMode(kind) : null;

    // Callback notifications do not depend on the WPF dispatcher or create another message window.
    internal static IDisposable RegisterCallback(Action<PowerModes> onPowerModeChanged) =>
        new CallbackRegistration(kind => onPowerModeChanged(ToPowerMode(kind)));

    internal static IDisposable RegisterCallback(Action<SystemPowerNotificationKind> onPowerNotification) =>
        new CallbackRegistration(onPowerNotification);

    // Suspend and resume share a dispatcher priority so delivery keeps their arrival order.
    internal static void QueueOnDispatcher(Dispatcher dispatcher, Logger logger, PowerModes mode, Action<PowerModes> callback) =>
        _ = AppDispatcherHelper.InvokeAsync(dispatcher, logger, () =>
        {
            callback(mode);
            return Task.CompletedTask;
        }, "Failed to process a power notification", nameof(QueueOnDispatcher), DispatcherPriority.Send);

    internal static void QueueOnDispatcher(Dispatcher dispatcher, Logger logger, SystemPowerNotificationKind kind, Action<SystemPowerNotificationKind> callback) =>
        _ = AppDispatcherHelper.InvokeAsync(dispatcher, logger, () =>
        {
            callback(kind);
            return Task.CompletedTask;
        }, "Failed to process a power notification", nameof(QueueOnDispatcher), DispatcherPriority.Send);

    private sealed class CallbackRegistration : IDisposable
    {
        private static readonly ConcurrentDictionary<nint, Action<SystemPowerNotificationKind>> Callbacks = new();
        private static long _nextId;
        private readonly nint _id;
        private nint _handle;

        internal unsafe CallbackRegistration(Action<SystemPowerNotificationKind> callback)
        {
            // Context is an opaque token, never a freed pointer. Late native callbacks safely find no subscription.
            do { _id = unchecked((nint)Interlocked.Increment(ref _nextId)); }
            while (_id == 0 || !Callbacks.TryAdd(_id, callback));
            var parameters = new CallbackParameters
            {
                Callback = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, uint>)&OnNotification,
                Context = _id,
            };
            try
            {
                _handle = RegisterSuspendResumeNotification((nint)(&parameters), DeviceNotifyCallback);
                if (_handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            catch
            {
                Callbacks.TryRemove(_id, out _);
                throw;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint OnNotification(nint context, uint notification, nint _)
        {
            // Subscribers only update atomic state or post dispatcher work. A callback already in flight may outlive disposal.
            try
            {
                if (GetPowerNotificationKind((nint)notification) is { } kind && Callbacks.TryGetValue(context, out var callback))
                    callback(kind);
            }
            catch
            {
                // Never allow a managed exception to cross an [UnmanagedCallersOnly] boundary (causes CLR FailFast).
            }
            return 0;
        }

        public void Dispose()
        {
            nint handle = Interlocked.Exchange(ref _handle, 0);
            if (handle == 0) return;
            Callbacks.TryRemove(_id, out _);
            if (!UnregisterSuspendResumeNotification(handle))
            {
                int error = Marshal.GetLastPInvokeError();
                try { Logger.Instance.Warning("SystemPowerNotifications", () => $"power-notification-unregister-failed | win32={error}"); }
                catch { }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackParameters
    {
        internal nint Callback;
        internal nint Context;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != PowerBroadcastMessage || Volatile.Read(ref _registration) == 0 || GetPowerNotificationKind(wParam) is not SystemPowerNotificationKind kind)
            return 0;
        _onPowerNotification(kind);
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterSuspendResumeNotification(nint registration);
}

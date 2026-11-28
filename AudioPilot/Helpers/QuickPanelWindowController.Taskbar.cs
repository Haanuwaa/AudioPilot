using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Windows.Interop;
using AudioPilot.Logging;

namespace AudioPilot.Helpers;

internal sealed partial class QuickPanelWindowController
{
    private const int ExtendedStyle = -20;
    private const int AppWindowStyle = 0x00040000;
    private const int ToolWindowStyle = 0x00000080;
    private const uint RefreshFrameWithoutMovingOrActivating = 0x0037;
    private const int DwmCloaked = 14;
    private const int CloakedByApplication = 1;
    private const uint InProcessServer = 1;
    private static readonly Guid ShellTaskbarClassId = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private bool? _taskbarPinned;
    private bool _useWpfTaskbar;
    private nint _manualTaskbarEntry;

    private void OnTaskbarSourceInitialized(object? sender, EventArgs e) => ApplyTaskbarMode();

    private void ApplyTaskbarMode()
    {
        bool pinned = IsPinned;
        if (_taskbarPinned == pinned) return;
        nint handle = new WindowInteropHelper(_window).Handle;
        if (handle == 0) return;

        if (!_useWpfTaskbar)
        {
            // The tool-window bit also keeps transient panels out of Alt+Tab. WPF's
            // live ShowInTaskbar setter hides and shows the HWND to update the shell.
            int style = GetWindowLong(handle, ExtendedStyle);
            int next = pinned ? (style & ~ToolWindowStyle) | AppWindowStyle : (style & ~AppWindowStyle) | ToolWindowStyle;
            if (TrySetExtendedStyle(handle, style, next))
            {
                if (!_window.IsVisible || IsAppCloaked(handle)) { _taskbarPinned = pinned; return; }
                if (TrySetTaskbarEntry(handle, pinned))
                {
                    _manualTaskbarEntry = pinned ? handle : 0;
                    _taskbarPinned = pinned;
                    return;
                }
                _ = TrySetExtendedStyle(handle, next, style);
            }

            Logger.Instance.Debug("QuickPanel", "quick-panel-taskbar-native-update-failed", nameof(ApplyTaskbarMode));
            _useWpfTaskbar = true;
            RemoveManualTaskbarEntry();
            int current = GetWindowLong(handle, ExtendedStyle);
            _ = TrySetExtendedStyle(handle, current, current & ~ToolWindowStyle);
            // Force WPF to reconcile shell membership if its property already has the desired value.
            if (_window.IsVisible && _window.ShowInTaskbar == pinned) _window.ShowInTaskbar = !pinned;
        }
        _window.ShowInTaskbar = pinned;
        _taskbarPinned = pinned;
    }

    private void RemoveManualTaskbarEntry()
    {
        if (_manualTaskbarEntry == 0) return;
        _ = TrySetTaskbarEntry(_manualTaskbarEntry, add: false);
        _manualTaskbarEntry = 0;
    }

    private static bool TrySetExtendedStyle(nint window, int oldStyle, int style)
    {
        if (oldStyle == style) return true;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLong(window, ExtendedStyle, style) == 0 && Marshal.GetLastPInvokeError() != 0) return false;
        if (SetWindowPos(window, 0, 0, 0, 0, 0, RefreshFrameWithoutMovingOrActivating)) return true;
        _ = SetWindowLong(window, ExtendedStyle, oldStyle);
        return false;
    }

    private static bool IsAppCloaked(nint window) =>
        DwmGetWindowAttribute(window, DwmCloaked, out int state, sizeof(int)) == 0 && (state & CloakedByApplication) != 0;

    private static bool TrySetTaskbarEntry(nint window, bool add)
    {
        try
        {
            if (!NativeAudioInteropHelper.ComActivator.TryCreateTyped(ShellTaskbarClassId, typeof(ITaskbarListNativeInterop).GUID,
                InProcessServer, out IActivatedNativeComObject<ITaskbarListNativeInterop>? shell, out _)) return false;
            using var connection = shell;
            var list = connection.Interface;
            return list.HrInit() >= 0 && (add ? list.AddTab(window) : list.DeleteTab(window)) >= 0;
        }
        catch (COMException) { return false; }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(nint window, int index, int style);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}

[GeneratedComInterface]
[Guid("56FDF342-FD6D-11D0-958A-006097C9A090")]
internal partial interface ITaskbarListNativeInterop
{
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(nint window);
    [PreserveSig] int DeleteTab(nint window);
}

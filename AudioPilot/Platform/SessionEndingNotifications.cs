using System.Windows.Interop;
using System.Windows.Threading;

namespace AudioPilot.Platform;

/// <summary>Listens for confirmed Windows WM_ENDSESSION broadcasts and external WM_CLOSE requests on a hidden top-level window.</summary>
internal sealed class SessionEndingNotifications : IDisposable
{
    internal const int WmClose = 0x0010;
    internal const int WmEndSession = 0x0016;
    private readonly HwndSource _source;
    private readonly Action<bool> _onEndSession;
    private readonly Action? _onCloseRequested;
    private int _closeRequested;
    private int _disposed;

    public SessionEndingNotifications(Action<bool> onEndSession, Action? onCloseRequested = null)
    {
        ArgumentNullException.ThrowIfNull(onEndSession);
        _onEndSession = onEndSession;
        _onCloseRequested = onCloseRequested;
        // Top-level (ParentWindow = 0) is required to receive broadcast WM_ENDSESSION and setup/Restart Manager WM_CLOSE messages.
        _source = new HwndSource(new HwndSourceParameters("AudioPilotSessionEnding")
        {
            ParentWindow = 0,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        try
        {
            _source.AddHook(WindowProc);
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }

    internal static bool TryParseEndSession(int message, nint wParam, out bool ending)
    {
        if (message == WmEndSession)
        {
            ending = wParam != 0;
            return true;
        }

        ending = false;
        return false;
    }

    internal static bool IsCloseRequest(int message) => message == WmClose;

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            if (TryParseEndSession(message, wParam, out bool ending))
            {
                _onEndSession(ending);
            }
            else if (IsCloseRequest(message) && _onCloseRequested != null)
            {
                handled = true;
                if (Interlocked.Exchange(ref _closeRequested, 1) == 0)
                {
                    _onCloseRequested();
                }
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_source.Dispatcher.CheckAccess())
        {
            _source.Dispose();
        }
        else if (!AppDispatcherHelper.IsDispatcherUnavailable(_source.Dispatcher))
        {
            _ = _source.Dispatcher.InvokeAsync(_source.Dispose, DispatcherPriority.Send);
        }
    }
}

using System.Buffers;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Platform;

internal readonly record struct SteamBigPictureSignalMonitorStartResult(bool Success, string Status, string? FailureReason = null);

internal enum SteamBigPictureSignalKind
{
    Unknown,
    Foreground,
    Create,
    Destroy,
    Show,
    Hide,
    NameChange,
}

internal readonly record struct SteamBigPictureSignal(
    SteamBigPictureSignalKind Kind,
    nint Hwnd,
    int ProcessId,
    string ProcessExecutablePath,
    string Title,
    string ClassName);

internal interface ISteamBigPictureSignalMonitor : IDisposable
{
    event Action<SteamBigPictureSignal>? Signaled;

    bool IsRunning { get; }

    SteamBigPictureSignalMonitorStartResult Start();
    void Stop();
}

internal sealed partial class WinEventSteamBigPictureSignalMonitor : ISteamBigPictureSignalMonitor
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;
    private const int ObjidWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const long ProcessImagePathCacheTtlMs = 5000;
    private const int MaxProcessImagePathCacheEntries = 256;

    private readonly Lock _sync = new();
    private readonly WinEventProc _callback;
    private readonly Dictionary<int, (string ExecutablePath, long ExpiryTick)> _processImagePathCache = [];
    private Thread? _worker;
    private uint _workerThreadId;
    private bool _running;
    private bool _disposed;
    private int _callbackActive;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    private delegate void WinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(
        out NativeMessage message,
        nint windowHandle,
        uint messageFilterMin,
        uint messageFilterMax,
        uint removeMessage);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessage(
        out NativeMessage message,
        nint windowHandle,
        uint messageFilterMin,
        uint messageFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    public WinEventSteamBigPictureSignalMonitor()
    {
        _callback = OnWinEvent;
    }

    public event Action<SteamBigPictureSignal>? Signaled;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    public SteamBigPictureSignalMonitorStartResult Start()
    {
        TaskCompletionSource<SteamBigPictureSignalMonitorStartResult> startupSource;
        lock (_sync)
        {
            if (_disposed)
            {
                return new SteamBigPictureSignalMonitorStartResult(false, "inactive", "monitor-disposed");
            }

            if (_running)
            {
                return new SteamBigPictureSignalMonitorStartResult(true, "active");
            }

            if (_worker != null)
            {
                return new SteamBigPictureSignalMonitorStartResult(false, "inactive", "monitor-start-pending");
            }

            startupSource = new TaskCompletionSource<SteamBigPictureSignalMonitorStartResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _worker = new Thread(() => RunHookThread(startupSource))
            {
                IsBackground = true,
                Name = "AudioPilot Steam Big Picture monitor",
            };
            _worker.Start();
        }

        return startupSource.Task.GetAwaiter().GetResult();
    }

    public void Stop()
    {
        Thread? worker;
        uint workerThreadId;
        lock (_sync)
        {
            _running = false;
            worker = _worker;
            workerThreadId = _workerThreadId;
        }

        if (worker == null)
        {
            return;
        }

        if (workerThreadId == GetCurrentThreadId())
        {
            PostQuitMessage(0);
            return;
        }

        if (workerThreadId == 0)
        {
            _ = SpinWait.SpinUntil(
                () =>
                {
                    lock (_sync)
                    {
                        workerThreadId = _workerThreadId;
                        return workerThreadId != 0 || _worker == null;
                    }
                },
                TimeSpan.FromMilliseconds(AppConstants.Timing.CleanupWaitMs));
        }

        if (workerThreadId != 0 && !PostThreadMessage(workerThreadId, WmQuit, 0, 0))
        {
            int errorCode = Marshal.GetLastPInvokeError();
            Logger.Instance.Warning(
                "SteamBigPictureSignalMonitor",
                () => $"steam-big-picture-monitor-stop-signal-failed | errorCode={errorCode}");
        }

        if (worker.IsAlive && !worker.Join(AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs))
        {
            Logger.Instance.Warning(
                "SteamBigPictureSignalMonitor",
                () => $"steam-big-picture-monitor-stop-timeout | timeoutMs={AppConstants.Timing.CleanupWaitMs + AppConstants.Timing.CleanupGraceExtensionMs}");
        }
    }

    internal bool HasWorkerForTests
    {
        get
        {
            lock (_sync)
            {
                return _worker != null;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        Signaled = null;
    }

    internal static bool ShouldSignal(uint eventType, nint hwnd, int idObject, int idChild)
    {
        if (hwnd == nint.Zero)
        {
            return false;
        }

        if (eventType == EventSystemForeground)
        {
            return true;
        }

        if (idObject != ObjidWindow || idChild != 0)
        {
            return false;
        }

        return eventType is EventObjectCreate or EventObjectDestroy or EventObjectShow or EventObjectHide or EventObjectNameChange;
    }

    internal static SteamBigPictureSignalKind GetSignalKind(uint eventType)
    {
        return eventType switch
        {
            EventSystemForeground => SteamBigPictureSignalKind.Foreground,
            EventObjectCreate => SteamBigPictureSignalKind.Create,
            EventObjectDestroy => SteamBigPictureSignalKind.Destroy,
            EventObjectShow => SteamBigPictureSignalKind.Show,
            EventObjectHide => SteamBigPictureSignalKind.Hide,
            EventObjectNameChange => SteamBigPictureSignalKind.NameChange,
            _ => SteamBigPictureSignalKind.Unknown,
        };
    }

    private void RunHookThread(TaskCompletionSource<SteamBigPictureSignalMonitorStartResult> startupSource)
    {
        var hooks = new List<nint>(capacity: 3);
        try
        {
            _ = PeekMessage(out _, 0, 0, 0, PmNoRemove);
            lock (_sync)
            {
                _workerThreadId = GetCurrentThreadId();
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            hooks.Add(CreateHook(EventSystemForeground, EventSystemForeground));
            hooks.Add(CreateHook(EventObjectCreate, EventObjectHide));
            hooks.Add(CreateHook(EventObjectNameChange, EventObjectNameChange));

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _running = true;
            }

            startupSource.TrySetResult(new SteamBigPictureSignalMonitorStartResult(true, "active"));

            while (true)
            {
                int messageResult = GetMessage(out NativeMessage message, 0, 0, 0);
                if (messageResult == 0)
                {
                    break;
                }

                if (messageResult < 0)
                {
                    int errorCode = Marshal.GetLastPInvokeError();
                    Logger.Instance.Warning(
                        "SteamBigPictureSignalMonitor",
                        () => $"steam-big-picture-message-loop-failed | errorCode={errorCode}");
                    break;
                }

                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }
        }
        catch (Exception ex)
        {
            startupSource.TrySetResult(new SteamBigPictureSignalMonitorStartResult(false, "inactive", ex.GetType().Name));
        }
        finally
        {
            _processImagePathCache.Clear();
            DisposeHooks(hooks);
            lock (_sync)
            {
                _running = false;
                _workerThreadId = 0;
                if (ReferenceEquals(_worker, Thread.CurrentThread))
                {
                    _worker = null;
                }
            }
        }
    }

    private nint CreateHook(uint eventMin, uint eventMax)
    {
        nint hook = SetWinEventHook(
            eventMin,
            eventMax,
            0,
            _callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        if (hook == nint.Zero)
        {
            int errorCode = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"Failed to register WinEvent hook for range {eventMin}-{eventMax}; errorCode={errorCode}");
        }

        return hook;
    }

    private static void DisposeHooks(IReadOnlyList<nint> hooks)
    {
        foreach (nint hook in hooks)
        {
            try
            {
                if (!UnhookWinEvent(hook))
                {
                    int errorCode = Marshal.GetLastPInvokeError();
                    Logger.Instance.Warning(
                        "SteamBigPictureSignalMonitor",
                        () => $"steam-big-picture-hook-unregister-failed | errorCode={errorCode}");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning(
                    "SteamBigPictureSignalMonitor",
                    () => $"steam-big-picture-hook-unregister-failed | reason={ex.GetType().Name}");
            }
        }
    }

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (Interlocked.Exchange(ref _callbackActive, 1) != 0)
        {
            return;
        }

        try
        {
            bool shouldRaise;
            lock (_sync)
            {
                shouldRaise = _running && !_disposed && ShouldSignal(eventType, hwnd, idObject, idChild);
            }

            if (shouldRaise)
            {
                Action<SteamBigPictureSignal>? signaled = Signaled;
                if (signaled != null)
                {
                    SteamBigPictureSignal signal = BuildSignal(eventType, hwnd);
                    if (signaled.HasSingleTarget)
                    {
                        InvokeSubscriber(signaled, signal);
                    }
                    else
                    {
                        foreach (Delegate subscriber in signaled.GetInvocationList())
                        {
                            InvokeSubscriber((Action<SteamBigPictureSignal>)subscriber, signal);
                        }
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _callbackActive, 0);
        }
    }

    private static void InvokeSubscriber(Action<SteamBigPictureSignal> subscriber, SteamBigPictureSignal signal)
    {
        try
        {
            subscriber(signal);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning(
                "SteamBigPictureSignalMonitor",
                () => $"steam-big-picture-subscriber-failed | signal={signal.Kind} reason={ex.GetType().Name}");
        }
    }

    private SteamBigPictureSignal BuildSignal(uint eventType, nint hwnd)
    {
        int processId = 0;
        string executablePath = string.Empty;
        string title = TryGetWindowTitle(hwnd) ?? string.Empty;
        string className = TryGetWindowClassName(hwnd) ?? string.Empty;

        try
        {
            GetWindowThreadProcessId(hwnd, out uint rawProcessId);
            if (rawProcessId > 0 && rawProcessId <= int.MaxValue)
            {
                processId = (int)rawProcessId;
                executablePath = ResolveCachedProcessImagePath(processId);
            }
        }
        catch
        {
        }

        return new SteamBigPictureSignal(
            GetSignalKind(eventType),
            hwnd,
            processId,
            executablePath,
            title,
            className);
    }

    private string ResolveCachedProcessImagePath(int processId)
    {
        long now = Environment.TickCount64;
        if (_processImagePathCache.TryGetValue(processId, out var cached) && now < cached.ExpiryTick)
        {
            return cached.ExecutablePath;
        }

        string resolved = ProcessEnumerationHelper.TryGetImagePath(processId) ?? string.Empty;
        if (_processImagePathCache.Count >= MaxProcessImagePathCacheEntries)
        {
            List<int>? expiredKeys = null;
            foreach (var entry in _processImagePathCache)
            {
                if (now >= entry.Value.ExpiryTick)
                {
                    (expiredKeys ??= []).Add(entry.Key);
                }
            }

            if (expiredKeys != null)
            {
                foreach (int key in expiredKeys)
                {
                    _processImagePathCache.Remove(key);
                }
            }

            if (_processImagePathCache.Count >= MaxProcessImagePathCacheEntries)
            {
                _processImagePathCache.Clear();
            }
        }

        _processImagePathCache[processId] = (resolved, now + ProcessImagePathCacheTtlMs);
        return resolved;
    }

    private static unsafe string? TryGetWindowTitle(nint hwnd)
    {
        try
        {
            Span<char> shortTitle = stackalloc char[256];
            int copiedLength;
            fixed (char* buffer = shortTitle)
            {
                copiedLength = GetWindowText(hwnd, buffer, shortTitle.Length);
            }

            if (copiedLength <= 0)
            {
                return null;
            }

            if (copiedLength < shortTitle.Length - 1)
            {
                ReadOnlySpan<char> trimmed = shortTitle[..copiedLength].Trim();
                return trimmed.IsEmpty ? null : new string(trimmed);
            }

            int capacity = Math.Clamp(GetWindowTextLength(hwnd) + 1, 512, 32768);
            char[] rented = ArrayPool<char>.Shared.Rent(capacity);
            try
            {
                fixed (char* buffer = rented)
                {
                    copiedLength = GetWindowText(hwnd, buffer, capacity);
                }

                if (copiedLength <= 0)
                {
                    return null;
                }

                ReadOnlySpan<char> trimmed = rented.AsSpan(0, copiedLength).Trim();
                return trimmed.IsEmpty ? null : new string(trimmed);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
        catch
        {
            return null;
        }
    }

    private static unsafe string? TryGetWindowClassName(nint hwnd)
    {
        try
        {
            Span<char> classBuffer = stackalloc char[256];
            int copiedLength;
            fixed (char* classBufferPointer = classBuffer)
            {
                copiedLength = GetClassName(hwnd, classBufferPointer, classBuffer.Length);
            }

            if (copiedLength <= 0)
            {
                return null;
            }

            string className = new string(classBuffer[..copiedLength]).Trim();
            return string.IsNullOrWhiteSpace(className) ? null : className;
        }
        catch
        {
            return null;
        }
    }
}

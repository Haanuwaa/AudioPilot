using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys
{
    [Flags]
    internal enum HotkeyModifierMask : byte
    {
        None = 0,
        Ctrl = 1 << 0,
        Alt = 1 << 1,
        Shift = 1 << 2,
        Win = 1 << 3,
    }

    internal readonly record struct MouseHotkeyBindingSnapshot(
        int Id,
        HotkeyMainInput MainInput,
        HotkeyModifierMask ModifierMask,
        Action? Callback,
        string Description);

    internal sealed class MouseHotkeySnapshot
    {
        public static MouseHotkeySnapshot Empty { get; } = new([]);

        private readonly Dictionary<HotkeyMainInput, MouseHotkeyBindingSnapshot[]> _bindingsByInput;

        private MouseHotkeySnapshot(Dictionary<HotkeyMainInput, MouseHotkeyBindingSnapshot[]> bindingsByInput)
        {
            _bindingsByInput = bindingsByInput;
        }

        public bool HasBindings => _bindingsByInput.Count > 0;

        public static MouseHotkeySnapshot Create(IEnumerable<MouseHotkeyBindingSnapshot> bindings)
        {
            Dictionary<HotkeyMainInput, List<MouseHotkeyBindingSnapshot>> grouped = [];
            foreach (MouseHotkeyBindingSnapshot binding in bindings)
            {
                if (!grouped.TryGetValue(binding.MainInput, out List<MouseHotkeyBindingSnapshot>? bucket))
                {
                    bucket = [];
                    grouped[binding.MainInput] = bucket;
                }

                bucket.Add(binding);
            }

            if (grouped.Count == 0)
            {
                return Empty;
            }

            Dictionary<HotkeyMainInput, MouseHotkeyBindingSnapshot[]> finalized = new(grouped.Count);
            foreach ((HotkeyMainInput mainInput, List<MouseHotkeyBindingSnapshot> bucket) in grouped)
            {
                finalized[mainInput] = [.. bucket];
            }

            return new MouseHotkeySnapshot(finalized);
        }

        public bool TryMatch(HotkeyMainInput mainInput, HotkeyModifierMask activeModifiers, out MouseHotkeyBindingSnapshot binding)
        {
            if (_bindingsByInput.TryGetValue(mainInput, out MouseHotkeyBindingSnapshot[]? candidates))
            {
                foreach (MouseHotkeyBindingSnapshot candidate in candidates)
                {
                    if (candidate.ModifierMask == activeModifiers)
                    {
                        binding = candidate;
                        return true;
                    }
                }
            }

            binding = default;
            return false;
        }
    }

    internal interface IMouseHotkeyCaptureHost : IDisposable
    {
        bool IsRunning { get; }

        bool TryStart(MouseHotkeySnapshot snapshot);

        void UpdateSnapshot(MouseHotkeySnapshot snapshot);

        void Stop();
    }

    internal sealed partial class LowLevelMouseHotkeyThreadHost : IMouseHotkeyCaptureHost
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_QUIT = 0x0012;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const int XBUTTON1 = 0x0001;
        private const int XBUTTON2 = 0x0002;
        private const uint PM_NOREMOVE = 0x0000;

        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        private static partial IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWindowsHookEx(IntPtr hhk);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr GetModuleHandle(string? lpModuleName);

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        private static partial sbyte GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TranslateMessage(in NativeMessage lpMsg);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW", SetLastError = true)]
        private static partial IntPtr DispatchMessage(in NativeMessage lpMsg);

        [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PeekMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetCurrentThreadId();

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MsllHookStruct
        {
            public int PtX;
            public int PtY;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public nuint DwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr HWnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public NativePoint Pt;
            public uint LPrivate;
        }

        private readonly Logger _logger;
        private readonly Action<MouseHotkeyBindingSnapshot, long> _dispatchMatch;
        private readonly Func<IntPtr>? _setMouseHookInvoker;
        private readonly Lock _stateLock = new();
        private readonly LowLevelMouseProc _hookProc;
        private volatile MouseHotkeySnapshot _snapshot = MouseHotkeySnapshot.Empty;
        private Thread? _worker;
        private uint _workerThreadId;
        private IntPtr _hookId;
        private bool _disposed;

        public LowLevelMouseHotkeyThreadHost(
            Logger logger,
            Action<MouseHotkeyBindingSnapshot, long> dispatchMatch,
            Func<IntPtr>? setMouseHookInvoker = null)
        {
            _logger = logger;
            _dispatchMatch = dispatchMatch;
            _setMouseHookInvoker = setMouseHookInvoker;
            _hookProc = HookCallback;
        }

        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                {
                    return _hookId != IntPtr.Zero;
                }
            }
        }

        public bool TryStart(MouseHotkeySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            UpdateSnapshot(snapshot);

            using var started = new ThreadedWin32StartupSignal();
            Exception? startupFailure = null;

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_hookId != IntPtr.Zero)
                {
                    return true;
                }

                if (_worker != null && _worker.IsAlive)
                {
                    return false;
                }

                started.AddReference();
                try
                {
                    _worker = ThreadedWin32HostHelper.StartBackgroundWorker(
                        () => WorkerLoop(started, ex => startupFailure = ex),
                        "AudioPilot.MouseHotkeys");
                }
                catch
                {
                    started.Dispose();
                    throw;
                }
            }

            return ThreadedWin32HostHelper.WaitForStartup(
                started,
                startupFailure,
                () =>
                {
                    lock (_stateLock)
                    {
                        return _hookId != IntPtr.Zero;
                    }
                },
                _logger,
                "HotkeyService",
                $"mouse-hook-start-timeout | timeoutMs={AppConstants.Timing.CleanupWaitMs}");
        }

        public void UpdateSnapshot(MouseHotkeySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            _snapshot = snapshot;
        }

        public void Stop()
        {
            Thread? worker;
            uint workerThreadId;

            lock (_stateLock)
            {
                worker = _worker;
                workerThreadId = _workerThreadId;
            }

            if (worker == null)
            {
                _snapshot = MouseHotkeySnapshot.Empty;
                return;
            }

            _snapshot = MouseHotkeySnapshot.Empty;

            ThreadedWin32HostHelper.RequestStopAndJoin(
                worker,
                workerThreadId,
                id => _ = PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero),
                _logger,
                "HotkeyService",
                $"mouse-hook-stop-timeout | timeoutMs={AppConstants.Timing.CleanupWaitMs}");
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();
        }

        private void WorkerLoop(ThreadedWin32StartupSignal started, Action<Exception?> reportStartupFailure)
        {
            IntPtr installedHookId = IntPtr.Zero;

            try
            {
                _workerThreadId = GetCurrentThreadId();
                _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

                installedHookId = InstallMouseHook();
                if (installedHookId == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    _logger.Error("HotkeyService", () => $"Failed to install global mouse hook. Win32 Error Code: {errorCode}");
                    reportStartupFailure(new Win32Exception(errorCode));
                    return;
                }

                lock (_stateLock)
                {
                    _hookId = installedHookId;
                }

                reportStartupFailure(null);
                started.Set();

                while (true)
                {
                    sbyte result = GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0);
                    if (result == 0)
                    {
                        break;
                    }

                    if (result < 0)
                    {
                        int errorCode = Marshal.GetLastWin32Error();
                        throw new Win32Exception(errorCode);
                    }

                    _ = TranslateMessage(message);
                    _ = DispatchMessage(message);
                }
            }
            catch (Exception ex)
            {
                reportStartupFailure(ex);
                if (!started.IsSet)
                {
                    started.Set();
                }

                _logger.Error("HotkeyService", "Exception while running global mouse hook host", nameof(TryStart), ex);
            }
            finally
            {
                if (installedHookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(installedHookId);
                }

                lock (_stateLock)
                {
                    _hookId = IntPtr.Zero;
                    _workerThreadId = 0;
                    _worker = null;
                }

                if (!started.IsSet)
                {
                    started.Set();
                }

                started.Dispose();
            }
        }

        private IntPtr InstallMouseHook()
        {
            if (_setMouseHookInvoker != null)
            {
                return _setMouseHookInvoker();
            }

            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule? curModule = curProcess.MainModule;
            return SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(curModule?.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            IntPtr currentHookId = _hookId;
            try
            {
                if (nCode < 0 || currentHookId == IntPtr.Zero)
                {
                    return CallNextHookEx(currentHookId, nCode, wParam, lParam);
                }

                if (!TryParseMouseHookInput(wParam, lParam, out HotkeyMainInput mainInput))
                {
                    return CallNextHookEx(currentHookId, nCode, wParam, lParam);
                }

                MouseHotkeySnapshot snapshot = _snapshot;
                if (!snapshot.TryMatch(mainInput, GetActiveModifierMask(), out MouseHotkeyBindingSnapshot binding))
                {
                    return CallNextHookEx(currentHookId, nCode, wParam, lParam);
                }

                _dispatchMatch(binding, Stopwatch.GetTimestamp());
                return (IntPtr)1;
            }
            catch (Exception ex)
            {
                TryLogHookCallbackFailure(ex);
                return TryCallNextHook(currentHookId, nCode, wParam, lParam);
            }
        }

        internal IntPtr InvokeHookCallbackForTests(int nCode, IntPtr wParam, IntPtr lParam)
            => HookCallback(nCode, wParam, lParam);

        private void TryLogHookCallbackFailure(Exception exception)
        {
            try
            {
                _logger.Error(
                    "HotkeyService",
                    () => $"mouse-hook-callback-failed | exceptionType={exception.GetType().Name}");
            }
            catch (Exception)
            {
            }
        }

        private static IntPtr TryCallNextHook(IntPtr hookId, int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return CallNextHookEx(hookId, nCode, wParam, lParam);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        internal static bool TryParseMouseHookInput(IntPtr wParam, IntPtr lParam, out HotkeyMainInput mainInput)
        {
            mainInput = HotkeyMainInput.None;

            int message = wParam.ToInt32();
            mainInput = message switch
            {
                WM_LBUTTONDOWN => HotkeyMainInput.FromMouseButton(MouseButton.Left),
                WM_RBUTTONDOWN => HotkeyMainInput.FromMouseButton(MouseButton.Right),
                WM_MBUTTONDOWN => HotkeyMainInput.FromMouseButton(MouseButton.Middle),
                _ => HotkeyMainInput.None,
            };

            if (mainInput.HasValue)
            {
                return true;
            }

            if (message is not (WM_XBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEHWHEEL))
            {
                return false;
            }

            MsllHookStruct hookData = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            mainInput = message switch
            {
                WM_XBUTTONDOWN => ((hookData.MouseData >> 16) & 0xFFFF) switch
                {
                    XBUTTON1 => HotkeyMainInput.FromMouseButton(MouseButton.XButton1),
                    XBUTTON2 => HotkeyMainInput.FromMouseButton(MouseButton.XButton2),
                    _ => HotkeyMainInput.None,
                },
                WM_MOUSEWHEEL => ResolveWheelInput(hookData.MouseData, horizontal: false),
                WM_MOUSEHWHEEL => ResolveWheelInput(hookData.MouseData, horizontal: true),
                _ => HotkeyMainInput.None,
            };

            return mainInput.HasValue;
        }

        private static HotkeyMainInput ResolveWheelInput(uint mouseData, bool horizontal)
        {
            short delta = unchecked((short)((mouseData >> 16) & ushort.MaxValue));
            if (delta == 0)
            {
                return HotkeyMainInput.None;
            }

            if (horizontal)
            {
                return delta > 0 ? HotkeyMainInput.WheelRight : HotkeyMainInput.WheelLeft;
            }

            return delta > 0 ? HotkeyMainInput.WheelUp : HotkeyMainInput.WheelDown;
        }

        internal static HotkeyModifierMask GetModifierMaskFromKeys(IEnumerable<Key> modifiers)
        {
            HotkeyModifierMask mask = HotkeyModifierMask.None;
            foreach (Key modifier in modifiers)
            {
                mask |= modifier switch
                {
                    Key.LeftCtrl or Key.RightCtrl => HotkeyModifierMask.Ctrl,
                    Key.LeftAlt or Key.RightAlt => HotkeyModifierMask.Alt,
                    Key.LeftShift or Key.RightShift => HotkeyModifierMask.Shift,
                    Key.LWin or Key.RWin => HotkeyModifierMask.Win,
                    _ => HotkeyModifierMask.None,
                };
            }

            return mask;
        }

        private static HotkeyModifierMask GetActiveModifierMask()
        {
            HotkeyModifierMask mask = HotkeyModifierMask.None;
            if (IsKeyPressed(Key.LeftCtrl) || IsKeyPressed(Key.RightCtrl))
            {
                mask |= HotkeyModifierMask.Ctrl;
            }

            if (IsKeyPressed(Key.LeftAlt) || IsKeyPressed(Key.RightAlt))
            {
                mask |= HotkeyModifierMask.Alt;
            }

            if (IsKeyPressed(Key.LeftShift) || IsKeyPressed(Key.RightShift))
            {
                mask |= HotkeyModifierMask.Shift;
            }

            if (IsKeyPressed(Key.LWin) || IsKeyPressed(Key.RWin))
            {
                mask |= HotkeyModifierMask.Win;
            }

            return mask;
        }

        private static bool IsKeyPressed(Key key)
        {
            int vKey = KeyInterop.VirtualKeyFromKey(key);
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }
    }
}

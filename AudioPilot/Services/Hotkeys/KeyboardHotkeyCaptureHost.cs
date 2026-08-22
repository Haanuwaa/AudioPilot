using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys
{
    internal readonly record struct KeyboardHotkeyBindingSnapshot(
        int Id,
        HotkeyMainInput MainInput,
        HotkeyModifierMask ModifierMask,
        Action? Callback,
        string Description);

    internal sealed class KeyboardHotkeySnapshot
    {
        public static KeyboardHotkeySnapshot Empty { get; } = new([]);

        private readonly Dictionary<HotkeyMainInput, KeyboardHotkeyBindingSnapshot[]> _bindingsByInput;

        private KeyboardHotkeySnapshot(Dictionary<HotkeyMainInput, KeyboardHotkeyBindingSnapshot[]> bindingsByInput)
        {
            _bindingsByInput = bindingsByInput;
        }

        public bool HasBindings => _bindingsByInput.Count > 0;

        public static KeyboardHotkeySnapshot Create(IEnumerable<KeyboardHotkeyBindingSnapshot> bindings)
        {
            Dictionary<HotkeyMainInput, List<KeyboardHotkeyBindingSnapshot>> grouped = [];
            foreach (KeyboardHotkeyBindingSnapshot binding in bindings)
            {
                if (!grouped.TryGetValue(binding.MainInput, out List<KeyboardHotkeyBindingSnapshot>? bucket))
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

            Dictionary<HotkeyMainInput, KeyboardHotkeyBindingSnapshot[]> finalized = new(grouped.Count);
            foreach ((HotkeyMainInput mainInput, List<KeyboardHotkeyBindingSnapshot> bucket) in grouped)
            {
                finalized[mainInput] = [.. bucket];
            }

            return new KeyboardHotkeySnapshot(finalized);
        }

        public bool TryMatch(HotkeyMainInput mainInput, HotkeyModifierMask activeModifiers, out KeyboardHotkeyBindingSnapshot binding)
        {
            if (_bindingsByInput.TryGetValue(mainInput, out KeyboardHotkeyBindingSnapshot[]? candidates))
            {
                foreach (KeyboardHotkeyBindingSnapshot candidate in candidates)
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

    internal interface IKeyboardHotkeyCaptureHost : IDisposable
    {
        bool IsRunning { get; }

        bool TryStart(KeyboardHotkeySnapshot snapshot);

        void UpdateSnapshot(KeyboardHotkeySnapshot snapshot);

        void Stop();
    }

    internal sealed partial class LowLevelKeyboardHotkeyThreadHost : IKeyboardHotkeyCaptureHost
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_QUIT = 0x0012;
        private const uint PM_NOREMOVE = 0x0000;

        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        private static partial IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

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

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdllHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
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
        private readonly Action<KeyboardHotkeyBindingSnapshot, long> _dispatchMatch;
        private readonly Func<IntPtr>? _setKeyboardHookInvoker;
        private readonly Lock _stateLock = new();
        private readonly LowLevelKeyboardProc _hookProc;
        private volatile KeyboardHotkeySnapshot _snapshot = KeyboardHotkeySnapshot.Empty;
        private Thread? _worker;
        private uint _workerThreadId;
        private IntPtr _hookId;
        private bool _disposed;

        public LowLevelKeyboardHotkeyThreadHost(
            Logger logger,
            Action<KeyboardHotkeyBindingSnapshot, long> dispatchMatch,
            Func<IntPtr>? setKeyboardHookInvoker = null)
        {
            _logger = logger;
            _dispatchMatch = dispatchMatch;
            _setKeyboardHookInvoker = setKeyboardHookInvoker;
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

        public bool TryStart(KeyboardHotkeySnapshot snapshot)
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
                        "AudioPilot.KeyboardHotkeys");
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
                $"keyboard-hook-start-timeout | timeoutMs={AppConstants.Timing.CleanupWaitMs}");
        }

        public void UpdateSnapshot(KeyboardHotkeySnapshot snapshot)
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
                _snapshot = KeyboardHotkeySnapshot.Empty;
                return;
            }

            _snapshot = KeyboardHotkeySnapshot.Empty;

            ThreadedWin32HostHelper.RequestStopAndJoin(
                worker,
                workerThreadId,
                id => _ = PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero),
                _logger,
                "HotkeyService",
                $"keyboard-hook-stop-timeout | timeoutMs={AppConstants.Timing.CleanupWaitMs}");
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

                installedHookId = InstallKeyboardHook();
                if (installedHookId == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    _logger.Error("HotkeyService", () => $"Failed to install global keyboard hook. Win32 Error Code: {errorCode}");
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

                _logger.Error("HotkeyService", "Exception while running global keyboard hook host", nameof(TryStart), ex);
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

        private IntPtr InstallKeyboardHook()
        {
            if (_setKeyboardHookInvoker != null)
            {
                return _setKeyboardHookInvoker();
            }

            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule? curModule = curProcess.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(curModule?.ModuleName), 0);
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

                if (!TryParseKeyboardHookInput(wParam, lParam, out HotkeyMainInput mainInput))
                {
                    return CallNextHookEx(currentHookId, nCode, wParam, lParam);
                }

                KeyboardHotkeySnapshot snapshot = _snapshot;
                if (!snapshot.TryMatch(mainInput, GetActiveModifierMask(), out KeyboardHotkeyBindingSnapshot binding))
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
                    () => $"keyboard-hook-callback-failed | exceptionType={exception.GetType().Name}");
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

        internal static bool TryParseKeyboardHookInput(IntPtr wParam, IntPtr lParam, out HotkeyMainInput mainInput)
        {
            mainInput = HotkeyMainInput.None;
            int message = wParam.ToInt32();
            if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            {
                return false;
            }

            KbdllHookStruct hookData = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
            if (hookData.DwExtraInfo == AppConstants.Hotkeys.SyntheticMediaInputMarker)
            {
                return false;
            }

            mainInput = HotkeyMainInput.FromKeyboard(KeyInterop.KeyFromVirtualKey(unchecked((int)hookData.VkCode)));
            return mainInput.HasValue;
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

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using AudioPilot.Logging;

namespace AudioPilot.Services.Hotkeys;

/// <summary>Observes independent raw releases because consumed hook input does not update GetAsyncKeyState.</summary>
internal sealed partial class HotkeyRawInputReleaseMonitor(bool keyboard, Action<HotkeyMainInput, uint> release) : IDisposable
{
    private bool _enabled;
    private nint _window;
    private string? _className;
    private WindowProc? _windowProc;

    internal void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        if (enabled && _window == 0)
        {
            _windowProc = WndProc;
            string name = $"AudioPilot.HeldInputRelease.{Guid.NewGuid():N}";
            nint className = Marshal.StringToHGlobalUni(name);
            try
            {
                var windowClass = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(),
                    Instance = GetModuleHandle(null),
                    Procedure = Marshal.GetFunctionPointerForDelegate(_windowProc),
                    ClassName = className
                };
                if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _className = name;
            }
            finally { Marshal.FreeHGlobal(className); }
            _window = CreateWindowEx(0, name, string.Empty, 0, 0, 0, 0, 0, (nint)(-3), 0, GetModuleHandle(null), 0);
            if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var device = new RawInputDevice
        {
            UsagePage = 1,
            Usage = keyboard ? (ushort)6 : (ushort)2,
            Flags = enabled ? 0x100u : 1u,
            Target = enabled ? _window : 0
        };
        if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not monitor held-input releases.");
        _enabled = enabled;
    }

    private unsafe nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x00FF && _enabled)
        {
            try
            {
                byte* buffer = stackalloc byte[64];
                uint size = 64;
                uint headerSize = (uint)(8 + 2 * nint.Size);
                uint read = GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize);
                if (read <= 64 && read >= headerSize)
                    ProcessPacket(new ReadOnlySpan<byte>(buffer, (int)read), (int)headerSize,
                        unchecked((uint)GetMessageTime()), !keyboard && GetSystemMetrics(23) != 0, release);
            }
            catch (Exception ex)
            {
                try { Logger.Instance.Warning("HotkeyService", "raw-input-release-failed", nameof(WndProc), ex); }
                catch (Exception) { }
            }
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    internal static void ProcessPacket(ReadOnlySpan<byte> packet, int headerSize, uint inputTime, bool swapped,
        Action<HotkeyMainInput, uint> release)
    {
        if (headerSize is not (16 or 24) || packet.Length < headerSize + 8) return;
        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        var data = packet[headerSize..];
        if (kind == 1)
        {
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            ushort key = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            if ((flags & 1) != 0 && key is > 0 and < 255)
                release(HotkeyMainInput.FromKeyboard(KeyInterop.KeyFromVirtualKey(key)), inputTime);
        }
        else if (kind == 0)
        {
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            if ((flags & 0x2) != 0) release(HotkeyMainInput.FromMouseButton(swapped ? MouseButton.Right : MouseButton.Left), inputTime);
            if ((flags & 0x8) != 0) release(HotkeyMainInput.FromMouseButton(swapped ? MouseButton.Left : MouseButton.Right), inputTime);
            if ((flags & 0x20) != 0) release(HotkeyMainInput.FromMouseButton(MouseButton.Middle), inputTime);
            if ((flags & 0x80) != 0) release(HotkeyMainInput.FromMouseButton(MouseButton.XButton1), inputTime);
            if ((flags & 0x200) != 0) release(HotkeyMainInput.FromMouseButton(MouseButton.XButton2), inputTime);
        }
    }

    public void Dispose()
    {
        try { SetEnabled(false); }
        catch (Exception ex) { Logger.Instance.Warning("HotkeyService", "raw-input-release-stop-failed", nameof(Dispose), ex); }
        finally
        {
            if (_window != 0) { _ = DestroyWindow(_window); _window = 0; }
            if (_className != null) { _ = UnregisterClass(_className, GetModuleHandle(null)); _className = null; }
            GC.KeepAlive(_windowProc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public nint Target;
    }

    private delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(ref WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClass(string name, nint instance);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices(ref RawInputDevice devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    private static unsafe partial uint GetRawInputData(nint input, uint command, void* data, ref uint size, uint headerSize);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageTime();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI;

/// <summary>Native workspace coordinates, kept separate from portable audio settings.</summary>
internal sealed record SavedWindowPlacement(int Left, int Top, int Right, int Bottom, uint Dpi, bool Maximized);

/// <summary>Restores before first presentation and coalesces settled window changes into atomic background writes.</summary>
internal sealed partial class MainWindowPlacementService(string path, Logger logger)
{
    private readonly Lock _sync = new();
    private SavedWindowPlacement? _loaded;
    private SavedWindowPlacement? _latest;
    private SavedWindowPlacement? _persisted;
    private SavedWindowPlacement? _pending;
    private Task _writeTask = Task.CompletedTask;
    private bool _writing;
    private bool _recording;
    private Window? _window;
    private DispatcherTimer? _timer;

    internal Task<SavedWindowPlacement?> LoadAsync() => Task.Run(() =>
    {
        _loaded = null;
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 4096) throw new InvalidDataException("Window placement is too large.");
            SavedWindowPlacement? saved = JsonSerializer.Deserialize<SavedWindowPlacement>(stream);
            if (saved == null || !IsValid(saved)) throw new InvalidDataException("Invalid window placement.");
            _loaded = saved;
            return saved;
        }
        catch (Exception ex) { LogFailure("load", ex); return null; }
    });

    internal static bool IsValid(SavedWindowPlacement saved) => saved.Dpi is >= 48 and <= 960
        && saved.Left is >= -1000000 and <= 1000000 && saved.Top is >= -1000000 and <= 1000000
        && (long)saved.Right - saved.Left is >= 64 and <= 32768
        && (long)saved.Bottom - saved.Top is >= 64 and <= 32768;

    internal bool TryRestore(Window window)
    {
        if (_loaded is not { } saved) return false;
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return false;
        var placement = new NativePlacement
        {
            Length = (uint)Marshal.SizeOf<NativePlacement>(),
            ShowCommand = saved.Maximized ? 3u : 4u,
            Normal = new() { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom },
        };
        if (!SetWindowPlacement(handle, in placement)) return false;
        uint dpi = GetDpiForWindow(handle);
        if (dpi != 0 && dpi != saved.Dpi)
        {
            placement.Normal.Right = saved.Left + (int)Math.Clamp(Math.Round((saved.Right - saved.Left) * (double)dpi / saved.Dpi), 64, 32768);
            placement.Normal.Bottom = saved.Top + (int)Math.Clamp(Math.Round((saved.Bottom - saved.Top) * (double)dpi / saved.Dpi), 64, 32768);
            if (!SetWindowPlacement(handle, in placement)) return false;
        }
        WindowPlacementHelper.EnsureAccessible(handle);
        if (!saved.Maximized) _ = DialogWindowHelper.TryFitToWorkArea(window, null, center: false);
        return true;
    }

    internal void Start(Window window)
    {
        if (!ReferenceEquals(_window, window))
        {
            Detach();
            _window = window;
            window.LocationChanged += WindowChanged;
            window.SizeChanged += WindowChanged;
            window.StateChanged += WindowChanged;
            _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += SaveAfterIdle;
        }
        _recording = true;
        if (Capture(window) is { } snapshot)
        {
            lock (_sync)
            {
                _latest = snapshot;
                _persisted ??= _loaded ?? snapshot;
            }
        }
    }

    internal void Pause()
    {
        if (_recording && _window is { } window) CaptureLatest(window);
        _recording = false;
        _timer?.Stop();
        _ = PersistLatestAsync();
    }

    internal Task StopAsync()
    {
        Pause();
        Detach();
        return PersistLatestAsync();
    }

    private void WindowChanged(object? sender, EventArgs args)
    {
        if (!_recording || _window is not { IsVisible: true } window) return;
        CaptureLatest(window);
        _timer?.Stop();
        _timer?.Start();
    }

    private void CaptureLatest(Window window)
    {
        if (Capture(window) is not { } snapshot) return;
        lock (_sync) _latest = snapshot;
    }

    private static SavedWindowPlacement? Capture(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        var placement = new NativePlacement { Length = (uint)Marshal.SizeOf<NativePlacement>() };
        if (handle == 0 || !GetWindowPlacement(handle, ref placement)) return null;
        var snapshot = new SavedWindowPlacement(placement.Normal.Left, placement.Normal.Top, placement.Normal.Right,
            placement.Normal.Bottom, GetDpiForWindow(handle), placement.ShowCommand == 3 || (placement.ShowCommand == 2 && (placement.Flags & 2) != 0));
        return IsValid(snapshot) ? snapshot : null;
    }

    private void SaveAfterIdle(object? sender, EventArgs args)
    {
        _timer?.Stop();
        _ = PersistLatestAsync();
    }

    internal Task PersistLatestAsync(SavedWindowPlacement? snapshot = null)
    {
        lock (_sync)
        {
            if (snapshot != null)
            {
                ArgumentOutOfRangeException.ThrowIfEqual(IsValid(snapshot), false, nameof(snapshot));
                _latest = snapshot;
            }
            if (_latest == null || (!_writing && _latest == _persisted)) return _writeTask;
            _pending = _latest;
            if (_writing) return _writeTask;
            _writing = true;
            return _writeTask = Task.Run(DrainWrites);
        }
    }

    private void DrainWrites()
    {
        while (true)
        {
            SavedWindowPlacement? snapshot;
            lock (_sync)
            {
                snapshot = _pending;
                _pending = null;
                if (snapshot == null) { _writing = false; return; }
                if (snapshot == _persisted) continue;
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
                File.Move(temporary, path, overwrite: true);
                lock (_sync) _persisted = snapshot;
            }
            catch (Exception ex) { LogFailure("save", ex); }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) { LogFailure("temporary-cleanup", ex); }
            }
        }
    }

    private void Detach()
    {
        _timer?.Stop();
        _timer?.Tick -= SaveAfterIdle;
        _timer = null;
        if (_window != null)
        {
            _window.LocationChanged -= WindowChanged;
            _window.SizeChanged -= WindowChanged;
            _window.StateChanged -= WindowChanged;
        }
        _window = null;
    }

    private void LogFailure(string operation, Exception ex) => logger.Warning("MainWindowPlacementService",
        () => $"window-placement-{operation}-failed | error={ex.GetType().Name}", operation, ex);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public uint Length; public uint Flags; public uint ShowCommand;
        public NativePoint MinPosition; public NativePoint MaxPosition; public NativeRect Normal;
    }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint handle, ref NativePlacement placement);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint handle, in NativePlacement placement);
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint handle);
}

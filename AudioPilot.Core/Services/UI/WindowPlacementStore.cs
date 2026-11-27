using System.Text.Json;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.UI;

/// <summary>Native workspace coordinates, kept separate from portable audio settings.</summary>
internal sealed record SavedWindowPlacement(int Left, int Top, int Right, int Bottom, uint Dpi, bool Maximized);

/// <summary>
/// WPF-free persistence and headless layout store for the main window placement file.
/// Extended by <c>MainWindowPlacementService</c> in the WPF host for live window tracking and restoration.
/// </summary>
internal class WindowPlacementStore(string path, Logger logger, Action<string, string>? writeFile = null)
{
    private readonly Action<string, string> _writeFile = writeFile ?? AtomicFileWriter.WriteAllText;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
    };

    protected readonly Lock Sync = new();
    protected SavedWindowPlacement? Loaded;
    protected SavedWindowPlacement? Latest;
    protected SavedWindowPlacement? Persisted;
    private SavedWindowPlacement? _pending;
    private Task _writeTask = Task.CompletedTask;
    private bool _writing;

    internal Task<SavedWindowPlacement?> LoadAsync()
    {
        lock (Sync)
        {
            if (Latest != null)
            {
                Loaded = Latest;
                return Task.FromResult<SavedWindowPlacement?>(Loaded);
            }
        }

        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > 4096) throw new InvalidDataException("Window placement is too large.");
                SavedWindowPlacement? saved = JsonSerializer.Deserialize<SavedWindowPlacement>(stream, ReadOptions);
                if (saved == null || !IsValid(saved)) throw new InvalidDataException("Invalid window placement.");
                lock (Sync)
                {
                    if (Latest != null) return Loaded = Latest;
                    return Loaded = Latest = Persisted = saved;
                }
            }
            catch (Exception ex)
            {
                LogFailure("load", ex);
                return null;
            }
        });
    }

    internal static bool IsValid(SavedWindowPlacement saved) => saved.Dpi is >= 48 and <= 960
        && saved.Left is >= -1000000 and <= 1000000 && saved.Top is >= -1000000 and <= 1000000
        && (long)saved.Right - saved.Left is >= 64 and <= 32768
        && (long)saved.Bottom - saved.Top is >= 64 and <= 32768;

    internal async Task<WindowLayoutSnapshot> GetHeadlessLayoutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SavedWindowPlacement? saved = await LoadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (saved != null) return DescribeHeadless(saved);
        return WindowPlacementHelper.ResolveLayout(null, new(Reset: true), WindowPlacementHelper.GetLayoutMonitor())
            with
        { Source = "default" };
    }

    internal async Task<WindowLayoutSnapshot> ChangeHeadlessLayoutAsync(
        WindowLayoutChange change,
        CancellationToken cancellationToken = default)
    {
        WindowPlacementHelper.ValidateLayoutChange(change);
        WindowLayoutSnapshot current = await GetHeadlessLayoutAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        WindowLayoutMonitor monitor = ResolveHeadlessMonitor(current, change);
        WindowLayoutSnapshot requested = WindowPlacementHelper.ResolveLayout(current, change, monitor);
        SavedWindowPlacement updated = ToSaved(requested, monitor);
        lock (Sync) Loaded = updated;
        await PersistLatestAsync(updated);
        lock (Sync) return requested with { Source = Persisted == updated ? "saved" : "pending", Persisted = Persisted == updated };
    }

    internal Task PersistLatestAsync(SavedWindowPlacement? snapshot = null)
    {
        lock (Sync)
        {
            if (snapshot != null)
            {
                ArgumentOutOfRangeException.ThrowIfEqual(IsValid(snapshot), false, nameof(snapshot));
                Latest = snapshot;
            }

            if (Latest == null || (!_writing && Latest == Persisted)) return _writeTask;
            _pending = Latest;
            if (_writing) return _writeTask;
            _writing = true;
            return _writeTask = Task.Run(DrainWrites);
        }
    }

    protected WindowLayoutSnapshot DescribeHeadless(SavedWindowPlacement saved)
    {
        var raw = new WindowLayoutSnapshot(saved.Left, saved.Top, saved.Right - saved.Left, saved.Bottom - saved.Top,
            (int)saved.Dpi, saved.Maximized ? "maximized" : "normal", false, "saved", false);
        WindowLayoutMonitor monitor = WindowPlacementHelper.GetLayoutMonitor(raw, 0);
        lock (Sync) return raw with
        {
            X = raw.X + monitor.WorkLeft - monitor.Left,
            Y = raw.Y + monitor.WorkTop - monitor.Top,
            Persisted = Persisted == saved,
            Source = Persisted == saved ? "saved" : "pending",
        };
    }

    protected static WindowLayoutMonitor ResolveHeadlessMonitor(WindowLayoutSnapshot current, WindowLayoutChange change)
    {
        if (change.Reset)
        {
            return WindowPlacementHelper.GetLayoutMonitor();
        }

        if (change.X.HasValue || change.Y.HasValue)
        {
            return WindowPlacementHelper.GetLayoutMonitorAt(change.X ?? current.X, change.Y ?? current.Y);
        }

        return WindowPlacementHelper.GetLayoutMonitor(current);
    }

    internal static SavedWindowPlacement ToSaved(WindowLayoutSnapshot screen, WindowLayoutMonitor monitor)
    {
        int left = screen.X - (monitor.WorkLeft - monitor.Left);
        int top = screen.Y - (monitor.WorkTop - monitor.Top);
        return new(left, top, left + screen.Width, top + screen.Height, (uint)screen.Dpi, false);
    }

    private void DrainWrites()
    {
        while (true)
        {
            SavedWindowPlacement? snapshot;
            lock (Sync)
            {
                snapshot = _pending;
                _pending = null;
                if (snapshot == null)
                {
                    _writing = false;
                    return;
                }

                if (snapshot == Persisted) continue;
            }

            try
            {
                _writeFile(path, JsonSerializer.Serialize(snapshot));
                lock (Sync) Persisted = snapshot;
            }
            catch (Exception ex)
            {
                LogFailure("save", ex);
            }
        }
    }

    private void LogFailure(string operation, Exception ex) => logger.Warning("MainWindowPlacementService",
        () => $"window-placement-{operation}-failed | error={ex.GetType().Name}", operation, ex);
}

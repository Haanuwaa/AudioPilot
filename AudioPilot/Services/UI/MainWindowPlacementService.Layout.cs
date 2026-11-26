using System.Windows;
using System.Windows.Interop;
using AudioPilot.Models;

namespace AudioPilot.Services.UI;

internal sealed partial class MainWindowPlacementService
{
    internal async Task<WindowLayoutSnapshot> GetLayoutAsync(Window? window = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (window != null && Capture(window) is { } live)
            return Describe(live, window);
        return await GetHeadlessLayoutAsync(cancellationToken);
    }

    internal async Task<WindowLayoutSnapshot> ChangeLayoutAsync(WindowLayoutChange change, Window? window = null,
        CancellationToken cancellationToken = default)
    {
        if (window == null)
        {
            return await ChangeHeadlessLayoutAsync(change, cancellationToken);
        }

        WindowPlacementHelper.ValidateLayoutChange(change);
        WindowLayoutSnapshot current = await GetLayoutAsync(window, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        nint handle = new WindowInteropHelper(window).Handle;
        WindowLayoutMonitor monitor;
        if (change.Reset)
            monitor = WindowPlacementHelper.GetLayoutMonitor(window: window is { IsVisible: true, WindowState: not WindowState.Minimized } ? handle : 0);
        else if (change.X.HasValue || change.Y.HasValue)
            monitor = WindowPlacementHelper.GetLayoutMonitorAt(change.X ?? current.X, change.Y ?? current.Y, handle);
        else
            monitor = WindowPlacementHelper.GetLayoutMonitor(current, handle);
        WindowLayoutSnapshot requested = WindowPlacementHelper.ResolveLayout(current, change, monitor);
        SavedWindowPlacement updated;
        if (handle != 0)
        {
            var mainWindow = window as MainWindow;
            var scroll = mainWindow?.CaptureLayoutScrollPosition();
            bool recording = _recording;
            _recording = false;
            _timer?.Stop();
            try
            {
                window.WindowState = WindowState.Normal;
                DialogWindowHelper.ApplyWorkAreaConstraints(window, new(monitor.WorkWidth, monitor.WorkHeight), monitor.Dpi / 96d);
                WindowPlacementHelper.ApplyLayout(handle, requested);
                window.UpdateLayout();
                updated = Capture(window) ?? throw new InvalidOperationException("The changed window placement could not be read.");
                requested = Describe(updated, window);
            }
            catch
            {
                if (Capture(window) is { } actual) _ = PersistLatestAsync(actual);
                throw;
            }
            finally
            {
                _recording = recording;
                if (scroll is { } position) mainWindow!.RestoreLayoutScrollPosition(position);
            }
        }
        else updated = ToSaved(requested, monitor);

        lock (Sync) Loaded = updated;
        await PersistLatestAsync(updated);
        lock (Sync) return requested with { Source = handle != 0 ? "live" : Persisted == updated ? "saved" : "pending", Persisted = Persisted == updated };
    }

    private WindowLayoutSnapshot Describe(SavedWindowPlacement saved, Window window)
    {
        var raw = new WindowLayoutSnapshot(saved.Left, saved.Top, saved.Right - saved.Left, saved.Bottom - saved.Top,
            (int)saved.Dpi, saved.Maximized ? "maximized" : "normal", window.IsVisible, "live", false);
        WindowLayoutMonitor monitor = WindowPlacementHelper.GetLayoutMonitor(raw, new WindowInteropHelper(window).Handle);
        lock (Sync) return raw with
        {
            X = raw.X + monitor.WorkLeft - monitor.Left,
            Y = raw.Y + monitor.WorkTop - monitor.Top,
            State = window.WindowState.ToString().ToLowerInvariant(),
            Persisted = Persisted == saved,
            Source = "live",
        };
    }
}

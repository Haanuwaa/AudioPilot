namespace AudioPilot.Tests.Helpers;

internal sealed class MediaOverlayEngineTestAdapter
{
    private readonly MediaOverlayEngine _engine;
    private MediaOverlayTrackNavigationDiagnostics? _trackNavigationDiagnostics;
    private MediaOverlayPlayPauseDiagnostics? _playPauseDiagnostics;

    public MediaOverlayEngineTestAdapter(
        Func<string?, long, CancellationToken, Task<MediaOverlaySessionSnapshot>>? currentSnapshotOverride = null,
        Func<long, CancellationToken, Task<Dictionary<string, MediaOverlaySessionSnapshot>>>? snapshotsBySourceOverride = null,
        Func<long, CancellationToken, Task<List<MediaOverlaySessionSnapshot>>>? sessionSnapshotsOverride = null,
        Func<string?, int, long, CancellationToken, Task<MediaEventAssistOutcome>>? eventWaitOverride = null,
        MediaOverlayTimingProfile? timingProfile = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _engine = new MediaOverlayEngine(
            currentSnapshotOverride,
            snapshotsBySourceOverride,
            sessionSnapshotsOverride,
            eventWaitOverride,
            timingProfile,
            diagnostics => _trackNavigationDiagnostics = diagnostics,
            diagnostics => _playPauseDiagnostics = diagnostics,
            utcNow);
    }

    public MediaOverlayEngine Engine => _engine;

    public async Task<MediaOverlayEngineTestAdapterResult> SendWithBestEffortOverlayAsync(
        MediaOverlayCommand command,
        Func<bool> sendCommand)
    {
        _trackNavigationDiagnostics = null;
        _playPauseDiagnostics = null;

        MediaOverlayResult result = await _engine.SendWithBestEffortOverlayAsync(command, sendCommand);
        return new MediaOverlayEngineTestAdapterResult(result, _trackNavigationDiagnostics, _playPauseDiagnostics);
    }

    public async Task<MediaOverlayEngineDetailedTestAdapterResult> SendWithDetailedResultAsync(
        MediaOverlayCommand command,
        Func<bool> sendCommand)
    {
        _trackNavigationDiagnostics = null;
        _playPauseDiagnostics = null;

        MediaOverlayCommandResult result = await _engine.SendWithDetailedResultAsync(command, sendCommand);
        return new MediaOverlayEngineDetailedTestAdapterResult(result, _trackNavigationDiagnostics, _playPauseDiagnostics);
    }
}

internal readonly record struct MediaOverlayEngineTestAdapterResult(
    MediaOverlayResult Result,
    MediaOverlayTrackNavigationDiagnostics? TrackNavigationDiagnostics,
    MediaOverlayPlayPauseDiagnostics? PlayPauseDiagnostics);

internal readonly record struct MediaOverlayEngineDetailedTestAdapterResult(
    MediaOverlayCommandResult Result,
    MediaOverlayTrackNavigationDiagnostics? TrackNavigationDiagnostics,
    MediaOverlayPlayPauseDiagnostics? PlayPauseDiagnostics);

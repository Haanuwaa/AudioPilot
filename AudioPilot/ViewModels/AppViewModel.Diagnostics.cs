using System.Windows.Input;
using AudioPilot.Cli;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private RecoveryHealthSnapshot? _lastResumeHealth;
    public ICommand OpenDiagnosticsCommand { get; private set; } = null!;

    private void InitializeDiagnosticsCommand()
    {
        OpenDiagnosticsCommand = TrackCommand(new RelayCommand(() =>
        {
            if (_isCleaningUp || ShutdownToken.IsCancellationRequested) return;
            DiagnosticReviewTarget? review = null;
            var window = new DiagnosticsWindow(CaptureDiagnosticsReport, SaveDiagnosticsBundleAsync, ShutdownToken,
                testProcessMonitor: RunProcessMonitorDiagnosticAsync, reviewSetting: target => review = target);
            DialogWindowHelper.ShowAppOwnedDialog(window);
            if (review != null && !_isCleaningUp && !ShutdownToken.IsCancellationRequested)
            {
                if (System.Windows.Application.Current?.MainWindow is MainWindow main && ReferenceEquals(main.DataContext, this))
                    main.ReviewDiagnosticSetting(review);
                else SelectedSettingsTabIndex = review.TabIndex;
            }
        }));
    }

    private Task<DiagnosticBundleSaveResult> SaveDiagnosticsBundleAsync(string path) => RunDiagnosticsWorkAsync(token =>
        InvokeOnDispatcherAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (_isCleaningUp || IsRuntimeShutdownRequested) throw new OperationCanceledException(token);
            bool loggingDisabled = _logger.MinimumLevel == LogLevel.None;
            var (success, _, result) = await ExportDiagnosticBundleCoreAsync(path, allowAnyPath: true,
                CliDiagnosticsExportDetailLevel.Summary, jsonOutput: false, includeSensitive: false);
            return success && result is { } export ? DiagnosticBundleSaveResult.FromExport(export, loggingDisabled) : new(false);
        }, new DiagnosticBundleSaveResult(false)), "diagnostics-ui-export");

    private Task<ProcessMonitorDiagnosticResult> RunProcessMonitorDiagnosticAsync(CancellationToken token)
    {
        Task nativeWork = Task.CompletedTask;
        return RunDiagnosticsWorkAsync(
            cancellation => ProcessMonitorDiagnosticService.RunAsync(cancellationToken: cancellation, trackWork: task => nativeWork = task),
            "diagnostics-process-monitor-test", () => nativeWork, token);
    }

    private async Task<T> RunDiagnosticsWorkAsync<T>(Func<CancellationToken, Task<T>> operation, string name,
        Func<Task>? waitForCleanup = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!TryRunBackgroundWork(async _ =>
        {
            try { token.ThrowIfCancellationRequested(); completion.TrySetResult(await operation(token)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally
            {
                // Return feedback promptly while shutdown still tracks uninterruptible native cleanup.
                if (waitForCleanup != null) await waitForCleanup();
            }
        }, name)) throw new OperationCanceledException(token);
        return await completion.Task.WaitAsync(token);
    }

    private DiagnosticsReport CaptureDiagnosticsReport()
    {
        DateTime lastWritten;
        lock (_settingsLock) lastWritten = _lastSettingsWriteTime;
        RuntimeProcessSnapshot? process = null;
        try { process = RuntimeProcessSnapshot.Capture(); }
        catch (Exception ex) { _logger.Warning("AppViewModel", "diagnostics-ui-process-counters-unavailable", nameof(CaptureDiagnosticsReport), ex); }
        var entries = _executionHistory.Value.GetEntries(limit: 30);
        return new(CaptureRuntimeHealth(), CliOutputFormatter.FormatExecutionHistory(entries, jsonOutput: false, redactOutput: true),
            lastWritten == default || lastWritten == DateTime.FromFileTime(0) ? null : lastWritten, process,
            [.. entries.Select(DiagnosticsOperation.FromEntry)]);
    }
}

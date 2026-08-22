using System.Text;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Cli;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Services.Diagnostics;
using Microsoft.Win32;

namespace AudioPilot;

/// <summary>UI-safe snapshots and recent history already redacted by the shared formatter.</summary>
internal sealed record DiagnosticsReport(RuntimeHealthSnapshot Health, string History, DateTime? SettingsLastWritten = null,
    RuntimeProcessSnapshot? Process = null, IReadOnlyList<DiagnosticsOperation>? Operations = null);

internal readonly record struct DiagnosticBundleSaveResult(bool Success, bool PartialExport = false, bool ExpectedMissingLogs = false)
{
    internal static DiagnosticBundleSaveResult FromExport(DiagnosticBundleExportResult result, bool loggingDisabled)
    {
        var missing = result.Entries.Where(static entry => entry.Status != "exported").ToArray();
        bool expected = loggingDisabled && result.CollectionIssues is not { Count: > 0 } && missing.Length > 0 && missing.All(static entry =>
            entry.SourceKind is "logs" or "current-log" or "backup-log" && entry.Status is "missing" or "missing-at-export");
        return new(true, result.PartialExport, expected);
    }
}

/// <summary>Collects a bounded, redacted diagnostic report only when requested by the user.</summary>
public partial class DiagnosticsWindow : Window
{
    private readonly Func<DiagnosticsReport> _capture;
    private readonly Func<string, Task<DiagnosticBundleSaveResult>> _export;
    private readonly AppBuildInformation _buildInformation = AppBuildInformation.Current;
    private readonly Action<string> _copyText;
    private readonly Func<bool> _openAbout;
    private readonly Func<string?> _chooseExportPath;
    private readonly Action<DiagnosticReviewTarget>? _reviewSetting;
    private readonly Func<CancellationToken, Task<ProcessMonitorDiagnosticResult>>? _testProcessMonitor;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _shutdown;
    private bool _closed;
    private bool _testingMonitor;
    private bool _exporting;
    private DiagnosticsReport? _lastReport;

    internal DiagnosticsWindow(Func<DiagnosticsReport> capture, Func<string, Task<DiagnosticBundleSaveResult>> export, CancellationToken shutdownToken,
        Action<string>? copyText = null, Func<bool>? openAbout = null,
        Func<CancellationToken, Task<ProcessMonitorDiagnosticResult>>? testProcessMonitor = null, Func<string?>? chooseExportPath = null,
        Action<DiagnosticReviewTarget>? reviewSetting = null)
    {
        _capture = capture;
        _export = export;
        _copyText = copyText ?? Clipboard.SetText;
        _testProcessMonitor = testProcessMonitor;
        _reviewSetting = reviewSetting;
        _chooseExportPath = chooseExportPath ?? ChooseExportPath;
        _openAbout = openAbout ?? (() => AboutDocumentLauncher.TryOpen(Logger.Instance, nameof(DiagnosticsWindow)));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        InitializeComponent();
        BuildSummary.Text = $"Version {_buildInformation.Version} ({_buildInformation.ProcessArchitecture.ToLowerInvariant()})";
        BuildRows.ItemsSource = _buildInformation.GetDisplayRows();
        TestProcessMonitorButton.IsEnabled = testProcessMonitor != null;
        _shutdown = shutdownToken.Register(() =>
        {
            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher)) return;
            try { _ = Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Close(); })); }
            catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher)) { }
        });
        RefreshReport();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lastReport = null;
        _previousResources = null;
        OperationsList.ItemsSource = null;
        AdviceRows.ItemsSource = null;
        OperationAdvice.Content = null;
        _lifetime.Cancel();
        _shutdown.Dispose();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshReport();

    private void CopyBuildInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested) return;
        try
        {
            _copyText(_buildInformation.ToDisplayText());
            Status.Text = "Build information copied.";
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "build-info-copy-failed", nameof(CopyBuildInfo_Click), ex);
            Status.Text = "Build information could not be copied. Try again.";
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested || _lastReport is not { } report) return;
        try
        {
            var text = new StringBuilder(_buildInformation.ToDisplayText());
            text.AppendLine().AppendLine($"Snapshot: {report.Health.CapturedUtc:O}");
            AppendRows("Runtime health", HealthRows);
            AppendRows("Hotkey registration (not a delivery test)", DetailRows);
            AppendRows("Automation and process monitor", AutomationRows);
            AppendRows("Audio defaults (cached)", AudioRows);
            AppendRows("Settings", SettingsRows);
            AppendRows("Logging", LoggingRows);
            AppendRows("Sleep/resume recovery", RecoveryRows);
            text.AppendLine().AppendLine("What to check");
            foreach (DiagnosticAdvice advice in AdviceRows.Items)
                text.AppendLine(advice.Summary).AppendLine(advice.NextStep);
            AppendRows("Process resources", ProcessRows);
            if (report.Process != null) text.AppendLine().AppendLine("Exact process counters").AppendLine(CliOutputFormatter.SerializeCliJson(report.Process));
            text.AppendLine(ResourceComparison.Text);
            text.AppendLine().AppendLine("Process monitor test").AppendLine(ProcessMonitorReport.Text);
            text.AppendLine().AppendLine("Recent operations (redacted)").Append(report.History);
            _copyText(text.ToString());
            Status.Text = "Redacted diagnostics copied. Review before sharing.";

            void AppendRows(string heading, ItemsControl control)
            {
                text.AppendLine().AppendLine(heading);
                foreach (var row in control.Items.Cast<KeyValuePair<string, string>>())
                    text.Append(row.Key).Append(": ").AppendLine(row.Value);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "diagnostics-copy-failed", nameof(CopyDiagnostics_Click), ex);
            Status.Text = "Diagnostics could not be copied. Try again.";
        }
    }

    private async void TestProcessMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _testingMonitor || _testProcessMonitor == null || _lifetime.IsCancellationRequested) return;
        CancellationToken token = _lifetime.Token;
        _testingMonitor = true;
        TestProcessMonitorButton.IsEnabled = false;
        TestProcessMonitorButton.Content = "Testing…";
        ProcessMonitorReport.Text = "Checking temporary WMI subscriptions and event delivery…";
        Task<ProcessMonitorDiagnosticResult>? pending = null;
        try
        {
            pending = _testProcessMonitor(token);
            ProcessMonitorDiagnosticResult result = await pending.WaitAsync(token);
            if (_closed || token.IsCancellationRequested) return;
            ProcessMonitorReport.Text = $"Checked {DateTime.Now:g}\n" + CliOutputFormatter.FormatProcessMonitorDiagnostic(result, jsonOutput: false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending != null) _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "process-monitor-test-failed", nameof(TestProcessMonitor_Click), ex);
            if (!_closed) ProcessMonitorReport.Text = "The process monitor test could not be completed. Try again.";
        }
        finally
        {
            _testingMonitor = false;
            if (!_closed)
            {
                TestProcessMonitorButton.Content = "Test process monitor";
                TestProcessMonitorButton.IsEnabled = true;
            }
        }
    }

    private void OpenAbout_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_closed || _lifetime.IsCancellationRequested) return;
        try
        {
            Status.Text = _openAbout() ? "Build reference opened." : "The build reference could not be opened.";
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "build-reference-open-failed", nameof(OpenAbout_Click), ex);
            Status.Text = "The build reference could not be opened.";
        }
    }

    private void RefreshReport()
    {
        if (_closed || _lifetime.IsCancellationRequested) return;
        try
        {
            DiagnosticsReport report = _capture();
            PopulateReport(report);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "diagnostics-capture-failed", nameof(RefreshReport), ex);
            Status.Text = "Diagnostics could not be collected. Try Refresh again.";
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _exporting || _lifetime.IsCancellationRequested) return;
        CancellationToken token = _lifetime.Token;
        Task<DiagnosticBundleSaveResult>? pending = null;
        _exporting = true;
        ExportButton.IsEnabled = false;
        try
        {
            string? path = _chooseExportPath();
            if (string.IsNullOrWhiteSpace(path) || _closed || token.IsCancellationRequested) return;
            Status.Text = "Saving redacted support bundle…";
            pending = _export(path);
            DiagnosticBundleSaveResult result = await pending.WaitAsync(token);
            if (!_closed && !token.IsCancellationRequested) Status.Text = !result.Success
                ? "The support bundle could not be saved. Check the destination and try again."
                : result.PartialExport && result.ExpectedMissingLogs
                    ? "Redacted support bundle saved. Logging is disabled; missing log files were expected. Review before sharing."
                    : result.PartialExport ? "Redacted support bundle saved; some diagnostic data was unavailable. See its manifest for details."
                    : "Redacted support bundle saved. Review its contents before sharing.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending != null) _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "diagnostics-export-failed", nameof(Export_Click), ex);
            if (!_closed && !token.IsCancellationRequested) Status.Text = "The support bundle could not be saved.";
        }
        finally
        {
            _exporting = false;
            if (!_closed && !token.IsCancellationRequested) ExportButton.IsEnabled = true;
        }
    }

    private string? ChooseExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save redacted support bundle",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"AudioPilot-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}

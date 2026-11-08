using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Cli;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;
using Microsoft.Win32;

namespace AudioPilot;

/// <summary>UI-safe snapshots and recent history already redacted by the shared formatter.</summary>
internal sealed record DiagnosticsReport(RuntimeHealthSnapshot Health, string History, DateTime? SettingsLastWritten = null,
    RuntimeProcessSnapshot? Process = null, IReadOnlyList<DiagnosticsOperation>? Operations = null);

internal sealed record DiagnosticsFolderActions(Func<(string Settings, string? Logs)> ReadLocations, Action<string> OpenFolder);
internal sealed record DiagnosticsAudioDefaultsCheck(DateTimeOffset CheckedUtc, AudioStatusSnapshot Status);

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
    private readonly LocalizationService _localization;
    private readonly AppBuildInformation _buildInformation = AppBuildInformation.Current;
    private readonly Action<string> _copyText;
    private readonly Func<bool> _openAbout;
    private readonly DiagnosticsFolderActions? _folderActions;
    private readonly Func<string?> _chooseExportPath;
    private readonly Action<DiagnosticReviewTarget>? _reviewSetting;
    private readonly Func<CancellationToken, Task<ProcessMonitorDiagnosticResult>>? _testProcessMonitor;
    private readonly Func<CancellationToken, Task<DiagnosticsAudioDefaultsCheck>>? _checkAudioDefaults;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _shutdown;
    private bool _closed;
    private bool _testingMonitor;
    private bool _checkingAudioDefaults;
    private bool _showAudioDefaultsResult;
    private bool _exporting;
    private DiagnosticsReport? _lastReport;
    private DiagnosticsAudioDefaultsCheck? _lastAudioDefaultsCheck;

    internal DiagnosticsWindow(Func<DiagnosticsReport> capture, Func<string, Task<DiagnosticBundleSaveResult>> export, CancellationToken shutdownToken,
        Action<string>? copyText = null, Func<bool>? openAbout = null,
        Func<CancellationToken, Task<ProcessMonitorDiagnosticResult>>? testProcessMonitor = null, Func<string?>? chooseExportPath = null,
        Action<DiagnosticReviewTarget>? reviewSetting = null, DiagnosticsFolderActions? folderActions = null,
        Func<CancellationToken, Task<DiagnosticsAudioDefaultsCheck>>? checkAudioDefaults = null, LocalizationService? localization = null)
    {
        _capture = capture;
        _export = export;
        _copyText = copyText ?? Clipboard.SetText;
        _testProcessMonitor = testProcessMonitor;
        _checkAudioDefaults = checkAudioDefaults;
        _reviewSetting = reviewSetting;
        _folderActions = folderActions;
        _chooseExportPath = chooseExportPath ?? ChooseExportPath;
        _openAbout = openAbout ?? (() => AboutDocumentLauncher.TryOpen(Logger.Instance, nameof(DiagnosticsWindow)));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        _localization = localization ?? LocalizationService.Default;
        LocalizationContext.SetService(this, _localization);
        InitializeComponent();
        PropertyChangedEventManager.AddHandler(_localization, LanguageChanged, nameof(LocalizationService.Revision));
        BuildRows.ItemsSource = _buildInformation.GetDisplayRows(_localization);
        TestProcessMonitorButton.IsEnabled = testProcessMonitor != null;
        CheckAudioDefaultsButton.IsEnabled = checkAudioDefaults != null;
        _shutdown = shutdownToken.Register(() =>
        {
            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher)) return;
            try { _ = Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Close(); })); }
            catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher)) { }
        });
        RefreshReport();
        RefreshFolders();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        PropertyChangedEventManager.RemoveHandler(_localization, LanguageChanged, nameof(LocalizationService.Revision));
        _lastReport = null;
        _previousResources = null;
        _resourceComparisonBaseline = null;
        OperationsList.ItemsSource = null;
        AdviceRows.ItemsSource = null;
        OperationAdvice.Content = null;
        _lifetime.Cancel();
        _shutdown.Dispose();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void SetStatus(LocalizedText text) => LocalizationContext.SetText(Status, TextBlock.TextProperty, text);

    private void LanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_closed) return;
        double offset = HealthScrollViewer.VerticalOffset;
        bool atBottom = HealthScrollViewer.ScrollableHeight > 0 && offset >= HealthScrollViewer.ScrollableHeight - 1;
        if (_lastReport is { } report)
        {
            PopulateReport(report, languageChanged: true);
            UpdateOperationsEmptyState(report.Operations ?? []);
        }
        if (_lastAudioDefaultsCheck is { } check && !_checkingAudioDefaults && _showAudioDefaultsResult)
            ShowAudioDefaultsResult(check);
        HealthScrollViewer.UpdateLayout();
        if (atBottom) HealthScrollViewer.ScrollToBottom();
        else HealthScrollViewer.ScrollToVerticalOffset(offset);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshReport();
        RefreshFolders();
    }

    private void RefreshFolders()
    {
        SettingsFolderButton.Visibility = LogsFolderButton.Visibility = Visibility.Collapsed;
        if (_folderActions == null || _closed) return;
        try
        {
            var (settings, logs) = _folderActions.ReadLocations();
            bool shared = logs != null && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(settings)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(logs)), StringComparison.OrdinalIgnoreCase);
            SettingsFolderButton.Content = shared ? "Open data folder" : "Open settings folder";
            SettingsFolderButton.IsEnabled = true;
            LogsFolderButton.IsEnabled = logs != null;
            SettingsFolderButton.Visibility = Visibility.Visible;
            LogsFolderButton.Visibility = logs != null && !shared ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "storage-locations-unavailable", nameof(RefreshFolders), ex);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested || _folderActions == null) return;
        try
        {
            var (settings, logs) = _folderActions.ReadLocations();
            string? path = ReferenceEquals(sender, LogsFolderButton) ? logs : settings;
            if (path == null) { SetStatus(new("Diag_NoActiveLogFolderIsAvailable")); return; }
            _folderActions.OpenFolder(path);
            SetStatus(new("Diag_FolderOpened"));
            RefreshFolders();
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "storage-folder-open-failed", nameof(OpenFolder_Click), ex);
            SetStatus(new("Diag_TheFolderCouldNotBeOpenedCheck"));
        }
    }

    private void CopyBuildInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested) return;
        try
        {
            _copyText(_buildInformation.ToDisplayText());
            SetStatus(new("Diag_BuildInformationCopied"));
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "build-info-copy-failed", nameof(CopyBuildInfo_Click), ex);
            SetStatus(new("Diag_BuildInformationCouldNotBeCopiedTry"));
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
            if (_lastAudioDefaultsCheck is { } check)
                text.AppendLine().AppendLine($"Audio defaults checked {check.CheckedUtc:O}").AppendLine(DescribeAudioDefaults(check.Status));
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
            SetStatus(new("Diag_RedactedDiagnosticsCopiedReviewBeforeSharing"));

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
            SetStatus(new("Diag_DiagnosticsCouldNotBeCopiedTryAgain"));
        }
    }

    private void ShowAudioDefaultsResult(DiagnosticsAudioDefaultsCheck check) =>
        AudioDefaultsCheckStatus.Text = _localization.FormatKey("Diag_CheckedResult", check.CheckedUtc.ToLocalTime(), DescribeAudioDefaults(check.Status));

    private async void CheckAudioDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _checkingAudioDefaults || _checkAudioDefaults == null || _lifetime.IsCancellationRequested) return;
        CancellationToken token = _lifetime.Token;
        _checkingAudioDefaults = true;
        _showAudioDefaultsResult = false;
        CheckAudioDefaultsButton.IsEnabled = false;
        AudioDefaultsCheckStatus.Visibility = Visibility.Visible;
        LocalizationContext.SetText(AudioDefaultsCheckStatus, TextBox.TextProperty, new("Diag_CheckingWindowsAudioDefaults"));
        Task<DiagnosticsAudioDefaultsCheck>? pending = null;
        try
        {
            pending = _checkAudioDefaults(token);
            var check = await pending.WaitAsync(token);
            if (_closed || token.IsCancellationRequested) return;
            _lastAudioDefaultsCheck = check;
            _showAudioDefaultsResult = true;
            ShowAudioDefaultsResult(check);
            if (_lastReport is { } report) PopulateOverview(report.Health);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending != null) _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "audio-defaults-check-failed", nameof(CheckAudioDefaults_Click), ex);
            if (!_closed && !token.IsCancellationRequested)
                LocalizationContext.SetText(AudioDefaultsCheckStatus, TextBox.TextProperty, new("Diag_TheAudioDefaultsCheckCouldNotBe"));
        }
        finally
        {
            _checkingAudioDefaults = false;
            if (!_closed && !token.IsCancellationRequested) CheckAudioDefaultsButton.IsEnabled = true;
        }
    }

    private async void TestProcessMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _testingMonitor || _testProcessMonitor == null || _lifetime.IsCancellationRequested) return;
        CancellationToken token = _lifetime.Token;
        _testingMonitor = true;
        TestProcessMonitorButton.IsEnabled = false;
        LocalizationContext.SetText(TestProcessMonitorButton, ContentControl.ContentProperty, new("Diag_TestingMonitor"));
        LocalizationContext.SetText(ProcessMonitorReport, TextBox.TextProperty, new("Diag_CheckingTemporaryWMISubscriptionsAndEventDelivery"));
        Task<ProcessMonitorDiagnosticResult>? pending = null;
        try
        {
            pending = _testProcessMonitor(token);
            ProcessMonitorDiagnosticResult result = await pending.WaitAsync(token);
            if (_closed || token.IsCancellationRequested) return;
            LocalizationContext.SetText(ProcessMonitorReport, TextBox.TextProperty,
                new("Diag_CheckedResult", DateTime.Now, CliOutputFormatter.FormatProcessMonitorDiagnostic(result, jsonOutput: false)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending != null) _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "process-monitor-test-failed", nameof(TestProcessMonitor_Click), ex);
            if (!_closed) LocalizationContext.SetText(ProcessMonitorReport, TextBox.TextProperty, new("Diag_TheProcessMonitorTestCouldNotBe"));
        }
        finally
        {
            _testingMonitor = false;
            if (!_closed)
            {
                LocalizationContext.SetText(TestProcessMonitorButton, ContentControl.ContentProperty, new("Ui_TestProcessMonitor"));
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
            SetStatus(new(_openAbout() ? "Diag_BuildReferenceOpened" : "Diag_TheBuildReferenceCouldNotBeOpened"));
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "build-reference-open-failed", nameof(OpenAbout_Click), ex);
            SetStatus(new("Diag_TheBuildReferenceCouldNotBeOpened"));
        }
    }

    private void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested) return;
        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(Constants.AppConstants.Links.RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "project-link-open-failed", nameof(OpenRepository_Click), ex);
            SetStatus(new("Diag_TheProjectRepositoryCouldNotBeOpened"));
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
            SetStatus(new("Diag_DiagnosticsCouldNotBeCollectedTryRefresh"));
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
            SetStatus(new("Diag_SavingRedactedSupportBundle"));
            pending = _export(path);
            DiagnosticBundleSaveResult result = await pending.WaitAsync(token);
            if (!_closed && !token.IsCancellationRequested) SetStatus(new(!result.Success
                ? "Diag_TheSupportBundleCouldNotBeSaved"
                : result.PartialExport && result.ExpectedMissingLogs
                    ? "Diag_RedactedSupportBundleSavedLoggingIsDisabled"
                    : result.PartialExport ? "Diag_RedactedSupportBundleSavedSomeDiagnosticData"
                    : "Diag_RedactedSupportBundleSavedReviewItsContents"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending != null) _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "diagnostics-export-failed", nameof(Export_Click), ex);
            if (!_closed && !token.IsCancellationRequested) SetStatus(new("Diag_TheSupportBundleCouldNotBeSavedText"));
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
            Title = _localization["Diag_SaveRedactedSupportBundle"],
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"AudioPilot-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}

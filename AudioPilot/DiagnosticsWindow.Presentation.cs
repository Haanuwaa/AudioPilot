using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot;

/// <summary>A bounded display projection created through the CLI's existing redaction path.</summary>
internal sealed record DiagnosticsOperation(string Id, DateTimeOffset Timestamp, string Heading, string Context, bool Issue, string Details,
    DiagnosticAdvice? Advice = null)
{
    public string LocalTime => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    internal static DiagnosticsOperation FromEntry(ExecutionHistoryEntry entry)
    {
        var value = CliOutputFormatter.CreateExecutionHistorySnapshot(entry, redactOutput: true);
        string outcome = value.Outcome switch { "success" => "Succeeded", "awaiting-app-audio" => "Waiting for app audio", "skipped" => "Skipped", "unconfirmed" => "Unconfirmed", "not-completed" => "Not completed", _ => "Failed" };
        string context = value.Source;
        if (value.ElapsedMs is { } elapsed) context += $" · {elapsed:N1} ms";
        if (!string.IsNullOrWhiteSpace(value.Summary))
        {
            string summary = value.Summary;
            if (summary.Length > 200) summary = summary[..(char.IsHighSurrogate(summary[199]) ? 199 : 200)] + "…";
            context += " · " + summary;
        }
        return new(value.OpId, DateTimeOffset.Parse(value.TimestampUtc, CultureInfo.InvariantCulture),
            value.Action.Replace('-', ' ') + " · " + outcome, context, !value.Success && !value.Skipped,
            CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput: false, redactOutput: true),
            DiagnosticAdvice.ForOperation(value.DiagCode, value.Kind, value.Action, !value.Success && !value.Skipped));
    }
}

public partial class DiagnosticsWindow
{
    private RuntimeProcessSnapshot? _previousResources;

    private void PopulateReport(DiagnosticsReport report)
    {
        RuntimeHealthSnapshot health = report.Health;
        var advice = DiagnosticAdvice.ForHealth(health);
        AdviceRows.ItemsSource = advice;
        NoAdvice.Visibility = advice.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GuidanceDetails.Visibility = advice.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatedText.Text = $"Snapshot captured {health.CapturedUtc.ToLocalTime():G}";
        int registrationIssues = health.Hotkeys?.Registrations.Count(static key => key.LastOutcome is not ("None" or "Registered" or "Fallback")) ?? 0;
        string shortcuts = health.Hotkeys is not { } keys ? "Unavailable"
            : keys.ShuttingDown ? "Stopping" : registrationIssues > 0 ? $"{registrationIssues} registration {(registrationIssues == 1 ? "issue" : "issues")}; {keys.BindingCount} bindings"
            : !keys.Initialized ? "Not initialized"
            : keys.CaptureSuspended ? "Shortcut editing in progress" : $"{keys.BindingCount} bindings; no registration issues recorded";
        HealthRows.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("Hotkeys", shortcuts),
            new("Audio defaults", health.Audio is { } audio ? $"Output: {audio.OutputDefault.ToLowerInvariant()}; input: {audio.InputDefault.ToLowerInvariant()}"
                + (audio.RefreshPending ? " (refresh pending)" : audio.Stale ? " (cached sample is stale)" : "") : "Unavailable"),
            new("Automatic routines", AutomationState(health.RoutineWork)),
            new("Settings", health.Settings is { } settings ? settings.LastSaveSucceeded == false ? "Last save failed; see details"
                : settings.SavePending ? "Save pending" : settings.LastSaveSucceeded == true ? "Last save succeeded" : "No save attempt recorded" : "Unavailable"),
            new("Logging", health.Logging is { } logging ? logging.Level == "None" ? "Disabled"
                : logging.LastWriteSucceeded == false ? "Last file write failed; see details" : $"{logging.Level}; {health.DroppedLogMessages} dropped messages" : $"{health.DroppedLogMessages} dropped messages"),
        };
        if (health.Recovery is { Succeeded: false })
            ((List<KeyValuePair<string, string>>)HealthRows.ItemsSource).Add(new("Resume recovery", "Incomplete; see recovery details"));
        var details = new List<KeyValuePair<string, string>>();
        if (health.Hotkeys is { } hotkeys)
        {
            details.Add(new("Shortcut editing", hotkeys.CaptureSuspended ? "In progress" : "Not active"));
            details.Add(new("Message host", hotkeys.KeyboardMessageHostRunning ? "Running" : "Inactive"));
            details.Add(new("Keyboard hook", hotkeys.KeyboardHookRunning ? "Running" : "Inactive"));
            details.Add(new("Mouse hook", hotkeys.MouseHookRunning ? "Running" : "Inactive"));
            details.Add(new("Fallback registrations", hotkeys.Registrations.Count(static key => key.LastOutcome == "Fallback").ToString()));
            details.Add(new("Registration issues", registrationIssues == 0 ? "None recorded" : registrationIssues.ToString()));
            foreach (var problem in hotkeys.Registrations.Where(static key => key.LastOutcome is not ("None" or "Registered" or "Fallback")))
                details.Add(new(Services.Hotkeys.HotkeyDisplayFormatter.FormatActionName(problem.ActionId),
                    problem.LastOutcome + (problem.Win32Error == 0 ? "" : $" (Windows error {problem.Win32Error})")));
        }
        DetailRows.ItemsSource = details;
        AutomationRows.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("Automatic routines", AutomationState(health.RoutineWork)),
            new("Process monitor", health.ProcessMonitorMode),
            new("Fallback reason", health.ProcessMonitorFallbackReason ?? "None recorded"),
            new("Routine work", health.RoutineWork is { } work ? $"{work.ActiveTasks} active, {work.DeferredOperations} queued" : "Unavailable"),
            new("Debounce slots", health.RoutineWork is { } pending ? string.Join(", ", pending.DebounceSlots.DefaultIfEmpty("None")) : "Unavailable"),
        };
        AudioRows.ItemsSource = health.Audio is { } devices ? new List<KeyValuePair<string, string>>
        {
            new("Output default", devices.OutputDefault), new("Input default", devices.InputDefault),
            new("Output roles", Roles(devices.OutputRolesSplit)), new("Input roles", Roles(devices.InputRolesSplit)),
            new("Cache refresh", devices.RefreshPending ? "Pending" : "Not pending"),
            new("Last successful refresh", When(devices.LastRefreshedUtc)),
            new("Cached sample", devices.LastRefreshedUtc == null ? "Not sampled" : devices.Stale ? "Stale; values may have changed" : "Within cache lifetime"),
        } : [new KeyValuePair<string, string>("Audio defaults", "Unavailable")];
        SettingsRows.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("Settings last written", report.SettingsLastWritten?.ToString("g") ?? "Unavailable"),
        };
        if (health.Settings is { } save)
            SettingsRows.ItemsSource = ((IEnumerable<KeyValuePair<string, string>>)SettingsRows.ItemsSource).Concat(
            [
                new("Auto-save", save.AutoSaveEnabled ? "Enabled" : "Disabled"), new("Save pending", save.SavePending ? "Yes" : "No"),
                new("Storage", save.UsingFallback ? "Fallback location" : "Primary location"),
                new("Last save attempt", When(save.LastSaveAttemptUtc)), new("Save result", Outcome(save.LastSaveSucceeded)),
                new("Save error", save.LastSaveError ?? "None recorded"), new("Settings recovery", save.RecoveryWarning ? "Load/recovery warning recorded" : "No warning recorded"),
            ]).ToArray();
        LoggingRows.ItemsSource = health.Logging is { } log ? new List<KeyValuePair<string, string>>
        {
            new("Level", log.Level), new("Sensitive log redaction", log.RedactionEnabled ? "Enabled" : "Disabled"),
            new("Queued messages", log.QueuedMessages.ToString()), new("Dropped messages", health.DroppedLogMessages.ToString()),
            new("Last file write", When(log.LastWriteAttemptUtc)), new("File write result", Outcome(log.LastWriteSucceeded)),
            new("File write error", log.LastWriteError ?? "None recorded"),
        } : [new KeyValuePair<string, string>("Logging state", "Unavailable")];
        RecoveryRows.ItemsSource = health.Recovery is { } recovery ? new List<KeyValuePair<string, string>>
        {
            new("Last completed", When(recovery.CompletedUtc)), new("Result", recovery.Succeeded ? "Succeeded" : "Incomplete"),
            new("Hotkey attempts", recovery.HotkeyAttempts.ToString()), new("Hotkey failures", recovery.HotkeyFailedCount.ToString()),
        } : [new KeyValuePair<string, string>("Recovery", "No completed recovery in this session")];
        PopulateResources(report.Process);
        string? selectedId = (OperationsList.SelectedItem as DiagnosticsOperation)?.Id;
        _lastReport = report;
        Report.Text = report.History;
        ApplyHistoryFilter(selectedId);
        CopyDiagnosticsButton.IsEnabled = true;
        Status.Text = "Redacted current-session snapshot and support bundles. Review before sharing.";

        static string Roles(bool? split) => split == true ? "Different defaults (can be intentional)" : split == false ? "Same default across roles" : "No complete role sample";
        static string When(DateTimeOffset? time) => time?.ToLocalTime().ToString("G") ?? "Not recorded in this session";
        static string Outcome(bool? result) => result == true ? "Succeeded" : result == false ? "Failed" : "Not recorded";
    }

    private static string AutomationState(RoutineWorkSnapshot? routines) => routines == null ? "Unavailable"
        : routines.AutomaticPaused ? routines.AutomaticResumeUtc is { } time ? $"Paused until {time.ToLocalTime():g}" : "Paused until resumed"
        : routines.HasAutomaticRoutines == false ? "None configured" : routines.HasAutomaticRoutines == true ? "Active" : "Not paused";

    private void PopulateResources(RuntimeProcessSnapshot? process)
    {
        ProcessRows.ItemsSource = process is { } current ? new List<KeyValuePair<string, string>>
        {
            new("Uptime", current.Uptime is { } uptime ? uptime.ToString(@"d\.hh\:mm\:ss") : "Unavailable"),
            new("Private memory", Memory(current.PrivateBytes)), new("Managed heap", Memory(current.ManagedBytes)), new("Working set", Memory(current.WorkingSetBytes)),
            new("Handles", current.Handles.ToString()), new("Threads", current.Threads.ToString()), new("Process ID", current.ProcessId.ToString()),
            new("Architecture", current.Architecture), new("Allocated since start", Memory(current.AllocatedBytes)),
            new("GC collections", $"Gen 0: {current.Gen0Collections}; Gen 1: {current.Gen1Collections}; Gen 2: {current.Gen2Collections}"),
        } : [new KeyValuePair<string, string>("Process counters", "Unavailable")];
        ResourceComparison.Text = process is { } next && _previousResources is { } previous && next.ProcessId == previous.ProcessId
            ? $"Since previous Refresh: private memory {(next.PrivateBytes - previous.PrivateBytes) / 1048576d:+0.0;-0.0;0.0} MiB; managed heap {(next.ManagedBytes - previous.ManagedBytes) / 1048576d:+0.0;-0.0;0.0} MiB; handles {next.Handles - previous.Handles:+0;-0;0}."
            : "Refresh to compare memory and handles with this sample.";
        _previousResources = process;
        static string Memory(long bytes) => $"{bytes / 1048576d:N1} MiB";
    }

    private void HistoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_closed && _lastReport != null) ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter(string? selectedId = null)
    {
        selectedId ??= (OperationsList.SelectedItem as DiagnosticsOperation)?.Id;
        var entries = _lastReport?.Operations ?? [];
        var visible = entries.Where(operation => HistoryFilter.SelectedIndex != 1 || operation.Issue).ToArray();
        OperationsList.ItemsSource = visible;
        if (_lastReport?.Operations != null) Report.Text = "Select an operation to inspect its redacted details.";
        OperationsList.Visibility = visible.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyOperations.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyOperations.Text = HistoryFilter.SelectedIndex == 1 ? "No issues in the latest operations." : "No recent operations recorded.";
        if (selectedId != null)
        {
            OperationsList.SelectedItem = visible.FirstOrDefault(operation => operation.Id == selectedId);
            if (OperationsList.SelectedItem is DiagnosticsOperation selected) Report.Text = selected.Details;
        }
        UpdateSelectedOperation();
    }

    private void OperationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_closed && CopyOperationButton != null) UpdateSelectedOperation();
    }

    private void UpdateSelectedOperation()
    {
        bool hasSelection = OperationsList.SelectedItem is DiagnosticsOperation;
        CopyOperationButton.IsEnabled = hasSelection;
        OperationDetails.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        OperationAdvice.Content = (OperationsList.SelectedItem as DiagnosticsOperation)?.Advice;
        if (!_closed && OperationsList.SelectedItem is DiagnosticsOperation operation) Report.Text = operation.Details;
        else if (!_closed && _lastReport?.Operations != null) Report.Text = "Select an operation to inspect its redacted details.";
    }

    private void CopyOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested || OperationsList.SelectedItem is not DiagnosticsOperation operation) return;
        try
        {
            _copyText("AudioPilot operation (redacted)" + Environment.NewLine + operation.Details);
            Status.Text = "Redacted operation copied. Review before sharing.";
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "diagnostics-operation-copy-failed", nameof(CopyOperation_Click), ex);
            Status.Text = "The operation could not be copied. Try again.";
        }
    }

    private void ReviewSetting_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested || sender is not FrameworkElement { DataContext: DiagnosticAdvice { Target: { } target } }) return;
        if (_reviewSetting == null) { Status.Text = "Open AudioPilot's main window to review this setting."; return; }
        try { _reviewSetting(target); Close(); }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "diagnostics-review-setting-failed", nameof(ReviewSetting_Click), ex);
            if (!_closed) Status.Text = "The setting could not be opened. Try again.";
        }
    }
}

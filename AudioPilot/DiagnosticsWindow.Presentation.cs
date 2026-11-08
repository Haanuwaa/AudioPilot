using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.Cli;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot;

/// <summary>A bounded display projection created through the CLI's existing redaction path.</summary>
internal sealed record DiagnosticsOperation(string Id, DateTimeOffset Timestamp, string Heading, string Context, bool Issue, string Details,
    DiagnosticAdvice? Advice = null)
{
    public string LocalTime => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public Func<LocalizationService, string> HeadingPresentation => FormatHeading;
    public Func<LocalizationService, string> AccessibleHeading => localization => $"{LocalTime}, {FormatHeading(localization)}";

    private string FormatHeading(LocalizationService localization)
    {
        int separator = Heading.LastIndexOf(" · ", StringComparison.Ordinal);
        if (separator < 0) return AppMessages.Translate(Heading, localization);
        return AppMessages.Translate(Heading[..separator], localization) + " · " + AppMessages.Translate(Heading[(separator + 3)..], localization);
    }

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
    private RuntimeProcessSnapshot? _resourceComparisonBaseline;

    private static void SetRows(ItemsControl target, IEnumerable<KeyValuePair<string, string>> rows)
    {
        if (target.ItemsSource is IEnumerable<KeyValuePair<string, string>> previous && previous.SequenceEqual(rows)) return;
        target.ItemsSource = rows;
    }

    private void PopulateOverview(RuntimeHealthSnapshot health)
    {
        var audio = health.Audio;
        var check = _lastAudioDefaultsCheck is { } last && !(audio?.LastRefreshedUtc > last.CheckedUtc) ? last : null;
        var advice = DiagnosticAdvice.ForHealth(health, check?.Status, _localization);
        AdviceRows.ItemsSource = advice;
        NoAdvice.Visibility = advice.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GuidanceDetails.Visibility = advice.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GuidanceDetails.Header = _localization.FormatKey("Diag_GuidanceCount", advice.Count);
        int registrationIssues = CountRegistrationIssues(health.Hotkeys);
        string bindings = health.Hotkeys is { BindingCount: var count } ? _localization.FormatKey(count == 1 ? "Diag_SingleBinding" : "Diag_BindingCount", count) : string.Empty;
        string shortcuts = health.Hotkeys is not { } keys ? _localization["Tray_Unavailable"]
            : keys.ShuttingDown ? _localization["Diag_Stopping"] : registrationIssues > 0 ? _localization.FormatKey(registrationIssues == 1 ? "Diag_SingleRegistrationIssue" : "Diag_RegistrationIssues", registrationIssues, bindings)
            : !keys.Initialized ? _localization["Diag_NotInitialized"]
            : keys.CaptureSuspended ? _localization["Diag_ShortcutEditingInProgress"] : _localization.FormatKey("Diag_BindingsHealthy", bindings);
        string audioDefaults = check != null ? _localization.FormatKey("Diag_OnDemand", DescribeAudioDefaults(check.Status))
            : audio == null ? _localization["Tray_Unavailable"]
            : audio.OutputDefault == "Not sampled" && audio.InputDefault == "Not sampled" ? _localization["Diag_NotYetSampled"]
            : audio.Stale ? _localization.FormatKey("Diag_LastKnown", DescribeAudioDefaults(audio.OutputDefault, audio.InputDefault))
            : DescribeAudioDefaults(audio.OutputDefault, audio.InputDefault);
        if (check == null && audio is { RefreshPending: true }) audioDefaults += _localization["Diag_RefreshPending"];
        List<KeyValuePair<string, string>> rows =
        [
            new(_localization["Diag_Hotkeys"], shortcuts),
            new(_localization["Ui_AudioDefaults"], audioDefaults),
            new(_localization["Tray_AutomaticRoutines"], AutomationState(health.RoutineWork)),
            new(_localization["Common_Settings"], health.Settings is { } settings ? settings.LastSaveSucceeded == false ? _localization["Diag_LastSaveFailedSeeDetails"]
                : settings.SavePending ? _localization["Diag_SavePending"] : settings.LastSaveSucceeded == true ? _localization["Diag_LastSaveSucceeded"] : _localization["Diag_NoSaveAttemptRecorded"] : _localization["Tray_Unavailable"]),
            new(_localization["Ui_Logging"], health.Logging is { } logging ? logging.Level == "None" ? _localization["Ui_Disabled"]
                : logging.LastWriteSucceeded == false ? _localization["Diag_LastFileWriteFailedSeeDetails"] : _localization.FormatKey("Diag_DroppedWithLevel", logging.Level, health.DroppedLogMessages) : _localization.FormatKey("Diag_DroppedCount", health.DroppedLogMessages)),
        ];
        if (health.Recovery is { Succeeded: false })
            rows.Add(new(_localization["Diag_ResumeRecovery"], _localization["Diag_IncompleteSeeRecoveryDetails"]));
        SetRows(HealthRows, rows);
    }

    private static int CountRegistrationIssues(HotkeyRuntimeSnapshot? hotkeys) =>
        hotkeys?.Registrations.Count(static key => key.LastOutcome is not ("None" or "Registered" or "Fallback")) ?? 0;

    private string DescribeAudioDefaults(AudioStatusSnapshot status) => DescribeAudioDefaults(Describe(status.Output.Kind), Describe(status.Input.Kind));

    private static string Describe(AudioEndpointStatusKind kind) => kind switch
    {
        AudioEndpointStatusKind.Available => "available",
        AudioEndpointStatusKind.NoDevice => "no active default",
        _ => "could not be checked",
    };

    private string DescribeAudioDefaults(string output, string input) =>
        string.Equals(output, "available", StringComparison.OrdinalIgnoreCase) && string.Equals(input, "available", StringComparison.OrdinalIgnoreCase)
            ? _localization["Diag_OutputAndInputAvailable"]
            : _localization.FormatKey("Diag_AudioPair", AudioDefaultState(output), AudioDefaultState(input));

    private string AudioDefaultState(string state) => state.ToLowerInvariant() switch
    {
        "available" => _localization["Diag_EndpointAvailable"],
        "no active default" => _localization["Diag_EndpointMissing"],
        "not sampled" => _localization["Diag_EndpointUnsampled"],
        "could not be checked" => _localization["Diag_EndpointUnknown"],
        _ => state.ToLowerInvariant(),
    };

    private void PopulateReport(DiagnosticsReport report, bool languageChanged = false)
    {
        RuntimeHealthSnapshot health = report.Health;
        PopulateOverview(health);
        SetRows(BuildRows, _buildInformation.GetDisplayRows(_localization));
        BuildSummary.Text = _localization.FormatKey("Diag_BuildSummary", _buildInformation.Version, _buildInformation.ProcessArchitecture.ToLowerInvariant(), health.CapturedUtc.ToLocalTime());
        var details = new List<KeyValuePair<string, string>>();
        if (health.Hotkeys is { } hotkeys)
        {
            details.Add(new(_localization["Diag_ShortcutEditing"], hotkeys.CaptureSuspended ? _localization["Diag_InProgress"] : _localization["Diag_NotActive"]));
            details.Add(new(_localization["Diag_MessageHost"], hotkeys.KeyboardMessageHostRunning ? _localization["Diag_Running"] : _localization["Diag_Inactive"]));
            details.Add(new(_localization["Diag_KeyboardHook"], hotkeys.KeyboardHookRunning ? _localization["Diag_Running"] : _localization["Diag_Inactive"]));
            details.Add(new(_localization["Diag_MouseHook"], hotkeys.MouseHookRunning ? _localization["Diag_Running"] : _localization["Diag_Inactive"]));
            details.Add(new(_localization["Diag_FallbackRegistrations"], hotkeys.Registrations.Count(static key => key.LastOutcome == "Fallback").ToString()));
            int registrationIssues = CountRegistrationIssues(hotkeys);
            details.Add(new(_localization["Diag_RegistrationIssuesText"], registrationIssues == 0 ? _localization["Diag_NoneRecorded"] : registrationIssues.ToString()));
            foreach (var problem in hotkeys.Registrations.Where(static key => key.LastOutcome is not ("None" or "Registered" or "Fallback")))
                details.Add(new(Services.Hotkeys.HotkeyDisplayFormatter.FormatActionName(problem.ActionId),
                    problem.LastOutcome + (problem.Win32Error == 0 ? "" : $" (Windows error {problem.Win32Error})")));
        }
        SetRows(DetailRows, details);
        SetRows(AutomationRows,
        [
            new(_localization["Tray_AutomaticRoutines"], AutomationState(health.RoutineWork)),
            new(_localization["Diag_ProcessMonitor"], health.ProcessMonitorMode),
            new(_localization["Diag_FallbackReason"], health.ProcessMonitorFallbackReason ?? _localization["Diag_NoneRecorded"]),
            new(_localization["Diag_RoutineWork"], health.RoutineWork is { } work ? _localization.FormatKey("Diag_RoutineQueue", work.ActiveTasks, work.DeferredOperations) : _localization["Tray_Unavailable"]),
            new(_localization["Diag_DebounceSlots"], health.RoutineWork is { } pending ? string.Join(", ", pending.DebounceSlots.DefaultIfEmpty("None")) : _localization["Tray_Unavailable"]),
        ]);
        List<KeyValuePair<string, string>> audioRows = health.Audio is { } devices ?
        [
            new(_localization["Diag_OutputDefault"], AudioDefaultState(devices.OutputDefault)), new(_localization["Diag_InputDefault"], AudioDefaultState(devices.InputDefault)),
            new(_localization["Ui_OutputRoles"], Roles(devices.OutputRolesSplit)), new(_localization["Ui_InputRoles"], Roles(devices.InputRolesSplit)),
            new(_localization["Diag_CacheRefresh"], devices.RefreshPending ? _localization["Diag_Pending"] : _localization["Diag_NotPending"]),
            new(_localization["Diag_LastCachedAudioCheck"], When(devices.LastRefreshedUtc)),
            new(_localization["Diag_CachedSample"], devices.LastRefreshedUtc == null ? _localization["Diag_EndpointUnsampled"] : devices.Stale ? _localization["Diag_LastKnownValues"] : _localization["Diag_WithinCacheLifetime"]),
        ] : [new KeyValuePair<string, string>(_localization["Ui_AudioDefaults"], _localization["Tray_Unavailable"])];
        if (health.AudioWorker is { } worker)
            audioRows.AddRange(
            [
                new(_localization["Diag_AudioWorker"], !worker.Initialized ? _localization["Diag_NotStarted"] : !worker.Running ? _localization["Diag_Stopped"] : worker.ShuttingDown ? _localization["Diag_Stopping"] : worker.Busy ? _localization["Diag_Busy"] : _localization["Diag_Idle"]),
                new(_localization["Diag_QueuedEntries"], _localization.FormatKey("Diag_AudioQueue", worker.QueuedEntries, worker.QueueCapacity)),
                new(_localization["Diag_ActiveCall"], worker.ActiveOperationElapsedMs is { } elapsed ? _localization.FormatKey("Diag_Seconds", elapsed / 1000) : "None"),
                new(_localization["Diag_LastAudioCallCompleted"], When(worker.LastCompletedUtc)),
            ]);
        SetRows(AudioRows, audioRows);
        var settingsRows = new List<KeyValuePair<string, string>>
        {
            new(_localization["Diag_SettingsLastWritten"], report.SettingsLastWritten?.ToString("g") ?? _localization["Tray_Unavailable"]),
        };
        if (health.Settings is { } save)
            settingsRows.AddRange(
            [
                new(_localization["Diag_AutoSave"], save.AutoSaveEnabled ? _localization["Ui_Enabled"] : _localization["Ui_Disabled"]), new(_localization["Diag_SavePending"], save.SavePending ? _localization["Diag_Yes"] : _localization["Diag_No"]),
                new(_localization["Diag_Storage"], save.UsingFallback ? _localization["Diag_FallbackLocation"] : _localization["Diag_PrimaryLocation"]),
                new(_localization["Diag_LastSaveAttempt"], When(save.LastSaveAttemptUtc)), new(_localization["Diag_SaveResult"], Outcome(save.LastSaveSucceeded)),
                new(_localization["Diag_SaveError"], save.LastSaveError ?? _localization["Diag_NoneRecorded"]), new(_localization["Diag_SettingsRecovery"], save.RecoveryWarning ? _localization["Diag_LoadRecoveryWarningRecorded"] : _localization["Diag_NoWarningRecorded"]),
            ]);
        SetRows(SettingsRows, settingsRows);
        SetRows(LoggingRows, health.Logging is { } log ? new List<KeyValuePair<string, string>>
        {
            new(_localization["Diag_Level"], log.Level), new(_localization["Diag_SensitiveLogRedaction"], log.RedactionEnabled ? _localization["Ui_Enabled"] : _localization["Ui_Disabled"]),
            new(_localization["Diag_QueuedMessages"], log.QueuedMessages.ToString()), new(_localization["Diag_DroppedMessages"], health.DroppedLogMessages.ToString()),
            new(_localization["Diag_LastFileWrite"], When(log.LastWriteAttemptUtc)), new(_localization["Diag_FileWriteResult"], Outcome(log.LastWriteSucceeded)),
            new(_localization["Diag_FileWriteError"], log.LastWriteError ?? _localization["Diag_NoneRecorded"]),
        } : [new KeyValuePair<string, string>(_localization["Diag_LoggingState"], _localization["Tray_Unavailable"])]);
        SetRows(RecoveryRows, health.Recovery is { } recovery ? new List<KeyValuePair<string, string>>
        {
            new(_localization["Diag_LastCompleted"], When(recovery.CompletedUtc)), new(_localization["Diag_Result"], recovery.Succeeded ? _localization["Diag_Succeeded"] : _localization["Diag_Incomplete"]),
            new(_localization["Diag_AudioCallbacks"], recovery.AudioRecovered switch { true => _localization["Diag_Recovered"], false => _localization["Diag_Incomplete"], _ => _localization["Diag_Unknown"] }),
            new(_localization["Diag_DeviceAndMuteRefresh"], recovery.DevicesRefreshed switch { true => _localization["Diag_Completed"], false => _localization["Diag_Incomplete"], _ => _localization["Diag_Unknown"] }),
            new(_localization["Diag_HotkeyAttempts"], recovery.HotkeyAttempts.ToString()), new(_localization["Diag_HotkeyFailures"], recovery.HotkeyFailedCount.ToString()),
        } : [new KeyValuePair<string, string>(_localization["Diag_Recovery"], _localization["Diag_NoCompletedRecoveryInThisSession"])]);
        if (!languageChanged)
        {
            _resourceComparisonBaseline = _previousResources;
            _previousResources = report.Process;
        }
        PopulateResources(report.Process);
        if (languageChanged) return;
        string? selectedId = (OperationsList.SelectedItem as DiagnosticsOperation)?.Id;
        _lastReport = report;
        Report.Text = report.History;
        ApplyHistoryFilter(selectedId);
        CopyDiagnosticsButton.IsEnabled = true;
        SetStatus(new("Diag_RedactedSnapshot"));

        string Roles(bool? split) => split == true ? _localization["Diag_DifferentDefaultsCanBeIntentional"] : split == false ? _localization["Diag_SameDefaultAcrossRoles"] : _localization["Diag_NoCompleteRoleSample"];
        string When(DateTimeOffset? time) => time?.ToLocalTime().ToString("G") ?? _localization["Diag_NotRecordedInThisSession"];
        string Outcome(bool? result) => result == true ? _localization["Diag_Succeeded"] : result == false ? _localization["Diag_Failed"] : _localization["Diag_NotRecorded"];
    }

    private string AutomationState(RoutineWorkSnapshot? routines) => routines == null ? _localization["Tray_Unavailable"]
        : routines.AutomaticPaused ? routines.AutomaticResumeUtc is { } time ? _localization.FormatKey("Diag_PauseUntil", time.ToLocalTime()) : _localization["Diag_PausedUntilResumed"]
        : routines.HasAutomaticRoutines == false ? _localization["Diag_NoneConfigured"] : routines.HasAutomaticRoutines == true ? _localization["Diag_Active"] : _localization["Diag_NotPaused"];

    private void PopulateResources(RuntimeProcessSnapshot? process)
    {
        SetRows(ProcessRows, process is { } current ? new List<KeyValuePair<string, string>>
        {
            new(_localization["Diag_Uptime"], current.Uptime is { } uptime ? uptime.ToString(@"d\.hh\:mm\:ss") : _localization["Tray_Unavailable"]),
            new(_localization["Diag_PrivateMemory"], Memory(current.PrivateBytes)), new(_localization["Diag_ManagedHeap"], Memory(current.ManagedBytes)), new(_localization["Diag_WorkingSet"], Memory(current.WorkingSetBytes)),
            new(_localization["Diag_Handles"], current.Handles.ToString()), new(_localization["Diag_Threads"], current.Threads.ToString()), new(_localization["Diag_ProcessID"], current.ProcessId.ToString()),
            new(_localization["Diag_Architecture"], current.Architecture), new(_localization["Diag_AllocatedSinceStart"], Memory(current.AllocatedBytes)),
            new(_localization["Diag_GCCollections"], _localization.FormatKey("Diag_GC", current.Gen0Collections, current.Gen1Collections, current.Gen2Collections)),
        } : [new KeyValuePair<string, string>(_localization["Diag_ProcessCounters"], _localization["Tray_Unavailable"])]);
        ResourceComparison.Text = process is { } next && _resourceComparisonBaseline is { } previous && next.ProcessId == previous.ProcessId
            ? _localization.FormatKey("Diag_ResourcesDelta", (next.PrivateBytes - previous.PrivateBytes) / 1048576d, (next.ManagedBytes - previous.ManagedBytes) / 1048576d, next.Handles - previous.Handles)
            : _localization["Diag_RefreshToCompareMemoryAndHandlesWith"];
        static string Memory(long bytes) => $"{bytes / 1048576d:N1} MiB";
    }

    private void UpdateOperationsEmptyState(IReadOnlyList<DiagnosticsOperation> entries)
    {
        EmptyOperations.Text = entries.Count > 0 ? _localization["Diag_NoIssuesInTheLatestOperations"] : _localization["Diag_NoOperationsRecordedThisSession"];
    }

    private void HistoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_closed && _lastReport != null) ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter(string? selectedId = null)
    {
        selectedId ??= (OperationsList.SelectedItem as DiagnosticsOperation)?.Id;
        var entries = _lastReport?.Operations ?? [];
        HistoryFilter.Visibility = OperationsHint.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var visible = entries.Where(operation => HistoryFilter.SelectedIndex != 1 || operation.Issue).ToArray();
        OperationsList.ItemsSource = visible;
        if (_lastReport?.Operations != null) Report.Text = _localization["Diag_SelectAnOperationToInspectItsRedacted"];
        OperationsList.Visibility = visible.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyOperations.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateOperationsEmptyState(entries);
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

    private void OperationsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || OperationsList.SelectedItem is not DiagnosticsOperation) return;
        e.Handled = true;
        if (OperationDetails.IsExpanded) Report.BringIntoView();
        else OperationDetails.IsExpanded = true;
    }

    private void UpdateSelectedOperation()
    {
        bool hasSelection = OperationsList.SelectedItem is DiagnosticsOperation;
        CopyOperationButton.IsEnabled = hasSelection;
        OperationDetails.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        OperationAdvice.Content = (OperationsList.SelectedItem as DiagnosticsOperation)?.Advice;
        if (!_closed && OperationsList.SelectedItem is DiagnosticsOperation operation) Report.Text = operation.Details;
        else if (!_closed && _lastReport?.Operations != null) Report.Text = _localization["Diag_SelectAnOperationToInspectItsRedacted"];
    }

    private void CopyOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested || OperationsList.SelectedItem is not DiagnosticsOperation operation) return;
        try
        {
            _copyText("AudioPilot operation (redacted)" + Environment.NewLine + operation.Details);
            SetStatus(new("Diag_RedactedOperationCopiedReviewBeforeSharing"));
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "diagnostics-operation-copy-failed", nameof(CopyOperation_Click), ex);
            SetStatus(new("Diag_TheOperationCouldNotBeCopiedTry"));
        }
    }

    private void ReviewSetting_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _lifetime.IsCancellationRequested || sender is not FrameworkElement { DataContext: DiagnosticAdvice { Target: { } target } }) return;
        if (_reviewSetting == null) { SetStatus(new("Diag_OpenAudioPilotSMainWindowToReview")); return; }
        try { _reviewSetting(target); Close(); }
        catch (Exception ex)
        {
            Logger.Instance.Warning("DiagnosticsWindow", "diagnostics-review-setting-failed", nameof(ReviewSetting_Click), ex);
            if (!_closed) SetStatus(new("Diag_TheSettingCouldNotBeOpenedTry"));
        }
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Platform;
using AudioPilot.Services.UI;
using Microsoft.Win32;

namespace AudioPilot.Updater.Ui;

public partial class UpdaterWindow : Window
{
    private readonly UpdaterSession? _session;
    private readonly Func<string?> _choosePackage;
    private readonly DispatcherTimer _poll;
    private readonly DialogWorkAreaLifetime _workArea;
    private readonly AppTheme _theme;
    private UpdateJobState? _last;
    private bool _lastBusy;
    private bool _canClose;
    private bool _closing;
    private bool _closed;
    private string? _source;
    private readonly Func<UpdateConsent, CancellationToken, Task<bool>> _confirm;
    private AppDialogService? _dialogs;
    private CancellationTokenSource? _consentCancellation;
    private bool _confirming;
    private string? _startupError;
    private string? _actionError;
    private bool StartupFailed => _startupError != null;
    private readonly LocalizationService _localization;
    private readonly TaskCompletionSource _windowClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? ActiveAction { get; private set; }
    internal Task WindowClosed => _windowClosed.Task;

    internal UpdaterWindow(UpdaterSession? session, AppTheme theme, Func<string?>? choosePackage = null,
        Func<UpdateConsent, CancellationToken, Task<bool>>? confirm = null, LocalizationService? localization = null)
    {
        _session = session; _theme = theme;
        _localization = localization ?? LocalizationService.Default;
        LocalizationContext.SetService(this, _localization);
        _choosePackage = choosePackage ?? ChoosePackage;
        _confirm = confirm ?? ConfirmDefaultAsync;
        WindowThemeResolver.SetApplicationThemeProvider(() => theme);
        WindowThemeHelper.ApplyApplicationThemeResources(theme);
        InitializeComponent();
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _poll.Stop();
        _workArea = new DialogWorkAreaLifetime(this);
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTheme(this, _theme);
        IsVisibleChanged += (_, _) => UpdatePolling();
        Closing += OnClosing;
        Closed += OnClosed;
        PreviewKeyDown += OnKey;
        _source = session?.Feed?.PackageUri.AbsoluteUri ?? session?.Snapshot.Source;
        if (session?.Feed != null) BrowseButton.Visibility = Visibility.Collapsed;
        PackageSelectionPanel.Visibility = session?.Feed != null ? Visibility.Collapsed : Visibility.Visible;
        PropertyChangedEventManager.AddHandler(_localization, LanguageChanged, nameof(LocalizationService.Revision));
        Refresh(force: true);
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        double offset = UpdateScrollViewer.VerticalOffset;
        bool atBottom = UpdateScrollViewer.ScrollableHeight > 0 && offset >= UpdateScrollViewer.ScrollableHeight - 1;
        Refresh(force: true);
        UpdateScrollViewer.UpdateLayout();
        if (atBottom) UpdateScrollViewer.ScrollToBottom();
        else UpdateScrollViewer.ScrollToVerticalOffset(offset);
    }

    private void RenderSource()
    {
        string? runtime = _session?.Installed.Runtime;
        runtime = runtime switch
        {
            "self-contained" => _localization["Update_SelfContained"],
            "framework-dependent" => _localization["Update_FrameworkDependent"],
            _ => runtime
        };
        DeploymentText.Text = _session == null ? _localization["Update_InstallationIsAvailableOnlyInWindowsSandbox"] :
            $"{(_session.Installed.Kind == "msi" ? "MSI" : _localization["Update_Portable"])} · {runtime}";
        PackagePath.Text = _source == null ? _localization["Update_NoPackageSelected"] : System.IO.Path.GetFileName(_source);
        PackagePath.ToolTip = _source;
        if (_session?.Feed is not { } feed) return;
        bool pilot = feed is HttpsStagingFeed { IsPilot: true };
        bool stable = feed is HttpsStableFeed;
        ModeLabel.Text = stable ? _localization["Update_StableChannel"] : pilot ? _localization["Update_OptInStaging"] : feed is HttpsStagingFeed ? _localization["Update_HTTPSStaging"] : _localization["Update_LocalHTTPSTest"];
        ModeDescription.Text = stable ? feed is HttpsStableFeed { Policy.PublicUpdatesEnabled: true }
                ? _localization["Update_VerifiedStableReleasesUpdateAndRestartRequires"] : _localization["Update_StablePackagesIsolatedValidationOnlyPublicUpdates"]
            : pilot ? _localization["Update_SelfSignedStagingReleaseUpdateAndRestart"]
            : _localization["Update_SandboxFeedUpdateAndRestartRequiresOne"];
        PackagePath.Text = stable ? _localization["Update_StableUpdateFeed"] : pilot ? _localization["Update_StagingUpdateFeed"] : _localization["Update_HTTPSTestFeed"];
        PackagePath.ToolTip = _localization["Update_FeedAddressIsAvailableInDetails"];
        PackageLabel.Content = _localization["Update_UpdateSource"];
    }

    private string? ChoosePackage()
    {
        string extension = _session?.Installed.Kind == "msi" ? "msi" : "zip";
        var picker = new OpenFileDialog { Title = _localization["Update_SelectALocalUpdatePackage"], Filter = _localization.FormatKey("Update_PackageFilter", extension), CheckFileExists = true, Multiselect = false };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }
    private void Browse_Click(object sender, RoutedEventArgs args)
    {
        if (_closing || _confirming || _session == null || _session.IsBusy) return;
        string? path = _choosePackage();
        if (path == null) return;
        _source = path;
        _actionError = null;
        RenderSource();
        Refresh(force: true);
    }
    private void Verify_Click(object sender, RoutedEventArgs args)
    {
        if (_source == null || _session == null) return;
        if (_session.Feed != null && _session.Snapshot.Phase != UpdateJobPhase.Available) Start(() => _session.CheckAsync());
        else Start(DownloadWithConsentAsync);
    }
    private void Install_Click(object sender, RoutedEventArgs args)
    {
        if (_session != null && InstallButton.IsEnabled) Start(async () =>
        {
            var shown = _session.Snapshot;
            if (!await ConfirmAsync(shown)) return _session.Snapshot;
            return await _session.UpdateAndRestartAsync(shown);
        });
    }
    private void Recover_Click(object sender, RoutedEventArgs args)
    {
        if (_session != null) Start(_session.RecoverAsync);
    }
    internal void ResumePendingRecovery()
    {
        if (_session?.Snapshot.Phase == UpdateJobPhase.RecoveryRequired) Start(_session.RecoverAsync);
    }
    private void Pause_Click(object sender, RoutedEventArgs args)
    {
        if (_session == null || _closing) return;
        if (_session.Snapshot.Phase is UpdateJobPhase.Paused or UpdateJobPhase.Failed && _source != null) Start(DownloadWithConsentAsync);
        else { _session.Pause(); Refresh(force: true); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs args)
    {
        if (_session == null || _closing) return;
        try { _session.Cancel(); _actionError = null; Refresh(force: true); }
        catch (Exception error) { ShowFailure(error); }
    }
    private void Close_Click(object sender, RoutedEventArgs args) => Close();
    private void ManualDownload_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                AudioPilot.Constants.AppConstants.Links.RepositoryUrl + "/releases/latest")
            { UseShellExecute = true });
        }
        catch (Exception error) { ShowFailure(error); }
    }

    internal void ShowOrActivate()
    {
        if (_closed || _closing) return;
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        if (!IsVisible) Show();
        Activate();
    }
    private void Start(Func<Task<UpdateJobState>> action)
    {
        if (_closing || _confirming || _session == null || _session.IsBusy) return;
        _actionError = null;
        ActiveAction = PerformAsync(action);
    }
    private async Task<bool> ConfirmDefaultAsync(UpdateConsent consent, CancellationToken token)
    {
        if (_dialogs == null) { _dialogs = new AppDialogService(); _dialogs.SetSoundsEnabled(false); }
        return await _dialogs.ShowAsync(consent.CreateDialog(this), token) == AppDialogResult.Confirmed;
    }
    private async Task<bool> ConfirmAsync(UpdateJobState shown)
    {
        if (_closing || _closed || shown.Artifact == null || shown.Version == null) return false;
        _confirming = true;
        using var cancel = new CancellationTokenSource();
        _consentCancellation = cancel;
        Refresh(force: true);
        try
        {
            bool approved = await _confirm(new(_session!.Installed.InstalledVersion, shown.Version, shown.Artifact.Length,
                shown.Phase is UpdateJobPhase.Ready or UpdateJobPhase.Deferred), cancel.Token) &&
                !cancel.IsCancellationRequested && !_closing && !_closed && _session.Snapshot == shown;
            UpdateLog.Write(_session.Diagnostics, new(approved ? UpdateLogKind.ApprovalGranted : UpdateLogKind.ApprovalDeclined, shown.Phase, "window", shown.Version));
            return approved;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return false; }
        finally { _confirming = false; _consentCancellation = null; if (!_closed) Refresh(force: true); }
    }
    private async Task<UpdateJobState> DownloadWithConsentAsync()
    {
        if (_session!.Feed == null) return await _session.VerifyAsync(_source!);
        if (_session.Snapshot.Phase is not (UpdateJobPhase.Available or UpdateJobPhase.Paused or UpdateJobPhase.Failed) || _session.Snapshot.Artifact == null)
            await _session.CheckAsync();
        var shown = _session.Snapshot;
        if (_closing || _closed || shown.Artifact == null || shown.Phase is not (UpdateJobPhase.Available or UpdateJobPhase.Paused or UpdateJobPhase.Failed)) return shown;
        if (!await ConfirmAsync(shown)) return _session.Snapshot;
        if (_closing || _closed) return _session.Snapshot;
        return await _session.UpdateAndRestartAsync(shown);
    }
    private async Task PerformAsync(Func<Task<UpdateJobState>> action)
    {
        try
        {
            var task = action();
            Refresh(force: true);
            await task;
            if (!_closed && !_closing)
            {
                Refresh(force: true);
                CloseButton.Focus();
            }
        }
        catch (Exception error) { if (!_closed && !_closing) ShowFailure(error); }
        finally { UpdatePolling(); }
    }

    internal void Refresh(bool force = false)
    {
        if (_closed) return;
        var state = _session?.Snapshot ?? new(1, 0, Guid.Empty.ToString("N"), "preview", UpdateJobPhase.Idle);
        bool busy = _session?.IsBusy == true;
        if (!force && ReferenceEquals(state, _last) && busy == _lastBusy) return;
        bool phaseChanged = _last?.Phase != state.Phase;
        if (phaseChanged) _actionError = null;
        if (force) RenderSource();
        _last = state; _lastBusy = busy;
        var view = UpdaterPresentation.From(state, busy, _source != null, _session?.Feed != null, _localization);
        StatusHeading.Text = view.Heading;
        InstalledLabel.Text = state.Phase is UpdateJobPhase.RecoveryRequired or UpdateJobPhase.Recovering ? _localization["Update_LastConfirmed"] : _localization["Ui_Installed"];
        InstalledText.Text = _session == null ? _localization["Update_Preview"] : $"{_session.Installed.InstalledVersion} ({_session.Installed.Architecture.Replace("win-", "", StringComparison.Ordinal)})";
        StatusMessage.Text = _session == null ? _localization["Update_ReadOnlyPreviewOpenAPreparedWorkspace"] : view.Message;
        VersionText.Text = view.VersionSummary;
        ProgressText.Text = view.Progress;
        PackageProgress.IsIndeterminate = view.Indeterminate;
        PackageProgress.Value = state.Artifact is { Length: > 0 } artifact ? Math.Clamp(100d * state.Bytes / artifact.Length, 0, 100) : 0;
        PackageProgress.Visibility = busy || state.Phase == UpdateJobPhase.Paused ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Visibility = PackageProgress.Visibility;
        BrowseButton.IsEnabled = _session != null && view.CanBrowse && !_closing && !_confirming;
        VerifyButton.Visibility = !StartupFailed && state.Phase is UpdateJobPhase.Idle or UpdateJobPhase.Available or UpdateJobPhase.Cancelled or UpdateJobPhase.Completed or UpdateJobPhase.RolledBack or UpdateJobPhase.Current or UpdateJobPhase.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        if (_session?.Feed != null) VerifyButton.Content = state.Phase == UpdateJobPhase.Available ? _localization["Ui_UpdateAndRestart"] : _localization["Update_CheckForUpdates"];
        bool pilotIsCurrent = _session?.Feed is HttpsStagingFeed { IsPilot: true } && Version.Parse(_session.Installed.InstalledVersion) >= Version.Parse(AudioPilot.UpdateProtocol.UpdateClientPolicy.Current!.CandidateVersion!);
        if (pilotIsCurrent && state.Phase == UpdateJobPhase.Idle) { StatusHeading.Text = _localization["Update_PilotReleaseInstalled"]; StatusMessage.Text = _localization["Update_ThisInstallationAlreadyHasTheReleaseApproved"]; }
        VerifyButton.IsEnabled = _session != null && view.CanVerify && !_closing && !_confirming && !pilotIsCurrent;
        InstallButton.Visibility = view.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = view.CanInstall && !_closing && !_confirming;
        RecoverButton.Visibility = view.CanRecover ? Visibility.Visible : Visibility.Collapsed;
        RecoverButton.IsEnabled = view.CanRecover && !_closing;
        PauseButton.Content = view.CanResume ? state.Phase == UpdateJobPhase.Failed ? state.FailurePhase == UpdateJobPhase.Preparing ? _localization["Update_RetryUpdate"] : state.Artifact == null ? _localization["Update_RetryCheck"] : _localization["Update_RetryDownload"] : _localization["Update_Resume"] : _localization["Ui_PauseText"];
        PauseButton.Visibility = !StartupFailed && (view.CanPause || view.CanResume) ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = !_closing;
        CancelButton.Visibility = !StartupFailed && view.CanCancel && _session != null ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = !_closing;
        ManualDownloadLink.Visibility = StartupFailed || state.Phase == UpdateJobPhase.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        DetailsText.Text = _localization.FormatKey("Update_Details",
            _session?.Feed == null ? _localization["Update_LocalPackages"] : _session.Feed is HttpsStableFeed ? _localization["Update_StableHTTPS"] :
                _session.Feed is HttpsStagingFeed { IsPilot: true } ? _localization["Update_OptInHTTPSPilot"] :
                _session.Feed is HttpsStagingFeed ? _localization["Update_PublicHTTPSStagingSandboxOnly"] : _localization["Update_LoopbackHTTPSTestFeed"],
            _session?.Feed?.BaseUri.ToString() ?? _localization["Update_Local"], state.Phase, _session?.Installed.Kind ?? _localization["Update_PreviewText"],
            _session?.Installed.Runtime ?? _localization["Update_NotConnected"], _session?.Installed.Architecture ?? _localization["Update_NotConnected"], state.Reason ?? _localization["Update_None"],
            _session?.Feed is HttpsStableFeed { Policy.PublicUpdatesEnabled: true } ? _localization["Update_EnabledApprovalRequired"] : _localization["Update_Disabled"]);
        if (StartupFailed)
        {
            StatusHeading.Text = _localization["Update_TheUpdaterCouldNotOpen"];
            StatusMessage.Text = _localization["Update_ReviewDetailsAndTryOpeningTheUpdater"];
            DetailsText.Text += Environment.NewLine + _localization.FormatKey("Update_StartupDetail", _startupError);
            DetailsText.Text += Environment.NewLine + _localization.FormatKey("Update_ManualDownloadsDetail", AudioPilot.Constants.AppConstants.Links.RepositoryUrl + "/releases/latest");
        }
        else if (_actionError != null)
        {
            StatusHeading.Text = _localization["Update_TheUpdaterNeedsAttention"];
            StatusMessage.Text = _localization["Update_TheOperationCouldNotFinishSafelyReview"];
            DetailsText.Text += Environment.NewLine + _localization.FormatKey("Update_ActionDetail", _actionError);
        }
        if (phaseChanged && UIElementAutomationPeer.FromElement(StatusHeading) is { } peer) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        UpdatePolling();
    }
    private void UpdatePolling()
    {
        if (_closed || _closing || !IsVisible || _session?.IsBusy != true) _poll.Stop();
        else _poll.Start();
    }
    private void ShowFailure(Exception error)
    {
        UpdateLog.Write(_session?.Diagnostics, new(UpdateLogKind.WindowActionFailed, _session?.Snapshot.Phase, "window", Error: error));
        Refresh(force: true);
        _actionError = error.GetType().Name;
        Refresh(force: true);
    }
    internal void ShowStartupFailure(Exception error)
    {
        _startupError = error.GetType().Name;
        Refresh(force: true);
    }
    private void OnKey(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { args.Handled = true; Close(); }
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_canClose || _session == null) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        _consentCancellation?.Cancel();
        _poll.Stop();
        Task? confirmation = _confirming ? ActiveAction : null;
        bool active = _session.IsBusy || _confirming;
        if (active) Hide();
        ActiveAction = CloseSafelyAsync(confirmation);
    }
    private async Task CloseSafelyAsync(Task? confirmation)
    {
        await Task.Yield();
        try
        {
            if (confirmation != null) await confirmation;
            await _session!.CloseSafelyAsync();
            _canClose = true;
            Close();
        }
        catch (Exception error)
        {
            _closing = false;
            if (!IsVisible) Show();
            ShowFailure(error);
            Activate();
        }
    }
    private async void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        PropertyChangedEventManager.RemoveHandler(_localization, LanguageChanged, nameof(LocalizationService.Revision));
        _poll.Stop();
        _workArea.Dispose();
        Closing -= OnClosing; Closed -= OnClosed; PreviewKeyDown -= OnKey;
        try { if (_dialogs != null) await _dialogs.DisposeAsync(); _windowClosed.TrySetResult(); }
        catch (Exception error) { _windowClosed.TrySetException(error); }
    }
}

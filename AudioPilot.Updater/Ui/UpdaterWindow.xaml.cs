using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Helpers;
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
    private bool _startupFailure;
    private readonly TaskCompletionSource _windowClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? ActiveAction { get; private set; }
    internal Task WindowClosed => _windowClosed.Task;

    internal UpdaterWindow(UpdaterSession? session, AppTheme theme, Func<string?>? choosePackage = null,
        Func<UpdateConsent, CancellationToken, Task<bool>>? confirm = null)
    {
        _session = session; _theme = theme;
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
        DeploymentText.Text = session == null ? "Installation is available only in Windows Sandbox" :
            $"{(session.Installed.Kind == "msi" ? "MSI" : "Portable")} · {session.Installed.Runtime}";
        _source = session?.Feed?.PackageUri.AbsoluteUri ?? session?.Snapshot.Source;
        PackagePath.Text = session?.Feed == null ? _source == null ? "No package selected" : System.IO.Path.GetFileName(_source) : _source;
        PackagePath.ToolTip = session?.Feed == null ? _source : "Feed address is available in Details.";
        if (session?.Feed != null)
        {
            bool pilot = session.Feed is HttpsStagingFeed { IsPilot: true };
            bool stable = session.Feed is HttpsStableFeed;
            ModeLabel.Text = stable ? "Stable channel" : pilot ? "Opt-in staging" : session.Feed is HttpsStagingFeed ? "HTTPS staging" : "Local HTTPS test";
            ModeDescription.Text = stable ? session.Feed is HttpsStableFeed { Policy.PublicUpdatesEnabled: true }
                    ? "Verified stable releases. Update and restart requires your approval." : "Stable packages; isolated validation only. Public updates remain disabled."
                : pilot ? "Self-signed staging release. Update and restart requires one approval. Public updates remain disabled."
                : "Sandbox feed. Update and restart requires one approval. Public updates remain disabled.";
            PackagePath.Text = stable ? "Stable update feed" : pilot ? "Staging update feed" : "HTTPS test feed";
            PackageLabel.Content = "Update source"; BrowseButton.Visibility = Visibility.Collapsed; VerifyButton.Content = "_Check for updates";
        }
        Refresh(force: true);
    }

    private string? ChoosePackage()
    {
        string extension = _session?.Installed.Kind == "msi" ? "msi" : "zip";
        var picker = new OpenFileDialog { Title = "Select a local update package", Filter = $"Update package (*.{extension})|*.{extension}", CheckFileExists = true, Multiselect = false };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }
    private void Browse_Click(object sender, RoutedEventArgs args)
    {
        if (_closing || _confirming || _session == null || _session.IsBusy) return;
        string? path = _choosePackage();
        if (path == null) return;
        _source = path;
        PackagePath.Text = System.IO.Path.GetFileName(path);
        PackagePath.ToolTip = path;
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
        try { _session.Cancel(); Refresh(force: true); }
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
        _last = state; _lastBusy = busy;
        var view = UpdaterPresentation.From(state, busy, _source != null, _session?.Feed != null);
        StatusHeading.Text = view.Heading;
        InstalledLabel.Text = state.Phase is UpdateJobPhase.RecoveryRequired or UpdateJobPhase.Recovering ? "Last confirmed" : "Installed";
        InstalledText.Text = _session == null ? "Preview" : $"{_session.Installed.InstalledVersion} ({_session.Installed.Architecture.Replace("win-", "", StringComparison.Ordinal)})";
        StatusMessage.Text = _session == null ? "Read-only preview. Open a prepared workspace inside Windows Sandbox to test an update." : view.Message;
        VersionText.Text = state.Version ?? "Not verified";
        ProgressText.Text = view.Progress;
        PackageProgress.IsIndeterminate = view.Indeterminate;
        PackageProgress.Value = state.Artifact is { Length: > 0 } artifact ? Math.Clamp(100d * state.Bytes / artifact.Length, 0, 100) : 0;
        PackageProgress.Visibility = busy || state.Phase == UpdateJobPhase.Paused ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Visibility = PackageProgress.Visibility;
        BrowseButton.IsEnabled = _session != null && view.CanBrowse && !_closing && !_confirming;
        VerifyButton.Visibility = !_startupFailure && state.Phase is UpdateJobPhase.Idle or UpdateJobPhase.Available or UpdateJobPhase.Cancelled or UpdateJobPhase.Completed or UpdateJobPhase.RolledBack or UpdateJobPhase.Current or UpdateJobPhase.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        if (_session?.Feed != null) VerifyButton.Content = state.Phase == UpdateJobPhase.Available ? "_Update and restart" : "_Check for updates";
        bool pilotIsCurrent = _session?.Feed is HttpsStagingFeed { IsPilot: true } && Version.Parse(_session.Installed.InstalledVersion) >= Version.Parse(AudioPilot.UpdateProtocol.UpdateClientPolicy.Current!.CandidateVersion!);
        if (pilotIsCurrent && state.Phase == UpdateJobPhase.Idle) { StatusHeading.Text = "Pilot release installed"; StatusMessage.Text = "This installation already has the release approved for this pilot."; }
        VerifyButton.IsEnabled = _session != null && view.CanVerify && !_closing && !_confirming && !pilotIsCurrent;
        InstallButton.Visibility = view.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = view.CanInstall && !_closing && !_confirming;
        RecoverButton.Visibility = view.CanRecover ? Visibility.Visible : Visibility.Collapsed;
        RecoverButton.IsEnabled = view.CanRecover && !_closing;
        PauseButton.Content = view.CanResume ? state.Phase == UpdateJobPhase.Failed ? state.FailurePhase == UpdateJobPhase.Preparing ? "_Retry update" : state.Artifact == null ? "_Retry check" : "_Retry download" : "_Resume" : "_Pause";
        PauseButton.Visibility = !_startupFailure && (view.CanPause || view.CanResume) ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = !_closing;
        CancelButton.Visibility = !_startupFailure && view.CanCancel && _session != null ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = !_closing;
        ManualDownloadLink.Visibility = _startupFailure || state.Phase == UpdateJobPhase.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        DetailsText.Text = $"Mode: {(_session?.Feed == null ? "local packages" : _session.Feed is HttpsStableFeed ? "stable HTTPS" : _session.Feed is HttpsStagingFeed { IsPilot: true } ? "opt-in HTTPS pilot" : _session.Feed is HttpsStagingFeed ? "public HTTPS staging, Sandbox only" : "loopback HTTPS test feed")}\nFeed: {_session?.Feed?.BaseUri.ToString() ?? "local"}\nState: {state.Phase}\nDeployment: {_session?.Installed.Kind ?? "preview"}\nRuntime: {_session?.Installed.Runtime ?? "not connected"}\nArchitecture: {_session?.Installed.Architecture ?? "not connected"}\nReason: {state.Reason ?? "none"}\nPublic live updates: {(_session?.Feed is HttpsStableFeed { Policy.PublicUpdatesEnabled: true } ? "enabled, approval required" : "disabled")}";
        if (_startupFailure)
        {
            StatusHeading.Text = "The updater could not open";
            StatusMessage.Text = "Review Details and try opening the updater again. You can also download AudioPilot manually. No installation was started.";
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
        StatusHeading.Text = "The updater needs attention";
        StatusMessage.Text = "The operation could not finish safely. Review Details and try again, or close and reopen the updater.";
        DetailsText.Text += "\nUI action: " + error.GetType().Name;
    }
    internal void ShowStartupFailure(Exception error)
    {
        _startupFailure = true;
        Refresh(force: true);
        DetailsText.Text += "\nStartup: " + error.GetType().Name;
        DetailsText.Text += "\nManual downloads: " + AudioPilot.Constants.AppConstants.Links.RepositoryUrl + "/releases/latest";
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
        _poll.Stop();
        _workArea.Dispose();
        Closing -= OnClosing; Closed -= OnClosed; PreviewKeyDown -= OnKey;
        try { if (_dialogs != null) await _dialogs.DisposeAsync(); _windowClosed.TrySetResult(); }
        catch (Exception error) { _windowClosed.TrySetException(error); }
    }
}

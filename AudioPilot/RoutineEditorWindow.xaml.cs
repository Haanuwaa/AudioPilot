using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;
using Microsoft.Win32;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot
{
    public partial class RoutineEditorWindow : Window
    {
        private readonly ILogger _logger;
        private readonly RoutineEditorViewModel _viewModel;
        private readonly IAppDialogService _dialogs;
        private Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>? _packagedAppsTask;
        private Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>? _lastLoggedPackagedAppsLoadTask;
        private long _packagedAppsTaskStartedAt;
        private IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> _packagedApps = [];
        private bool _hasQueuedPackagedAppsPreload;
        private bool _isClosed;
        private bool _isOpeningPackagedAppPicker;
        private InputLanguageManager? _inputLanguageManager;
        private bool _userPreferenceHandlerRegistered;
        private readonly Func<AudioRoutine, CancellationToken, Task<Services.Routines.RoutineReadinessResult>>? _checkRoutine;
        private readonly CancellationTokenSource _checkLifetime = new();
        private int _editRevision;
        private (string Name, bool Enabled, string Definition, string Triggers)? _readinessState;
        private bool _isCheckingRoutine;
        private RoutineTriggerEditorWindow? _triggerEditor;
        private readonly Action<RoutineTriggerEditorWindow> _presentTriggerEditor;

        internal RoutineEditorWindow(RoutineEditorViewModel viewModel, ILogger? logger = null, IAppDialogService? dialogService = null,
            Func<AudioRoutine, CancellationToken, Task<Services.Routines.RoutineReadinessResult>>? checkRoutine = null,
            Action<RoutineTriggerEditorWindow>? presentTriggerEditor = null)
        {
            _checkRoutine = checkRoutine;
            _presentTriggerEditor = presentTriggerEditor ?? (dialog => DialogWindowHelper.ShowOwnedDialog(dialog, this));
            _logger = logger ?? Logger.Instance;
            _viewModel = viewModel;
            _dialogs = dialogService ?? AppDialogServiceProvider.Current;
            InitializeComponent();
            CheckRoutineButton.Visibility = checkRoutine == null ? Visibility.Collapsed : Visibility.Visible;
            DialogWindowHelper.Initialize(this, viewModel);
            UpdateReadinessButton();
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _userPreferenceHandlerRegistered = true;
            _inputLanguageManager = InputLanguageManagerHelper.TryGetCurrent();
            _inputLanguageManager?.InputLanguageChanged += OnInputLanguageChanged;

            QueuePackagedAppsPreloadIfNeeded();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
        }

        private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            _ = AboutDocumentLauncher.TryOpen(_logger, "RoutineEditorWindow");
            e.Handled = true;
        }

        public AudioRoutine? ResultRoutine { get; private set; }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            _triggerEditor?.Close();
            _checkLifetime.Cancel();
            _checkLifetime.Dispose();
            if (_userPreferenceHandlerRegistered)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                _userPreferenceHandlerRegistered = false;
            }

            _inputLanguageManager?.InputLanguageChanged -= OnInputLanguageChanged;
            _inputLanguageManager = null;

            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.Dispose();
            ResetPackagedAppsForClose();
            base.OnClosed(e);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_isClosed || (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Locale))
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
                {
                    return;
                }

                try
                {
                    _ = Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() => OnUserPreferenceChanged(sender, e)));
                }
                catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
                {
                }

                return;
            }

            _viewModel.RefreshSystemTimeFormat();
            _viewModel.RefreshHotkeyDisplayLabels();
        }

        private void OnInputLanguageChanged(object sender, InputLanguageEventArgs e)
        {
            if (!_isClosed)
            {
                _viewModel.RefreshHotkeyDisplayLabels();
            }
        }

        internal void ResetPackagedAppsForClose()
        {
            _packagedAppsTask = null;
            _lastLoggedPackagedAppsLoadTask = null;
            _packagedAppsTaskStartedAt = 0;
            _packagedApps = [];
            _hasQueuedPackagedAppsPreload = false;
        }

        private async Task PreloadPackagedAppsAsync()
        {
            if (_isClosed)
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("RoutineEditorWindow", "routine-packaged-apps-preload-start | forceRefresh=false", nameof(PreloadPackagedAppsAsync));
            }

            try
            {
                await LoadPackagedAppsAsync(forceRefresh: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!_isClosed)
                {
                    _logger.Warning(
                        "RoutineEditorWindow",
                        "routine-packaged-apps-preload-failed | result=failure forceRefresh=false",
                        nameof(PreloadPackagedAppsAsync),
                        ex);
                }
            }
        }

        private void QueuePackagedAppsPreloadIfNeeded()
        {
            if (_hasQueuedPackagedAppsPreload || !(_viewModel.IsApplicationTriggerSelected ||
                RoutineTriggerPathHelper.LooksLikePackagedAppId(_viewModel.RequiredApplicationPath) ||
                RoutineTriggerPathHelper.LooksLikePackagedAppId(_viewModel.TargetAppPath)))
            {
                return;
            }

            _hasQueuedPackagedAppsPreload = true;
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("RoutineEditorWindow", "routine-packaged-apps-preload-queued | reason=app-start-trigger", nameof(QueuePackagedAppsPreloadIfNeeded));
            }
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => _ = PreloadPackagedAppsAsync()));
        }

        private Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> GetPackagedAppsTask(bool forceRefresh)
        {
            _hasQueuedPackagedAppsPreload = true;
            long now = Environment.TickCount64;
            bool reuseTask = ShouldReusePackagedAppsTask(forceRefresh, _packagedAppsTask, _packagedAppsTaskStartedAt, now);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug(
                    "RoutineEditorWindow",
                    () => $"routine-packaged-apps-task | action={(reuseTask ? "reuse" : "create")} forceRefresh={forceRefresh} currentCompleted={_packagedAppsTask?.IsCompleted == true} ageMs={Math.Max(0, now - _packagedAppsTaskStartedAt)}",
                    nameof(GetPackagedAppsTask));
            }

            if (!reuseTask)
            {
                _packagedAppsTask = forceRefresh
                    ? Task.Run(static () =>
                    {
                        AudioDeviceHelper.ClearPackagedAppInventoryCache();
                        return AudioDeviceHelper.GetInstalledPackagedApps();
                    })
                    : Task.Run(AudioDeviceHelper.GetInstalledPackagedApps);
                _packagedAppsTaskStartedAt = now;
            }

            return _packagedAppsTask!;
        }

        private async Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> LoadPackagedAppsAsync(bool forceRefresh)
        {
            if (_isClosed) return _packagedApps;
            CancellationToken token = _checkLifetime.Token;
            Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> loadTask = GetPackagedAppsTask(forceRefresh);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("RoutineEditorWindow", () => $"routine-packaged-apps-load-start | forceRefresh={forceRefresh}", nameof(LoadPackagedAppsAsync));
            }

            try
            {
                IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> loadedApps = await loadTask.WaitAsync(token);
                if (!ShouldApplyPackagedAppsResult(_isClosed, ReferenceEquals(_packagedAppsTask, loadTask)))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("RoutineEditorWindow", () => $"routine-packaged-apps-load-abort | forceRefresh={forceRefresh} reason=stale-or-closed", nameof(LoadPackagedAppsAsync));
                    }
                    return _packagedApps;
                }

                _packagedApps = loadedApps;
                _viewModel.SetPackagedApplicationDisplayNames(loadedApps);
                if (!ReferenceEquals(_lastLoggedPackagedAppsLoadTask, loadTask))
                {
                    _lastLoggedPackagedAppsLoadTask = loadTask;
                    _logger.Info(
                        "RoutineEditorWindow",
                        () => $"routine-packaged-apps-load-complete | result=success forceRefresh={forceRefresh} appCount={loadedApps.Count}",
                        nameof(LoadPackagedAppsAsync));
                }
                else if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "RoutineEditorWindow",
                        () => $"routine-packaged-apps-load-complete | result=success forceRefresh={forceRefresh} appCount={loadedApps.Count} source=cached-task",
                        nameof(LoadPackagedAppsAsync));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _ = loadTask.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            catch (Exception ex)
            {
                if (!ShouldApplyPackagedAppsResult(_isClosed, ReferenceEquals(_packagedAppsTask, loadTask))) return _packagedApps;
                _packagedAppsTask = null;
                _logger.Warning(
                    "RoutineEditorWindow",
                    () => $"routine-packaged-apps-load-failed | result=failure forceRefresh={forceRefresh}",
                    nameof(LoadPackagedAppsAsync),
                    ex);
                throw;
            }

            return _packagedApps;
        }

        internal static bool ShouldApplyPackagedAppsResult(bool isClosed, bool isCurrentTask)
        {
            return !isClosed && isCurrentTask;
        }

        internal static bool ShouldReusePackagedAppsTask(
            bool forceRefresh,
            Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>? currentTask,
            long taskStartedAt,
            long now)
        {
            if (forceRefresh ||
                currentTask == null ||
                currentTask.IsCanceled ||
                currentTask.IsFaulted)
            {
                return false;
            }

            if (!currentTask.IsCompleted)
            {
                return true;
            }

            long age = now - taskStartedAt;
            long maxAge = (long)TimeSpan.FromMinutes(AppConstants.Timing.PackagedAppInventoryCacheMinutes).TotalMilliseconds;
            return age < maxAge;
        }

        internal static bool TryGetExecutableDialogSeed(string triggerAppPath, out string initialDirectory, out string fileName)
        {
            initialDirectory = string.Empty;
            fileName = string.Empty;

            if (!RoutineTriggerPathHelper.LooksLikeExecutablePath(triggerAppPath) ||
                string.IsNullOrWhiteSpace(triggerAppPath) ||
                triggerAppPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                string? candidateDirectory = Path.GetDirectoryName(triggerAppPath);
                string candidateFileName = Path.GetFileName(triggerAppPath);
                if (string.IsNullOrWhiteSpace(candidateDirectory) || string.IsNullOrWhiteSpace(candidateFileName))
                {
                    return false;
                }

                fileName = candidateFileName;
                initialDirectory = ResolveExecutableDialogInitialDirectory(candidateDirectory);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning(
                    "RoutineEditorWindow",
                    "routine-executable-dialog-seed-failed | result=failure",
                    nameof(TryGetExecutableDialogSeed),
                    ex);
                initialDirectory = string.Empty;
                fileName = string.Empty;
                return false;
            }
        }

        internal static string ResolveExecutableDialogInitialDirectory(string candidateDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidateDirectory) ||
                candidateDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            try
            {
                string? root = Path.GetPathRoot(candidateDirectory);
                if (string.IsNullOrWhiteSpace(root) ||
                    root.Length < 2 ||
                    root[1] != ':')
                {
                    return string.Empty;
                }

                var drive = new DriveInfo(root);
                if (drive.DriveType != DriveType.Fixed || !Directory.Exists(candidateDirectory))
                {
                    return string.Empty;
                }

                return candidateDirectory;
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning(
                    "RoutineEditorWindow",
                    "routine-executable-dialog-initial-directory-failed | result=failure",
                    nameof(ResolveExecutableDialogInitialDirectory),
                    ex);
                return string.Empty;
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isClosed) return;
            UpdateReadinessButton();
            if (_readinessState is { } checkedState &&
                ((!_viewModel.IsEditingAutomaticTrigger && _viewModel.Validate() != null) || CaptureReadinessState(_viewModel.BuildRoutine()) != checkedState))
            {
                _readinessState = null;
                _editRevision++;
                if (!string.IsNullOrEmpty(ReadinessText.Text)) ReadinessText.Text = "Routine changed. Check again for current readiness.";
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                ReadinessCheckedText.Visibility = Visibility.Collapsed;
            }

            if (e.PropertyName == nameof(RoutineEditorViewModel.TriggerAppPath) ||
                e.PropertyName == nameof(RoutineEditorViewModel.SelectedTriggerMode) ||
                e.PropertyName == nameof(RoutineEditorViewModel.RequiredApplicationPath) ||
                e.PropertyName == nameof(RoutineEditorViewModel.TargetAppPath))
            {
                QueuePackagedAppsPreloadIfNeeded();
            }
        }

        private static (string Name, bool Enabled, string Definition, string Triggers) CaptureReadinessState(AudioRoutine routine) =>
            (routine.Name, routine.Enabled, AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine), routine.TriggerFingerprint);

        private void UpdateReadinessButton() =>
            CheckRoutineButton.IsEnabled = !_isClosed && !_isCheckingRoutine && _checkRoutine != null && _viewModel.CanSaveRoutine;

        private void NetworkPicker_DropDownOpened(object sender, EventArgs e)
        {
            if (!_isClosed && !_viewModel.IsScanningNetworks) _ = _viewModel.RefreshNetworksAsync();
        }

        private void AddTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosed || _triggerEditor != null || sender is not Button button) return;
            _viewModel.AddAutomaticTrigger();
            ShowTriggerEditor(button);
        }

        private void EditTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed && _triggerEditor == null && sender is Button { DataContext: RoutineEditorViewModel.TriggerEntry entry } button)
            {
                _viewModel.EditAutomaticTrigger(entry);
                ShowTriggerEditor(button);
            }
        }

        private void ShowTriggerEditor(Button returnFocus)
        {
            if (!_viewModel.IsEditingAutomaticTrigger) return;
            try
            {
                _triggerEditor = new RoutineTriggerEditorWindow(_viewModel, BrowseExecutable, PickPackagedAppAsync);
                _presentTriggerEditor(_triggerEditor);
            }
            finally
            {
                if (_triggerEditor is { IsClosed: false } dialog) dialog.Close();
                _triggerEditor = null;
                if (!_isClosed)
                {
                    _viewModel.CancelTriggerEdit();
                    if (IsActive && returnFocus.IsVisible && returnFocus.IsEnabled) returnFocus.Focus();
                }
            }
        }

        private void RemoveTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: RoutineEditorViewModel.TriggerEntry entry }) _viewModel.RemoveAutomaticTrigger(entry);
        }

        private async void CheckRoutine_Click(object sender, RoutedEventArgs e)
        {
            if (_checkRoutine == null || _isClosed || _isCheckingRoutine) return;
            if (_viewModel.Validate() is { } error)
            {
                ReadinessPanel.Visibility = Visibility.Visible;
                ReadinessText.Text = error;
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "DialogWarningBrush");
                ReadinessCheckedText.Visibility = Visibility.Collapsed;
                return;
            }
            AudioRoutine snapshot = _viewModel.BuildRoutine();
            _readinessState = CaptureReadinessState(snapshot);
            int revision = _editRevision;
            CancellationToken token = _checkLifetime.Token;
            Task<Services.Routines.RoutineReadinessResult>? source = null;
            _isCheckingRoutine = true;
            UpdateReadinessButton();
            try
            {
                source = _checkRoutine(snapshot, token);
                Task<Services.Routines.RoutineReadinessResult> pending = source.WaitAsync(token);
                if (!pending.IsCompleted && await Task.WhenAny(pending, Task.Delay(200, token)) != pending && !_isClosed)
                    CheckRoutineButton.Content = "Checking…";
                var result = await pending;
                if (_isClosed) return;
                ReadinessPanel.Visibility = Visibility.Visible;
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, revision != _editRevision ? "MutedTextBrush" : result.Ready ? "DialogSuccessBrush" : "DialogWarningBrush");
                ReadinessText.Text = revision != _editRevision ? "Routine changed. Check again for current readiness." :
                    ($"{(result.Unavailable ? "Readiness unavailable" : result.Ready ? "Ready to run now" : "Not ready to run now")}\n" +
                    string.Join("\n", result.Checks.Where(static check => check.Status != "ready" || check.Area == "application-routing").Select(static check =>
                        $"• {check.Message}{(check.Target == null ? "" : $" ({check.Target})")}"))).TrimEnd();
                ReadinessCheckedText.Text = $"Checked {result.CheckedUtc.ToLocalTime():T}. Manual-run readiness; automatic triggers are not tested.";
                ReadinessCheckedText.Visibility = revision == _editRevision ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (source != null) _ = source.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                _logger.Warning("RoutineEditorWindow", "routine-readiness-check-failed", nameof(CheckRoutine_Click), ex);
                if (!_isClosed)
                {
                    ReadinessPanel.Visibility = Visibility.Visible;
                    ReadinessText.Text = "Readiness could not be checked. Try again.";
                    ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "DialogWarningBrush");
                    ReadinessCheckedText.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                _isCheckingRoutine = false;
                if (!_isClosed)
                {
                    CheckRoutineButton.Content = "Check routine";
                    UpdateReadinessButton();
                }
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!DialogWindowHelper.TryGetViewModel(this, out RoutineEditorViewModel? viewModel, setDialogResultOnFailure: true))
            {
                return;
            }

            string? validationMessage = viewModel.Validate();
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                await _dialogs.ShowWarningAsync(validationMessage, DialogText.Captions.InvalidSettings, this);
                return;
            }

            ResultRoutine = viewModel.BuildRoutine();
            DialogResult = true;
        }

        private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed && sender is Button button) BrowseExecutable(this, button, _checkLifetime.Token);
        }

        private void BrowseExecutable(Window owner, Button button, CancellationToken cancellationToken)
        {
            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            if (!DialogWindowHelper.TryGetViewModel(this, out RoutineEditorViewModel? viewModel))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Select app executable",
                Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
            };

            bool actionTarget = button.Tag is "Action";
            bool conditionTarget = button.Tag is "Condition";
            if (TryGetExecutableDialogSeed(conditionTarget ? viewModel.RequiredApplicationPath : actionTarget ? viewModel.TargetAppPath : viewModel.TriggerAppPath, out string initialDirectory, out string fileName))
            {
                dialog.FileName = fileName;
                if (!string.IsNullOrWhiteSpace(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }
            }

            if (dialog.ShowDialog(owner) != true || _isClosed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (conditionTarget) viewModel.RequiredApplicationPath = RoutineTriggerPathHelper.NormalizeExecutablePath(dialog.FileName);
            else if (actionTarget) viewModel.TargetAppPath = RoutineTriggerPathHelper.NormalizeExecutablePath(dialog.FileName);
            else viewModel.ApplyExecutablePath(dialog.FileName);
        }

        private async void PickPackagedApp_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed && sender is Button button) await PickPackagedAppAsync(this, button, _checkLifetime.Token);
        }

        private async Task PickPackagedAppAsync(Window owner, Button pickButton, CancellationToken cancellationToken)
        {
            if (_isClosed || _isOpeningPackagedAppPicker ||
                cancellationToken.IsCancellationRequested ||
                !DialogWindowHelper.TryGetViewModel(this, out RoutineEditorViewModel? viewModel))
            {
                return;
            }

            bool actionTarget = pickButton.Tag is "Action";
            bool conditionTarget = pickButton.Tag is "Condition";
            _isOpeningPackagedAppPicker = true;
            object? originalContent = pickButton.Content;
            bool pickerShown = false;
            Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>? pendingLoad = null;
            pickButton.IsEnabled = false;
            pickButton.Content = "Loading...";
            try
            {
                pendingLoad = LoadPackagedAppsAsync(forceRefresh: false);
                IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> apps = await pendingLoad.WaitAsync(cancellationToken);
                if (_isClosed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var packagedAppPickerViewModel = new PackagedAppPickerViewModel(apps);
                packagedAppPickerViewModel.TrySelectAppUserModelId(conditionTarget ? viewModel.RequiredApplicationPath : actionTarget ? viewModel.TargetAppPath : viewModel.TriggerAppPath);
                var picker = new PackagedAppPickerWindow(packagedAppPickerViewModel, RefreshPackagedAppsForPickerAsync, _logger, _dialogs);

                pickerShown = true;
                if (DialogWindowHelper.ShowOwnedDialog(picker, owner) == true && !_isClosed && !cancellationToken.IsCancellationRequested
                    && !string.IsNullOrWhiteSpace(picker.SelectedAppUserModelId))
                {
                    if (conditionTarget) viewModel.RequiredApplicationPath = picker.SelectedAppUserModelId;
                    else if (actionTarget) viewModel.TargetAppPath = picker.SelectedAppUserModelId;
                    else
                    {
                        viewModel.ApplyPackagedAppId(picker.SelectedAppUserModelId);
                    }
                    viewModel.SetResolvedPackagedAppDisplayName(picker.SelectedAppDisplayName, picker.SelectedAppUserModelId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (pendingLoad != null)
                    _ = pendingLoad.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                if (_isClosed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _logger.Warning(
                    "RoutineEditorWindow",
                    "routine-packaged-app-picker-open-failed | result=failure forceRefresh=false",
                    nameof(PickPackagedApp_Click),
                    ex);
                try
                {
                    await _dialogs.ShowInformationAsync("The packaged app list could not be loaded right now.", DialogText.Captions.InvalidSettings, owner, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
            finally
            {
                _isOpeningPackagedAppPicker = false;
                if (!_isClosed && !cancellationToken.IsCancellationRequested)
                {
                    pickButton.IsEnabled = true;
                    pickButton.Content = originalContent;
                    if (pickerShown && owner.IsActive) pickButton.Focus();
                }
            }
        }

        private Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> RefreshPackagedAppsForPickerAsync()
        {
            return LoadPackagedAppsAsync(forceRefresh: true);
        }

        private void ClearHotkeyText_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetHotkeyTextBoxContext(sender, out TextBox textBox, out IHotkeySink target))
            {
                return;
            }

            target.Reset();
            textBox.Text = target.DisplayText;
            textBox.SelectionLength = 0;
            textBox.SelectionStart = textBox.Text.Length;
        }

        private static bool TryGetHotkeyTextBoxContext(
            object sender,
            out TextBox textBox,
            out IHotkeySink target)
        {
            textBox = null!;
            target = null!;

            if (sender is not FrameworkElement { Parent: ContextMenu { PlacementTarget: TextBox placementTarget } })
            {
                return false;
            }

            textBox = placementTarget;
            foreach (Behavior behavior in Interaction.GetBehaviors(textBox))
            {
                if (behavior is HotkeyCaptureBehavior { Target: not null } hotkeyBehavior)
                {
                    target = hotkeyBehavior.Target;
                    return true;
                }
            }

            return false;
        }
    }
}

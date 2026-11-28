using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Controls;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;
using Microsoft.Win32;

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
        private enum AppTargetSlot
        {
            Trigger,
            RoutingTarget,
            RequiredCondition,
        }

        private bool _isClosed;
        private bool _isOpeningPackagedAppPicker;
        private bool _isOpeningRunningAppPicker;
        private bool _allowCloseWithoutPrompt;
        private bool _isConfirmingDiscardClose;
        private bool _isConfirmingReset;
        private InputLanguageManager? _inputLanguageManager;
        private bool _userPreferenceHandlerRegistered;
        private readonly Func<AudioRoutine, CancellationToken, Task<Services.Routines.RoutineReadinessResult>>? _checkRoutine;
        private readonly Func<AudioRoutine, CancellationToken, Task<RoutineExecutionResult>>? _testRoutine;
        private readonly CancellationTokenSource _checkLifetime = new();
        private int _editRevision;
        private (string Name, bool Enabled, string Definition, string Triggers)? _readinessState;
        private bool _isCheckingRoutine;
        private bool _isTestingRoutine;
        private bool _isWideLayout = true;
        private bool _hasReadinessOrTestStatus;
        private sealed record ReadinessCheckRow(string StatusGlyph, Brush StatusBrush, Func<LocalizationService, string> DescriptionPresentation);

        private const double WideLayoutThreshold = 680d;
        private const double StackedFooterThreshold = 540d;
        private RoutineTriggerEditorWindow? _triggerEditor;
        private readonly Action<RoutineTriggerEditorWindow>? _presentTriggerEditorOverride;
        private Button? _inlineTriggerReturnFocus;

        internal RoutineEditorWindow(RoutineEditorViewModel viewModel, ILogger? logger = null, IAppDialogService? dialogService = null,
            Func<AudioRoutine, CancellationToken, Task<Services.Routines.RoutineReadinessResult>>? checkRoutine = null,
            Action<RoutineTriggerEditorWindow>? presentTriggerEditor = null, LocalizationService? localization = null,
            Func<AudioRoutine, CancellationToken, Task<RoutineExecutionResult>>? testRoutine = null)
        {
            _checkRoutine = checkRoutine;
            _testRoutine = testRoutine;
            _presentTriggerEditorOverride = presentTriggerEditor;
            _logger = logger ?? Logger.Instance;
            _viewModel = viewModel;
            _dialogs = dialogService ?? AppDialogServiceProvider.Current;
            if (localization != null) LocalizationContext.SetService(this, localization);
            InitializeComponent();
            CheckRoutineButton.Visibility = checkRoutine == null ? Visibility.Collapsed : Visibility.Visible;
            TestRoutineButton.Visibility = testRoutine == null ? Visibility.Collapsed : Visibility.Visible;
            DialogWindowHelper.Initialize(this, viewModel);
            UpdateResponsiveLayout(Width);
            UpdateReadinessButton();
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _userPreferenceHandlerRegistered = true;
            _inputLanguageManager = InputLanguageManagerHelper.TryGetCurrent();
            _inputLanguageManager?.InputLanguageChanged += OnInputLanguageChanged;

            QueuePackagedAppsPreloadIfNeeded();
        }

        private void RoutineEditorWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        }

        internal void UpdateResponsiveLayout(double availableWidth)
        {
            bool wide = availableWidth >= WideLayoutThreshold;
            _isWideLayout = wide;
            PreviewRailColumn.Width = wide ? new GridLength(300) : new GridLength(0);
            PreviewRailSpacerColumn.Width = wide ? new GridLength(14) : new GridLength(0);
            PreviewRailBorder.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            RoutineSummaryText.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            UpdateNarrowValidationVisibility();
            if (_hasReadinessOrTestStatus)
            {
                ReadinessPanel.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
                RailReadinessCard.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            }

            bool stackFooter = availableWidth < StackedFooterThreshold;
            Grid.SetRow(RoutineActions, stackFooter ? 1 : 0);
            Grid.SetColumn(RoutineActions, stackFooter ? 0 : 1);
            Grid.SetColumnSpan(RoutineActions, stackFooter ? 2 : 1);
            Grid.SetColumnSpan(FooterSecondaryActions, stackFooter ? 2 : 1);
            RoutineActions.Margin = stackFooter ? new Thickness(0, 4, 0, 0) : new Thickness(8, 0, 0, 4);
        }

        private void UpdateNarrowValidationVisibility()
        {
            NarrowValidationBanner.Visibility = !_isWideLayout && _viewModel.HasValidationIssues
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
        }

        private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            AppHelpTopic topic = Keyboard.FocusedElement is DependencyObject target ? AppHelp.GetTopic(target) : AppHelpTopic.None;
            _ = AppHelp.TryOpen(_logger, "RoutineEditorWindow", topic == AppHelpTopic.None ? AppHelpTopic.Routines : topic);
            e.Handled = true;
        }

        public AudioRoutine? ResultRoutine { get; private set; }

        internal void AllowCloseWithoutPrompt() => _allowCloseWithoutPrompt = true;

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || _isClosed || _allowCloseWithoutPrompt || App.IsSessionEnding || DialogResult == true || !IsVisible)
            {
                return;
            }

            if (!_viewModel.HasUnsavedChanges && !_viewModel.IsEditingAutomaticTrigger)
            {
                return;
            }

            e.Cancel = true;
            if (_isConfirmingDiscardClose)
            {
                return;
            }

            _ = ConfirmDiscardChangesAndCloseAsync();
        }

        internal async Task<bool> ConfirmDiscardChangesAsync()
        {
            if (_isClosed || (!_viewModel.HasUnsavedChanges && !_viewModel.IsEditingAutomaticTrigger))
            {
                return true;
            }

            AppDialogResult result = await _dialogs.ShowAsync(AppDialogRequest.ConfirmDiscardRoutineChanges(this));
            return result == AppDialogResult.Confirmed;
        }

        private async Task ConfirmDiscardChangesAndCloseAsync()
        {
            _isConfirmingDiscardClose = true;
            try
            {
                if (await ConfirmDiscardChangesAsync() && !_isClosed)
                {
                    _allowCloseWithoutPrompt = true;
                    Close();
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _isConfirmingDiscardClose = false;
            }
        }

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
            AudioDeviceHelper.ClearPackagedAppInventoryCache(waitForDiscovery: false);
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
                CancellationToken lifetimeToken = _isClosed
                    ? new CancellationToken(canceled: true)
                    : (_checkLifetime?.Token ?? CancellationToken.None);
                _packagedAppsTask = Task.Run(() =>
                {
                    if (lifetimeToken.IsCancellationRequested)
                    {
                        return (IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>)[];
                    }

                    if (forceRefresh)
                    {
                        AudioDeviceHelper.ClearPackagedAppInventoryCache();
                    }

                    IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> result = AudioDeviceHelper.GetInstalledPackagedApps();
                    if (lifetimeToken.IsCancellationRequested)
                    {
                        AudioDeviceHelper.ClearPackagedAppInventoryCache(waitForDiscovery: false);
                        return [];
                    }

                    return result;
                }, lifetimeToken);
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
            if (e.PropertyName == nameof(RoutineEditorViewModel.HasValidationIssues))
            {
                UpdateNarrowValidationVisibility();
            }
            UpdateReadinessButton();
            if (_readinessState is { } checkedState &&
                RoutineEditorViewModel.AffectsReadinessOrSummary(e.PropertyName) &&
                ((!_viewModel.IsEditingAutomaticTrigger && _viewModel.ValidateMessage() != null) || CaptureReadinessState(_viewModel.BuildRoutine()) != checkedState))
            {
                _readinessState = null;
                _editRevision++;
                if (!string.IsNullOrEmpty(ReadinessText.Text)) LocalizationContext.SetText(ReadinessText, TextBlock.TextProperty, new("Routine_ReadinessChanged"));
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                ReadinessCheckedText.Visibility = Visibility.Collapsed;
                if (RailReadinessCard.Visibility == Visibility.Visible)
                {
                    LocalizationContext.SetText(RailReadinessSummaryText, TextBlock.TextProperty, new("Routine_ReadinessChanged"));
                    RailReadinessSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    RailReadinessChecksList.ItemsSource = null;
                    RailReadinessCheckedText.Visibility = Visibility.Collapsed;
                }
            }

            if (e.PropertyName == nameof(RoutineEditorViewModel.IsEditingAutomaticTrigger) && !_viewModel.IsEditingAutomaticTrigger)
            {
                InlineTriggerEditorCard.Visibility = Visibility.Collapsed;
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

        private void UpdateReadinessButton()
        {
            bool busy = _isClosed || _isCheckingRoutine || _isTestingRoutine || !_viewModel.CanSaveRoutine;
            CheckRoutineButton.IsEnabled = !busy && _checkRoutine != null;
            TestRoutineButton.IsEnabled = !busy && _testRoutine != null;
            ResetRoutineButton.IsEnabled = !_isClosed && !_isCheckingRoutine && !_isTestingRoutine && !_isConfirmingReset && _viewModel.CanResetChanges;
        }

        private async void ResetRoutine_Click(object sender, RoutedEventArgs e)
        {
            await ConfirmAndResetAsync();
        }

        internal async Task<bool> ConfirmAndResetAsync()
        {
            if (_isClosed || _isConfirmingReset || _isCheckingRoutine || _isTestingRoutine || !_viewModel.CanResetChanges)
            {
                return false;
            }

            _isConfirmingReset = true;
            UpdateReadinessButton();
            try
            {
                AppDialogResult result = await _dialogs.ShowAsync(AppDialogRequest.ConfirmResetRoutineChanges(this));
                if (result != AppDialogResult.Confirmed || _isClosed)
                {
                    return false;
                }

                _viewModel.ResetToInitialState();
                InlineTriggerEditorCard.Visibility = Visibility.Collapsed;
                _inlineTriggerReturnFocus = null;
                _readinessState = null;
                _hasReadinessOrTestStatus = false;
                _editRevision++;
                ReadinessPanel.Visibility = Visibility.Collapsed;
                ReadinessText.Text = string.Empty;
                ReadinessCheckedText.Visibility = Visibility.Collapsed;
                RailReadinessCard.Visibility = Visibility.Collapsed;
                RailReadinessChecksList.ItemsSource = null;
                RailReadinessCheckedText.Visibility = Visibility.Collapsed;
                UpdateNarrowValidationVisibility();
                RoutineEditorScrollViewer.ScrollToTop();
                RoutineNameTextBox.Focus();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                _isConfirmingReset = false;
                if (!_isClosed) UpdateReadinessButton();
            }
        }

        private void ApplyGamingTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosed) return;
            InlineTriggerEditorCard.Visibility = Visibility.Collapsed;
            _viewModel.ApplyStarterTemplate(RoutineStarterTemplate.GamingHeadset, LocalizationContext.GetService(this));
        }

        private void ApplyWorkTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosed) return;
            InlineTriggerEditorCard.Visibility = Visibility.Collapsed;
            _viewModel.ApplyStarterTemplate(RoutineStarterTemplate.WorkCalls, LocalizationContext.GetService(this));
        }

        private void ApplyNightTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosed) return;
            InlineTriggerEditorCard.Visibility = Visibility.Collapsed;
            _viewModel.ApplyStarterTemplate(RoutineStarterTemplate.NightVolumeCap, LocalizationContext.GetService(this));
        }

        private void ApplyPerAppTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosed) return;
            InlineTriggerEditorCard.Visibility = Visibility.Collapsed;
            _viewModel.ApplyStarterTemplate(RoutineStarterTemplate.PerAppRouting, LocalizationContext.GetService(this));
            RoutingTargetApplicationPicker.TargetTextBox.Focus();
        }

        private void ConditionDayOfWeekPicker_PresetSelected(object? _, DaySelectionPreset preset)
        {
            if (!_isClosed) _viewModel.ApplyConditionDayPreset(preset);
        }

        private void DismissStarterTemplates_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed) _viewModel.DismissStarterTemplates();
        }

        private async void TestRoutine_Click(object sender, RoutedEventArgs e)
        {
            if (_testRoutine == null || _isClosed || _isTestingRoutine || _isCheckingRoutine) return;
            if (_viewModel.ValidateMessage() is { } error)
            {
                _viewModel.MarkValidationAttempted();
                if (_viewModel.ValidationIssues is [var firstIssue, ..])
                {
                    NavigateToValidationIssue(firstIssue);
                }
                _readinessState = null;
                _hasReadinessOrTestStatus = true;
                ReadinessPanel.Visibility = _isWideLayout ? Visibility.Collapsed : Visibility.Visible;
                LocalizationContext.SetText(ReadinessText, TextBlock.TextProperty, error);
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "DialogWarningBrush");
                ReadinessCheckedText.Visibility = Visibility.Collapsed;
                UpdateRailReadiness(null, error, stale: false);
                return;
            }

            AudioRoutine snapshot = _viewModel.BuildRoutine();
            _readinessState = null;
            CancellationToken token = _checkLifetime.Token;
            _isTestingRoutine = true;
            UpdateReadinessButton();
            LocalizationContext.SetText(TestRoutineButton, ContentControl.ContentProperty, new("Routine_Testing"));
            try
            {
                RoutineExecutionResult result = await _testRoutine(snapshot, token);
                if (_isClosed) return;
                string StatusPresentation(LocalizationService localization) => result.Success
                    ? localization["Routine_TestRunSucceeded"]
                    : result.Skipped
                        ? localization.FormatKey("Routine_TestRunSkipped", string.IsNullOrWhiteSpace(result.SkipReason) ? string.Empty : AppMessages.Translate(result.SkipReason, localization))
                        : localization["Routine_TestRunFailed"];
                string brushKey = result.Success ? "DialogSuccessBrush" : "DialogWarningBrush";

                _hasReadinessOrTestStatus = true;
                ReadinessPanel.Visibility = _isWideLayout ? Visibility.Collapsed : Visibility.Visible;
                LocalizationContext.SetPresentation(ReadinessText, TextBlock.TextProperty, StatusPresentation);
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
                ReadinessCheckedText.Visibility = Visibility.Collapsed;

                RailReadinessCard.Visibility = _isWideLayout ? Visibility.Visible : Visibility.Collapsed;
                LocalizationContext.SetPresentation(RailReadinessSummaryText, TextBlock.TextProperty, StatusPresentation);
                RailReadinessSummaryText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
                RailReadinessChecksList.ItemsSource = null;
                RailReadinessCheckedText.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("RoutineEditorWindow", "routine-test-run-failed", nameof(TestRoutine_Click), ex);
                if (!_isClosed)
                {
                    LocalizedText failed = new("Routine_TestRunFailed");
                    _hasReadinessOrTestStatus = true;
                    ReadinessPanel.Visibility = _isWideLayout ? Visibility.Collapsed : Visibility.Visible;
                    LocalizationContext.SetText(ReadinessText, TextBlock.TextProperty, failed);
                    ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "DialogWarningBrush");
                    ReadinessCheckedText.Visibility = Visibility.Collapsed;
                    UpdateRailReadiness(null, failed, stale: false);
                }
            }
            finally
            {
                _isTestingRoutine = false;
                if (!_isClosed)
                {
                    LocalizationContext.SetText(TestRoutineButton, ContentControl.ContentProperty, new("Routine_TestRun"));
                    UpdateReadinessButton();
                }
            }
        }

        private void NetworkPicker_DropDownOpened(object sender, EventArgs e)
        {
            if (!_isClosed && !_viewModel.IsScanningNetworks) _ = _viewModel.RefreshNetworksAsync();
        }

        private void CaptureCurrentSetup_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed) _viewModel.CaptureCurrentAudioSetup();
        }

        private void SystemRoutingOption_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed) _viewModel.SwitchToSystemRouting();
        }

        private void ApplicationRoutingOption_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed) _viewModel.SwitchToApplicationRouting();
        }

        private void TriggerDelayPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed && sender is Button { Tag: string tag } && int.TryParse(tag, out int seconds))
            {
                _viewModel.TriggerDelaySeconds = seconds;
            }
        }

        private void ValidationIssue_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed && sender is Button { DataContext: RoutineValidationIssue issue })
            {
                NavigateToValidationIssue(issue);
            }
        }

        internal void NavigateToValidationIssue(RoutineValidationIssue issue)
        {
            _viewModel.SelectedSection = issue.Section;
            FrameworkElement? target = null;
            bool selectAll = false;
            switch (issue.FieldKey)
            {
                case "RoutineName":
                    target = RoutineNameTextBox;
                    selectAll = true;
                    break;
                case "Volume":
                case "MasterVolume":
                    VolumeTargetsExpander.IsExpanded = true;
                    target = MasterVolumeTextBox;
                    break;
                case "MicVolume":
                    VolumeTargetsExpander.IsExpanded = true;
                    target = MicVolumeTextBox;
                    break;
                case "AppVolume":
                    VolumeTargetsExpander.IsExpanded = true;
                    target = AppVolumeTextBox;
                    break;
                case "TargetApp":
                    target = RoutingTargetApplicationPicker.TargetTextBox;
                    break;
                case "OutputDevice":
                    target = OutputDeviceBox;
                    break;
                case "Communications":
                    CommunicationsExpander.IsExpanded = true;
                    target = CommunicationsOutputBox;
                    break;
                case "Hotkey":
                    target = RoutineHotkeyBox.CaptureTextBox;
                    break;
                case "HotkeyCycleGroup":
                    HotkeyCyclingExpander.IsExpanded = true;
                    target = HotkeyCycleGroupBox;
                    break;
                case "TriggerDelay":
                    target = TriggerDelayTextBox;
                    break;
                case "TriggerEditor":
                    if (InlineTriggerEditorCard.Visibility == Visibility.Visible)
                    {
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (_isClosed) return;
                            InlineTriggerEditorCard.BringIntoView();
                            InlineTriggerEditorPanel.FocusTriggerType();
                        }, DispatcherPriority.Loaded);
                    }
                    else
                    {
                        target = NewTriggerButton;
                    }
                    break;
                case "TimeCondition":
                    ConditionsExpander.IsExpanded = true;
                    target = RequireTimeWindowBox;
                    break;
                case "RequiredDevice":
                    ConditionsExpander.IsExpanded = true;
                    target = RequiredDeviceBox;
                    break;
                case "RequiredApplication":
                    ConditionsExpander.IsExpanded = true;
                    target = RequiredApplicationPicker.TargetTextBox;
                    break;
            }

            if (target == null) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_isClosed) return;
                target.BringIntoView();
                target.Focus();
                if (selectAll && target is TextBox box) box.SelectAll();
            }, DispatcherPriority.Loaded);
        }

        private void AddTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosed || _triggerEditor != null || sender is not Button button) return;
            _viewModel.SelectedSection = RoutineEditorSection.Triggers;
            _viewModel.AddAutomaticTrigger();
            ShowTriggerEditor(button);
        }

        private void EditTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (!_isClosed && _triggerEditor == null && sender is Button { DataContext: RoutineEditorViewModel.TriggerEntry entry } button)
            {
                _viewModel.SelectedSection = RoutineEditorSection.Triggers;
                _viewModel.EditAutomaticTrigger(entry);
                ShowTriggerEditor(button);
            }
        }

        private void TriggerRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && !_isClosed && _triggerEditor == null && !_viewModel.IsEditingAutomaticTrigger
                && sender is Border { DataContext: RoutineEditorViewModel.TriggerEntry entry })
            {
                _viewModel.SelectedSection = RoutineEditorSection.Triggers;
                _viewModel.EditAutomaticTrigger(entry);
                ShowTriggerEditor(NewTriggerButton);
                e.Handled = true;
            }
        }

        private void ShowTriggerEditor(Button returnFocus)
        {
            if (!_viewModel.IsEditingAutomaticTrigger) return;
            if (_presentTriggerEditorOverride == null)
            {
                _inlineTriggerReturnFocus = returnFocus;
                InlineTriggerEditorCard.Visibility = Visibility.Visible;
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_isClosed || InlineTriggerEditorCard.Visibility != Visibility.Visible) return;
                    InlineTriggerEditorCard.BringIntoView();
                    InlineTriggerEditorPanel.FocusTriggerType();
                }, DispatcherPriority.Loaded);
                return;
            }

            try
            {
                _triggerEditor = new RoutineTriggerEditorWindow(
                    _viewModel,
                    (owner, button, token) => BrowseExecutable(owner, AppTargetSlot.Trigger, token),
                    (owner, button, token) => PickPackagedAppAsync(owner, AppTargetSlot.Trigger, button, token),
                    (owner, button, token) => PickRunningAppAsync(owner, AppTargetSlot.Trigger, button, token));
                LocalizationContext.SetService(_triggerEditor, LocalizationContext.GetService(this));
                _presentTriggerEditorOverride(_triggerEditor);
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

        private void InlineTrigger_ConfirmRequested(object sender, RoutedEventArgs e)
        {
            if (_isClosed) return;
            if (_viewModel.ConfirmTriggerEdit())
            {
                InlineTriggerEditorCard.Visibility = Visibility.Collapsed;
                if (_inlineTriggerReturnFocus is { IsVisible: true, IsEnabled: true } focus) focus.Focus();
                _inlineTriggerReturnFocus = null;
            }
        }

        private void InlineTrigger_CancelRequested(object sender, RoutedEventArgs e)
        {
            if (_isClosed) return;
            _viewModel.CancelTriggerEdit();
            InlineTriggerEditorCard.Visibility = Visibility.Collapsed;
            if (_inlineTriggerReturnFocus is { IsVisible: true, IsEnabled: true } focus) focus.Focus();
            _inlineTriggerReturnFocus = null;
        }

        private void InlineTrigger_BrowseRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) BrowseExecutable(this, AppTargetSlot.Trigger, _checkLifetime.Token);
        }

        private async void InlineTrigger_RunningAppRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) await PickRunningAppAsync(this, AppTargetSlot.Trigger, e.SourceButton, _checkLifetime.Token);
        }

        private async void InlineTrigger_PackagedAppRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) await PickPackagedAppAsync(this, AppTargetSlot.Trigger, e.SourceButton, _checkLifetime.Token);
        }

        private void RemoveTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: RoutineEditorViewModel.TriggerEntry entry }) _viewModel.RemoveAutomaticTrigger(entry);
        }

        private void UpdateRailReadiness(Services.Routines.RoutineReadinessResult? result, LocalizedText? error, bool stale)
        {
            RailReadinessCard.Visibility = _isWideLayout ? Visibility.Visible : Visibility.Collapsed;
            if (error != null)
            {
                LocalizationContext.SetText(RailReadinessSummaryText, TextBlock.TextProperty, error);
                RailReadinessSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "DialogWarningBrush");
                RailReadinessChecksList.ItemsSource = null;
                RailReadinessCheckedText.Visibility = Visibility.Collapsed;
                return;
            }

            if (result == null)
            {
                RailReadinessCard.Visibility = Visibility.Collapsed;
                return;
            }

            if (stale)
            {
                LocalizationContext.SetText(RailReadinessSummaryText, TextBlock.TextProperty, new("Routine_ReadinessChanged"));
                RailReadinessSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                RailReadinessChecksList.ItemsSource = null;
                RailReadinessCheckedText.Visibility = Visibility.Collapsed;
                return;
            }

            string headingKey = result.Unavailable ? "Routine_ReadinessUnavailable" : result.Ready ? "Routine_Ready" : "Routine_NotReady";
            LocalizationContext.SetText(RailReadinessSummaryText, TextBlock.TextProperty, new(headingKey));
            RailReadinessSummaryText.SetResourceReference(TextBlock.ForegroundProperty, result.Ready ? "DialogSuccessBrush" : "DialogWarningBrush");

            Brush readyBrush = TryFindResource("DialogSuccessBrush") as Brush ?? Brushes.MediumSeaGreen;
            Brush warningBrush = TryFindResource("DialogWarningBrush") as Brush ?? Brushes.Goldenrod;
            Brush errorBrush = TryFindResource("DialogErrorBrush") as Brush ?? Brushes.IndianRed;

            RailReadinessChecksList.ItemsSource = result.Checks.Select(check =>
            {
                string glyph = check.Status == "ready" ? "✓" : check.Status == "blocked" ? "✗" : "⚠";
                Brush brush = check.Status == "ready" ? readyBrush : check.Status == "blocked" ? errorBrush : warningBrush;
                string Presentation(LocalizationService loc)
                {
                    string text = check.UiMessage == null ? AppMessages.Translate(check.Message, loc) : loc.Format(check.UiMessage);
                    return string.IsNullOrWhiteSpace(check.Target) ? text : $"{text} ({check.Target})";
                }
                return new ReadinessCheckRow(glyph, brush, Presentation);
            }).ToList();

            LocalizationContext.SetText(RailReadinessCheckedText, TextBlock.TextProperty, new("Routine_ReadinessChecked", result.CheckedUtc.ToLocalTime()));
            RailReadinessCheckedText.Visibility = Visibility.Visible;
        }

        private async void CheckRoutine_Click(object sender, RoutedEventArgs e)
        {
            if (_checkRoutine == null || _isClosed || _isCheckingRoutine || _isTestingRoutine) return;
            if (_viewModel.ValidateMessage() is { } error)
            {
                _viewModel.MarkValidationAttempted();
                if (_viewModel.ValidationIssues is [var firstIssue, ..])
                {
                    NavigateToValidationIssue(firstIssue);
                }
                _hasReadinessOrTestStatus = true;
                ReadinessPanel.Visibility = _isWideLayout ? Visibility.Collapsed : Visibility.Visible;
                LocalizationContext.SetText(ReadinessText, TextBlock.TextProperty, error);
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "DialogWarningBrush");
                ReadinessCheckedText.Visibility = Visibility.Collapsed;
                UpdateRailReadiness(null, error, stale: false);
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
                    LocalizationContext.SetText(CheckRoutineButton, ContentControl.ContentProperty, new("Routine_Checking"));
                var result = await pending;
                if (_isClosed) return;
                bool stale = revision != _editRevision;
                _hasReadinessOrTestStatus = true;
                ReadinessPanel.Visibility = _isWideLayout ? Visibility.Collapsed : Visibility.Visible;
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, stale ? "MutedTextBrush" : result.Ready ? "DialogSuccessBrush" : "DialogWarningBrush");
                if (stale)
                {
                    LocalizationContext.SetText(ReadinessText, TextBlock.TextProperty, new("Routine_ReadinessChanged"));
                }
                else
                {
                    LocalizationContext.SetPresentation(ReadinessText, TextBlock.TextProperty, result.FormatForUi);
                }
                LocalizationContext.SetText(ReadinessCheckedText, TextBlock.TextProperty, new("Routine_ReadinessChecked", result.CheckedUtc.ToLocalTime()));
                ReadinessCheckedText.Visibility = stale ? Visibility.Collapsed : Visibility.Visible;
                UpdateRailReadiness(result, null, stale);
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
                    _hasReadinessOrTestStatus = true;
                    ReadinessPanel.Visibility = _isWideLayout ? Visibility.Collapsed : Visibility.Visible;
                    LocalizationContext.SetText(ReadinessText, TextBlock.TextProperty, new("Routine_ReadinessFailed"));
                    ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "DialogWarningBrush");
                    ReadinessCheckedText.Visibility = Visibility.Collapsed;
                    UpdateRailReadiness(null, new("Routine_ReadinessFailed"), stale: false);
                }
            }
            finally
            {
                _isCheckingRoutine = false;
                if (!_isClosed)
                {
                    LocalizationContext.SetText(CheckRoutineButton, ContentControl.ContentProperty, new("Ui_CheckRoutine"));
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

            if (viewModel.ValidateMessage() is { } validationMessage)
            {
                viewModel.MarkValidationAttempted();
                if (viewModel.ValidationIssues is [var firstIssue, ..])
                {
                    NavigateToValidationIssue(firstIssue);
                }
                await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(validationMessage, DialogText.Captions.InvalidSettings, AppDialogKind.Warning, this));
                return;
            }

            ResultRoutine = viewModel.BuildRoutine();
            DialogResult = true;
        }

        private void RoutingTargetApp_BrowseRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) BrowseExecutable(this, AppTargetSlot.RoutingTarget, _checkLifetime.Token);
        }

        private async void RoutingTargetApp_RunningAppRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) await PickRunningAppAsync(this, AppTargetSlot.RoutingTarget, e.SourceButton, _checkLifetime.Token);
        }

        private async void RoutingTargetApp_PackagedAppRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) await PickPackagedAppAsync(this, AppTargetSlot.RoutingTarget, e.SourceButton, _checkLifetime.Token);
        }

        private void RequiredConditionApp_BrowseRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) BrowseExecutable(this, AppTargetSlot.RequiredCondition, _checkLifetime.Token);
        }

        private async void RequiredConditionApp_RunningAppRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) await PickRunningAppAsync(this, AppTargetSlot.RequiredCondition, e.SourceButton, _checkLifetime.Token);
        }

        private async void RequiredConditionApp_PackagedAppRequested(object sender, AppTargetPickerActionEventArgs e)
        {
            if (!_isClosed) await PickPackagedAppAsync(this, AppTargetSlot.RequiredCondition, e.SourceButton, _checkLifetime.Token);
        }

        private static string GetAppTargetPath(RoutineEditorViewModel viewModel, AppTargetSlot slot) => slot switch
        {
            AppTargetSlot.RoutingTarget => viewModel.TargetAppPath,
            AppTargetSlot.RequiredCondition => viewModel.RequiredApplicationPath,
            _ => viewModel.TriggerAppPath,
        };

        private static void ApplyAppTargetPath(RoutineEditorViewModel viewModel, AppTargetSlot slot, string targetPath, bool isPackagedApp, string? displayName = null)
        {
            if (isPackagedApp)
            {
                if (slot == AppTargetSlot.RequiredCondition) viewModel.RequiredApplicationPath = targetPath;
                else if (slot == AppTargetSlot.RoutingTarget) viewModel.TargetAppPath = targetPath;
                else viewModel.ApplyPackagedAppId(targetPath);

                viewModel.SetResolvedPackagedAppDisplayName(displayName, targetPath);
                return;
            }

            string normalized = RoutineTriggerPathHelper.NormalizeExecutablePath(targetPath);
            if (slot == AppTargetSlot.RequiredCondition) viewModel.RequiredApplicationPath = normalized;
            else if (slot == AppTargetSlot.RoutingTarget) viewModel.TargetAppPath = normalized;
            else viewModel.ApplyExecutablePath(normalized);
        }

        private void BrowseExecutable(Window owner, AppTargetSlot slot, CancellationToken cancellationToken)
        {
            if (_isClosed || cancellationToken.IsCancellationRequested) return;
            if (!DialogWindowHelper.TryGetViewModel(this, out RoutineEditorViewModel? viewModel))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = LocalizationContext.GetService(this)["Routine_ExecutableTitle"],
                Filter = LocalizationContext.GetService(this)["Routine_ExecutableFilter"],
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
            };

            if (TryGetExecutableDialogSeed(GetAppTargetPath(viewModel, slot), out string initialDirectory, out string fileName))
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

            ApplyAppTargetPath(viewModel, slot, dialog.FileName, isPackagedApp: false);
        }

        private async Task PickRunningAppAsync(Window owner, AppTargetSlot slot, Button pickButton, CancellationToken cancellationToken)
        {
            if (_isClosed || _isOpeningRunningAppPicker ||
                cancellationToken.IsCancellationRequested ||
                !DialogWindowHelper.TryGetViewModel(this, out RoutineEditorViewModel? viewModel))
            {
                return;
            }

            _isOpeningRunningAppPicker = true;
            bool pickerShown = false;
            pickButton.IsEnabled = false;
            try
            {
                string preferredTargetPath = GetAppTargetPath(viewModel, slot);
                var picker = new RunningAppPickerWindow(
                    new RunningAppPickerViewModel([]),
                    token => Task.Run(() => RunningApplicationDiscovery.Discover(), token),
                    _logger,
                    _dialogs,
                    loadOnOpen: true,
                    preferredTargetPath: preferredTargetPath);
                LocalizationContext.SetService(picker, LocalizationContext.GetService(this));

                pickerShown = true;
                if (DialogWindowHelper.ShowOwnedDialog(picker, owner) == true && !_isClosed && !cancellationToken.IsCancellationRequested
                    && !string.IsNullOrWhiteSpace(picker.SelectedTargetPath))
                {
                    ApplyAppTargetPath(viewModel, slot, picker.SelectedTargetPath, picker.SelectedIsPackagedApp, picker.SelectedDisplayName);
                }
            }
            catch (Exception ex)
            {
                if (_isClosed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _logger.Warning("RoutineEditorWindow", "routine-running-app-picker-open-failed | result=failure", nameof(PickRunningAppAsync), ex);
                try
                {
                    await _dialogs.ShowInformationAsync("The running app list could not be loaded right now.", DialogText.Captions.InvalidSettings, owner, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
            finally
            {
                _isOpeningRunningAppPicker = false;
                if (!_isClosed && !cancellationToken.IsCancellationRequested)
                {
                    pickButton.IsEnabled = true;
                    if (pickerShown && owner.IsActive) pickButton.Focus();
                }
            }
        }

        private async Task PickPackagedAppAsync(Window owner, AppTargetSlot slot, Button pickButton, CancellationToken cancellationToken)
        {
            if (_isClosed || _isOpeningPackagedAppPicker ||
                cancellationToken.IsCancellationRequested ||
                !DialogWindowHelper.TryGetViewModel(this, out RoutineEditorViewModel? viewModel))
            {
                return;
            }

            _isOpeningPackagedAppPicker = true;
            bool pickerShown = false;
            pickButton.IsEnabled = false;
            try
            {
                string preferredAppUserModelId = GetAppTargetPath(viewModel, slot);
                var picker = new PackagedAppPickerWindow(new PackagedAppPickerViewModel([]), RefreshPackagedAppsForPickerAsync, _logger, _dialogs,
                    initialLoad: () => LoadPackagedAppsAsync(forceRefresh: false), preferredAppUserModelId: preferredAppUserModelId);
                LocalizationContext.SetService(picker, LocalizationContext.GetService(this));

                pickerShown = true;
                if (DialogWindowHelper.ShowOwnedDialog(picker, owner) == true && !_isClosed && !cancellationToken.IsCancellationRequested
                    && !string.IsNullOrWhiteSpace(picker.SelectedAppUserModelId))
                {
                    ApplyAppTargetPath(viewModel, slot, picker.SelectedAppUserModelId, isPackagedApp: true, picker.SelectedAppDisplayName);
                }
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
                    nameof(PickPackagedAppAsync),
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
                    if (pickerShown && owner.IsActive) pickButton.Focus();
                }
            }
        }

        private Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> RefreshPackagedAppsForPickerAsync()
        {
            return LoadPackagedAppsAsync(forceRefresh: true);
        }
    }
}

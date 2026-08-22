using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Constants;
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
        private InputLanguageManager? _inputLanguageManager;
        private bool _userPreferenceHandlerRegistered;

        internal RoutineEditorWindow(RoutineEditorViewModel viewModel, ILogger? logger = null, IAppDialogService? dialogService = null)
        {
            _logger = logger ?? Logger.Instance;
            _viewModel = viewModel;
            _dialogs = dialogService ?? AppDialogServiceProvider.Current;
            InitializeComponent();
            DialogWindowHelper.Initialize(this, viewModel);
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
            if (_hasQueuedPackagedAppsPreload ||
                !ShouldPreloadPackagedApps(_viewModel.IsApplicationTriggerSelected))
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
            if (forceRefresh)
            {
                AudioDeviceHelper.ClearPackagedAppInventoryCache();
            }

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
                _packagedAppsTask = Task.Run(static () => AudioDeviceHelper.GetInstalledPackagedApps());
                _packagedAppsTaskStartedAt = now;
            }

            return _packagedAppsTask!;
        }

        private async Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> LoadPackagedAppsAsync(bool forceRefresh)
        {
            Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> loadTask = GetPackagedAppsTask(forceRefresh);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("RoutineEditorWindow", () => $"routine-packaged-apps-load-start | forceRefresh={forceRefresh}", nameof(LoadPackagedAppsAsync));
            }

            try
            {
                IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> loadedApps = await loadTask;
                if (!ShouldApplyPackagedAppsResult(_isClosed, ReferenceEquals(_packagedAppsTask, loadTask)))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("RoutineEditorWindow", () => $"routine-packaged-apps-load-abort | forceRefresh={forceRefresh} reason=stale-or-closed", nameof(LoadPackagedAppsAsync));
                    }
                    return _packagedApps;
                }

                _packagedApps = loadedApps;
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
            catch (Exception ex)
            {
                if (ReferenceEquals(_packagedAppsTask, loadTask))
                {
                    _packagedAppsTask = null;
                }

                if (_isClosed)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("RoutineEditorWindow", () => $"routine-packaged-apps-load-abort | forceRefresh={forceRefresh} reason=window-closed", nameof(LoadPackagedAppsAsync));
                    }
                    return _packagedApps;
                }

                _packagedApps = [];
                ApplyResolvedTriggerAppTargetDisplayName();
                _logger.Warning(
                    "RoutineEditorWindow",
                    () => $"routine-packaged-apps-load-failed | result=failure forceRefresh={forceRefresh}",
                    nameof(LoadPackagedAppsAsync),
                    ex);
                throw;
            }

            ApplyResolvedTriggerAppTargetDisplayName();
            return _packagedApps;
        }

        internal static bool ShouldApplyPackagedAppsResult(bool isClosed, bool isCurrentTask)
        {
            return !isClosed && isCurrentTask;
        }

        internal static bool ShouldPreloadPackagedApps(bool isAppStartTriggerSelected)
        {
            return isAppStartTriggerSelected;
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
            if (_isClosed)
            {
                return;
            }

            if (e.PropertyName == nameof(RoutineEditorViewModel.TriggerAppPath) ||
                e.PropertyName == nameof(RoutineEditorViewModel.SelectedTriggerMode))
            {
                QueuePackagedAppsPreloadIfNeeded();
                ApplyResolvedTriggerAppTargetDisplayName();
            }
        }

        private void ApplyResolvedTriggerAppTargetDisplayName()
        {
            if (!RoutineTriggerPathHelper.LooksLikePackagedAppId(_viewModel.TriggerAppPath))
            {
                _viewModel.SetResolvedPackagedAppDisplayName(string.Empty);
                return;
            }

            string resolvedDisplayName = _packagedApps
                .FirstOrDefault(app => string.Equals(app.AppUserModelId, _viewModel.TriggerAppPath, StringComparison.OrdinalIgnoreCase))
                .DisplayName;

            _viewModel.SetResolvedPackagedAppDisplayName(resolvedDisplayName);
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

            if (TryGetExecutableDialogSeed(viewModel.TriggerAppPath, out string initialDirectory, out string fileName))
            {
                dialog.FileName = fileName;
                if (!string.IsNullOrWhiteSpace(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }
            }

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            viewModel.ApplyExecutablePath(dialog.FileName);
        }

        private async void PickPackagedApp_Click(object sender, RoutedEventArgs e)
        {
            if (!DialogWindowHelper.TryGetViewModel(this, out RoutineEditorViewModel? viewModel))
            {
                return;
            }

            IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> apps;
            try
            {
                apps = await LoadPackagedAppsAsync(forceRefresh: false);
            }
            catch (Exception ex)
            {
                if (_isClosed)
                {
                    return;
                }

                _logger.Warning(
                    "RoutineEditorWindow",
                    "routine-packaged-app-picker-open-failed | result=failure forceRefresh=false",
                    nameof(PickPackagedApp_Click),
                    ex);
                await _dialogs.ShowInformationAsync("The packaged app list could not be loaded right now.", DialogText.Captions.InvalidSettings, this);
                return;
            }

            if (_isClosed)
            {
                return;
            }

            if (apps.Count == 0)
            {
                await _dialogs.ShowInformationAsync("No packaged apps were found for the current user.", DialogText.Captions.InvalidSettings, this);
                return;
            }

            var packagedAppPickerViewModel = new PackagedAppPickerViewModel(apps);
            packagedAppPickerViewModel.TrySelectAppUserModelId(viewModel.TriggerAppPath);

            var picker = new PackagedAppPickerWindow(packagedAppPickerViewModel, RefreshPackagedAppsForPickerAsync, _logger, _dialogs);

            if (DialogWindowHelper.ShowOwnedDialog(picker, this) != true || string.IsNullOrWhiteSpace(picker.SelectedAppUserModelId))
            {
                return;
            }

            viewModel.ApplyPackagedAppId(picker.SelectedAppUserModelId);
            viewModel.SetResolvedPackagedAppDisplayName(picker.SelectedAppDisplayName);
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

        private void ClearEditableText_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Parent: ContextMenu { PlacementTarget: TextBox textBox } })
            {
                return;
            }

            textBox.Clear();
            textBox.SelectionLength = 0;
            textBox.SelectionStart = 0;
        }

        private void Expander_Expanded(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, e.OriginalSource) ||
                sender is not Expander expander ||
                RoutineEditorScrollViewer?.Content is not Visual scrollContent)
            {
                return;
            }

            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
            {
                return;
            }

            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (RoutineEditorScrollViewer?.Content is not Visual currentScrollContent)
                    {
                        return;
                    }

                    RoutineEditorScrollViewer.UpdateLayout();
                    GeneralTransform transform = expander.TransformToAncestor(currentScrollContent);
                    Point position = transform.Transform(new Point(0, 0));
                    double targetOffset = Math.Max(0, Math.Min(position.Y, RoutineEditorScrollViewer.ScrollableHeight));
                    RoutineEditorScrollViewer.ScrollToVerticalOffset(targetOffset);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
            {
            }
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

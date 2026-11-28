using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot
{
    internal sealed record MainWindowDependencies(
        AppViewModel AppViewModel,
        MainWindowVisibilityCoordinator VisibilityCoordinator,
        Func<Task> RequestShutdown,
        TimeProvider? MixerRestoreTimeProvider = null);

    public partial class MainWindow : Window
    {
        private static readonly TimeSpan MixerRestoreCorrectionTimeout = TimeSpan.FromSeconds(1.5);
        private readonly AppViewModel _appVm;
        private readonly MainWindowVisibilityCoordinator _visibilityCoordinator;
        private readonly Func<Task> _requestShutdown;
        private readonly TimeProvider _mixerRestoreTimeProvider;
        private readonly Logger _logger;
        private DialogWorkAreaLifetime? _workAreaLifetime;
        private readonly MainWindowScrollPosition[] _tabScrollPositions = new MainWindowScrollPosition[4];
        private int _scrollTabIndex = -1;
        private DispatcherOperation? _tabScrollRestoreWork;
        private CancellationTokenSource? _restoreMixerScrollCts;
        private bool _restoreMixerScrollRequiresAutoScroll = true;
        private bool _allowCloseForRuntimeShutdown;
        private int _closeRequestStarted;

        internal AppViewModel AppViewModel => _appVm;

        internal MainWindow(MainWindowDependencies dependencies)
        {
            ArgumentNullException.ThrowIfNull(dependencies);
            long started = Stopwatch.GetTimestamp();

            _appVm = dependencies.AppViewModel;
            _visibilityCoordinator = dependencies.VisibilityCoordinator;
            _requestShutdown = dependencies.RequestShutdown;
            _mixerRestoreTimeProvider = dependencies.MixerRestoreTimeProvider ?? TimeProvider.System;
            _logger = Logger.Instance;

            string fontFamily = FontFamily.Source;
            _ = Task.Run(() => PrewarmFont(fontFamily));
            WindowFirstPresentationHelper.Prepare(this);
            InitializeComponent();
            _scrollTabIndex = DeviceTabControl.SelectedIndex;
            double xamlMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            DataContext = _appVm;
            UpdateLocalizedTaskbarChrome();
            double bindingMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - xamlMs;
            WindowThemeResolver.ApplyWindowTheme(this, _appVm.Theme);
            IsVisibleChanged += MainWindow_IsVisibleChanged;
            PreviewMouseWheel += CancelRestoreMixerScrollOnInput;
            PreviewKeyDown += CancelRestoreMixerScrollOnInput;
            _appVm.PropertyChanged += MainWindowViewModel_PropertyChanged;
            _appVm.Localization.PropertyChanged += MainWindowLocalization_PropertyChanged;

            _logger.Info(
                "MainWindow",
                () => $"main-window-view-constructed | xamlMs={xamlMs:F1} bindingMs={bindingMs:F1} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }

        /// <summary>
        /// Warms process-wide WPF font discovery using a worker-local typeface while XAML loads.
        /// Failures leave ordinary text rendering to perform the initialization.
        /// </summary>
        private static void PrewarmFont(string fontFamily)
        {
            try
            {
                _ = new Typeface(fontFamily).TryGetGlyphTypeface(out _);
            }
            catch (Exception ex)
            {
                Logger.Instance.Debug("MainWindow", () => $"font-prewarm-skipped | error={ex.GetType().Name}");
            }
        }

        internal void NavigateTo(MainWindowOpenTarget target)
        {
            int? tabIndex = target switch
            {
                MainWindowOpenTarget.Output => 0,
                MainWindowOpenTarget.Input => 1,
                MainWindowOpenTarget.Routines => 2,
                MainWindowOpenTarget.Settings => 3,
                MainWindowOpenTarget.VolumeMixer => 0,
                _ => null,
            };

            if (tabIndex.HasValue)
            {
                _appVm.SelectedSettingsTabIndex = tabIndex.Value;
                if (target == MainWindowOpenTarget.Settings)
                {
                    ResetMainContentScrollToTop();
                }
                if (target == MainWindowOpenTarget.VolumeMixer)
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (_appVm.SelectedSettingsTabIndex == 0) ScheduleScrollToVolumeMixerSection(requireAutoScroll: false);
                    }, DispatcherPriority.Loaded);
            }
        }

        internal void AllowCloseForRuntimeShutdown()
        {
            _allowCloseForRuntimeShutdown = true;
        }

        internal void MarkPendingAutoScrollOnNextShow() => _visibilityCoordinator.MarkPendingAutoScrollOnNextShow();

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            AppShellIntegration.ApplyWindowIdentity(this);
            WindowThemeResolver.ApplyWindowTheme(this, _appVm.Theme);
            _workAreaLifetime = new DialogWorkAreaLifetime(this, margin: 0);
        }

        internal static bool TryGetRoutineListItemFromEventSource(object? source, out ListBoxItem? item)
        {
            item = source as ListBoxItem;
            if (item != null)
            {
                return item.DataContext != null;
            }

            if (source is not DependencyObject dependencyObject)
            {
                return false;
            }

            item = FindVisualAncestor<ListBoxItem>(dependencyObject);
            return item?.DataContext != null;
        }

        internal static bool SelectRoutineListItem(ListBoxItem? item)
        {
            if (item?.DataContext == null)
            {
                return false;
            }

            if (ItemsControl.ItemsControlFromItemContainer(item) is ListBox listBox && !item.IsSelected)
            {
                listBox.UnselectAll();
            }

            item.IsSelected = true;
            item.Focus();
            return true;
        }

        private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        internal void HandleProjectLinkRequestNavigate(object sender, RequestNavigateEventArgs e) =>
            ProjectLink_RequestNavigate(sender, e);

        internal static void HandleProjectLinkPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Space or Key.Enter))
            {
                return;
            }

            if (e.IsRepeat)
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter || Keyboard.Modifiers != ModifierKeys.None) { return; }

            if (sender is System.Windows.Documents.Hyperlink link)
            {
                link.DoClick();
            }
            e.Handled = true;
        }

        private void ProjectLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Warning("MainWindow", () => $"project-link-open-failed | error={ex.GetType().Name}", nameof(ProjectLink_RequestNavigate), ex);
            }

            e.Handled = true;
        }

        private void ProjectLink_PreviewKeyDown(object sender, KeyEventArgs e) =>
            HandleProjectLinkPreviewKeyDown(sender, e);

        private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            DependencyObject? target = Keyboard.FocusedElement as DependencyObject;
            if (_appVm.IsSettingsTabActive && SettingsSearchBox.IsKeyboardFocusWithin) target = _settingsFind?.CurrentMatch ?? target;
            AppHelpTopic topic = target == null ? AppHelpTopic.None : AppHelp.GetTopic(target);
            if (topic == AppHelpTopic.None) topic = _appVm.SelectedSettingsTabIndex switch
            {
                2 => AppHelpTopic.Routines,
                3 => AppHelpTopic.Settings,
                _ => VolumeMixer.IsKeyboardFocusWithin ? AppHelpTopic.Mixer : AppHelpTopic.AudioDevices,
            };
            _ = AppHelp.TryOpen(_logger, "MainWindow", topic);
            e.Handled = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_allowCloseForRuntimeShutdown || App.IsSessionEnding)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            if (_appVm.CurrentSettings?.App.CloseButtonBehavior == Models.CloseButtonBehavior.HideToTray
                && !_appVm.IsRuntimeShutdownRequested)
            {
                _appVm.MinimizeWindow(honorShowCooldown: false);
                return;
            }

            if (Interlocked.Exchange(ref _closeRequestStarted, 1) == 0)
            {
                _ = ObserveShutdownRequestAsync();
            }
        }

        private async Task ObserveShutdownRequestAsync()
        {
            try
            {
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
                await _requestShutdown();
            }
            catch (Exception ex)
            {
                _logger.Error("MainWindow", "window-close-shutdown-request-failed", nameof(ObserveShutdownRequestAsync), ex);
                Interlocked.Exchange(ref _closeRequestStarted, 0);
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            _appVm.HandleWindowVisibilityChanged(IsVisible && WindowState != WindowState.Minimized);
            HandleMinimizeStateChanged(WindowState, Visibility == Visibility.Visible);
        }

        internal void HandleMinimizeStateChanged(WindowState state, bool isWindowVisible)
        {
            if (_appVm.CurrentSettings?.App.MinimizeButtonBehavior != Models.MinimizeButtonBehavior.MinimizeToTaskbar)
            {
                _visibilityCoordinator.HandleWindowStateChanged(state, isWindowVisible, () => _appVm.MinimizeWindow(honorShowCooldown: false));
            }
        }

        private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            bool isVisible = e.NewValue is true;
            if (isVisible)
            {
                RestoreVisibleDeferredTabs();
                ScheduleDeferredSettingsPrewarm();
            }

            _appVm.HandleWindowVisibilityChanged(isVisible);
            if (!isVisible)
            {
                CancelPendingRestoreMixerScroll();
                TrimHiddenSettingsTab();
            }

            _visibilityCoordinator.HandleVisibleChanged(
                isVisible,
                () => _appVm.IsEditorTabsActive,
                () => _appVm.Settings?.App.AutoScrollToMixerOnRestore ?? true,
                () => ScheduleScrollToVolumeMixerSection());
        }

        private void MainWindowViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppViewModel.SettingsLanguageDraft)) RefreshLocalizedSettingsPresentation();
            if (e.PropertyName == nameof(AppViewModel.SelectedSettingsTabIndex)
                || (e.PropertyName == nameof(AppViewModel.SettingsAutoScrollToMixerOnRestoreDraft) && _restoreMixerScrollRequiresAutoScroll))
                CancelPendingRestoreMixerScroll();
        }

        private void ScheduleScrollToVolumeMixerSection(bool requireAutoScroll = true)
        {
            CancelPendingTabScrollRestore();
            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
            {
                return;
            }

            CancellationTokenSource nextScrollCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _restoreMixerScrollCts);
            _restoreMixerScrollRequiresAutoScroll = requireAutoScroll;
            int expectedTab = _appVm.SelectedSettingsTabIndex;
            _ = AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                nextScrollCts,
                owned => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _restoreMixerScrollCts, owned),
                token => AppDispatcherHelper.InvokeAsync(
                    Dispatcher,
                    _logger,
                    () => ExecuteRestoreMixerScrollAsync(expectedTab, requireAutoScroll, token),
                    "Failed to schedule delayed mixer scroll",
                    nameof(MainWindow_IsVisibleChanged)),
                CancellationToken.None);
        }

        private bool CanContinueRestoreMixerScroll(int expectedTab, bool requireAutoScroll) =>
            IsVisible && WindowState != WindowState.Minimized && !_allowCloseForRuntimeShutdown
            && _appVm.IsEditorTabsActive && _appVm.SelectedSettingsTabIndex == expectedTab
            && (!requireAutoScroll || (_appVm.Settings?.App.AutoScrollToMixerOnRestore ?? true));

        private async Task ExecuteRestoreMixerScrollAsync(int expectedTab, bool requireAutoScroll, CancellationToken cancellationToken)
        {
            try
            {
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanContinueRestoreMixerScroll(expectedTab, requireAutoScroll) || !TryScrollToVolumeMixerSection()) return;

                using var correctionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task refresh = _appVm.WaitForActiveMixerRestoreRefreshAsync(correctionLifetime.Token);
                if (refresh.IsCompletedSuccessfully) return;

                try { await refresh.WaitAsync(MixerRestoreCorrectionTimeout, _mixerRestoreTimeProvider, cancellationToken); }
                catch (TimeoutException)
                {
                    correctionLifetime.Cancel();
                    return;
                }
                await Dispatcher.InvokeAsync(() =>
                {
                    if (CanContinueRestoreMixerScroll(expectedTab, requireAutoScroll)) TryScrollToVolumeMixerSection();
                }, DispatcherPriority.Loaded, correctionLifetime.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void CancelPendingRestoreMixerScroll() =>
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _restoreMixerScrollCts);

        private void CancelRestoreMixerScrollOnInput(object sender, RoutedEventArgs e)
        {
            if (e is MouseWheelEventArgs { Delta: not 0 }
                or KeyEventArgs { Key: Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space })
            {
                CancelPendingRestoreMixerScroll();
            }
        }

        private void ResetMainContentScrollToTop()
        {
            CancelPendingTabScrollRestore();
            if (DeviceTabControl.SelectedIndex is >= 0 and < 4)
                _tabScrollPositions[DeviceTabControl.SelectedIndex] = default;
            if (MainContentScrollViewer == null || MainContentScrollViewer.VerticalOffset <= 0.5)
            {
                return;
            }

            MainContentScrollViewer.ScrollToVerticalOffset(0);
            MainContentScrollViewer.UpdateLayout();
        }

        private bool TryScrollToVolumeMixerSection()
        {
            if (MainContentScrollViewer?.Content is not Visual scrollContent || VolumeMixerHeader is not { Visibility: Visibility.Visible })
            {
                return false;
            }

            Point headerPosition = VolumeMixerHeader.TransformToAncestor(scrollContent).Transform(new Point(0, 0));
            double targetOffset = Math.Clamp(headerPosition.Y, 0, MainContentScrollViewer.ScrollableHeight);
            if (Math.Abs(MainContentScrollViewer.VerticalOffset - targetOffset) > 0.5)
                MainContentScrollViewer.ScrollToVerticalOffset(targetOffset);
            return true;
        }

        private void RootGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            CancelPendingRestoreMixerScroll();
            if (!MainWindowInteractionHelper.ShouldClearRootFocus(e.OriginalSource as DependencyObject, this))
            {
                ClearListSelections(e.OriginalSource as DependencyObject);
                return;
            }

            ResetInteractionState();
        }

        internal void ResetInteractionState()
        {
            MainWindowInteractionHelper.ClearWidgetFocus(this);
            ClearListSelections(null);
        }

        private void RootGrid_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            ClearListSelections(e.NewFocus as DependencyObject);

        private void ClearListSelections(DependencyObject? source)
        {
            if (!MainWindowInteractionHelper.ShouldPreserveListSelection(source, _appVm.SelectedOutputCycleDevices))
            {
                if (_appVm.SelectedOutputCycleDevices.Count > 0) _appVm.SelectedOutputCycleDevices.Clear();
                _appVm.SelectedOutputCycleIndex = -1;
            }
            if (!MainWindowInteractionHelper.ShouldPreserveListSelection(source, _appVm.SelectedInputCycleDevices))
            {
                if (_appVm.SelectedInputCycleDevices.Count > 0) _appVm.SelectedInputCycleDevices.Clear();
                _appVm.SelectedInputCycleIndex = -1;
            }
            if (!MainWindowInteractionHelper.ShouldPreserveListSelection(source, _appVm.SelectedRoutines))
            {
                if (_appVm.SelectedRoutines.Count > 0) _appVm.SelectedRoutines.Clear();
                _appVm.SelectedRoutineIndex = -1;
            }
        }

        private void VolumeMixer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox listBox)
            {
                MainWindowInteractionHelper.UnselectListBoxIfSelected(listBox);
            }
        }

        private void DeviceTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, sender)) return;
            if (MainContentScrollViewer != null && DeviceTabControl.SelectedIndex is >= 0 and < 4)
                RestoreTabScrollPosition(DeviceTabControl.SelectedIndex);
            ResetSettingsFindForTabChange();
            if (VolumeMixer == null || !VolumeMixer.IsKeyboardFocusWithin)
            {
                return;
            }

            Keyboard.ClearFocus();
            if (DeviceTabControl.SelectedItem is TabItem selectedTab)
            {
                _ = selectedTab.Focus();
            }
            else
            {
                _ = DeviceTabControl.Focus();
            }
        }

        private void RestoreTabScrollPosition(int tabIndex)
        {
            bool restorePending = _tabScrollRestoreWork is { Status: DispatcherOperationStatus.Pending };
            CancelPendingTabScrollRestore();
            if (_scrollTabIndex >= 0 && !restorePending)
                _tabScrollPositions[_scrollTabIndex] = ReadScrollPosition();
            _scrollTabIndex = tabIndex;
            MainWindowScrollPosition targetPosition = _tabScrollPositions[tabIndex];
            MainContentScrollViewer.ScrollToVerticalOffset(0);
            if (!targetPosition.AtBottom && targetPosition.Offset <= 0.5)
            {
                return;
            }

            _tabScrollRestoreWork = Dispatcher.BeginInvoke(() =>
            {
                _tabScrollRestoreWork = null;
                RestoreLayoutScrollPosition((tabIndex, _tabScrollPositions[tabIndex]));
            }, DispatcherPriority.Loaded);
        }

        private void CancelPendingTabScrollRestore()
        {
            _tabScrollRestoreWork?.Abort();
            _tabScrollRestoreWork = null;
        }

        private void VolumeMixer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MainContentScrollViewer == null || e.Delta == 0)
            {
                return;
            }

            double targetOffset = MainWindowInteractionHelper.ClampMouseWheelOffset(
                MainContentScrollViewer.VerticalOffset,
                MainContentScrollViewer.ScrollableHeight,
                e.Delta,
                SystemParameters.WheelScrollLines);
            MainContentScrollViewer.ScrollToVerticalOffset(targetOffset);
            e.Handled = true;
        }

        private void MainWindowLocalization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(Localization.LocalizationService.Revision) or nameof(Localization.LocalizationService.EffectiveLanguage) or ""))
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                UpdateLocalizedTaskbarChrome();
            }
            else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(UpdateLocalizedTaskbarChrome);
            }
        }

        private void UpdateLocalizedTaskbarChrome()
        {
            PreviousTrackThumbButton.Description = _appVm.Localization["Ui_PreviousTrack"].TrimEnd(':');
            PlayPauseThumbButton.Description = _appVm.Localization["Ui_PlayPause"].TrimEnd(':');
            NextTrackThumbButton.Description = _appVm.Localization["Ui_NextTrack"].TrimEnd(':');
        }

        protected override void OnClosed(EventArgs e)
        {
            DisposeSettingsFind();
            CancelPendingTabScrollRestore();
            _workAreaLifetime?.Dispose();
            _workAreaLifetime = null;
            TaskbarItemInfo = null;
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            PreviewMouseWheel -= CancelRestoreMixerScrollOnInput;
            PreviewKeyDown -= CancelRestoreMixerScrollOnInput;
            _appVm.PropertyChanged -= MainWindowViewModel_PropertyChanged;
            _appVm.Localization.PropertyChanged -= MainWindowLocalization_PropertyChanged;
            CancelPendingRestoreMixerScroll();
            base.OnClosed(e);
        }
    }
}

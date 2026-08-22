using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot
{
    internal sealed record MainWindowDependencies(
        AppViewModel AppViewModel,
        AppShellService Shell,
        MainWindowVisibilityCoordinator VisibilityCoordinator,
        Func<Task> RequestShutdown);

    internal readonly record struct RestoreMixerScrollState(
        double HeaderOffsetY,
        double TargetOffset,
        double ScrollableHeight);

    public partial class MainWindow : Window
    {
        private readonly AppViewModel _appVm;
        private readonly AppShellService _shell;
        private readonly MainWindowVisibilityCoordinator _visibilityCoordinator;
        private readonly Func<Task> _requestShutdown;
        private readonly Logger _logger;
        private DialogWorkAreaLifetime? _workAreaLifetime;
        private readonly double[] _tabScrollOffsets = new double[4];
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
            _shell = dependencies.Shell;
            _visibilityCoordinator = dependencies.VisibilityCoordinator;
            _requestShutdown = dependencies.RequestShutdown;
            _logger = Logger.Instance;

            string fontFamily = FontFamily.Source;
            _ = Task.Run(() => PrewarmFont(fontFamily));
            WindowFirstPresentationHelper.Prepare(this);
            InitializeComponent();
            _scrollTabIndex = DeviceTabControl.SelectedIndex;
            double xamlMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            DataContext = _appVm;
            double bindingMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - xamlMs;
            WindowThemeResolver.ApplyWindowTheme(this, _appVm.Theme);
            IsVisibleChanged += MainWindow_IsVisibleChanged;
            _appVm.PropertyChanged += MainWindowViewModel_PropertyChanged;

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

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            AppShellIntegration.ApplyWindowIdentity(this);
            WindowThemeResolver.ApplyWindowTheme(this, _appVm.Theme);
            _workAreaLifetime = new DialogWorkAreaLifetime(this);
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

        private static bool TryGetHotkeyTextBoxContext(object sender, out TextBox textBox, out IHotkeySink target)
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

        private void SavedRoutinesListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryGetRoutineListItemFromEventSource(e.OriginalSource, out ListBoxItem? item))
            {
                SelectRoutineListItem(item);
            }
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

        private void ProjectLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Warning("MainWindow", () => $"project-link-open-failed | error={ex.GetType().Name}", nameof(ProjectLink_RequestNavigate), ex);
            }

            e.Handled = true;
        }

        private void ProjectLink_PreviewKeyDown(object sender, KeyEventArgs e)
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

        private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            _ = AboutDocumentLauncher.TryOpen(_logger, "MainWindow");
            e.Handled = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_allowCloseForRuntimeShutdown)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
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
            _visibilityCoordinator.HandleWindowStateChanged(WindowState, Visibility == Visibility.Visible, _appVm.MinimizeWindow);
        }

        private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            bool isVisible = e.NewValue is true;
            _appVm.HandleWindowVisibilityChanged(isVisible);
            if (!isVisible)
            {
                CancelPendingRestoreMixerScroll();
            }

            _visibilityCoordinator.HandleVisibleChanged(
                isVisible,
                () => _appVm.IsEditorTabsActive,
                () => _appVm.Settings!.Miscellaneous.AutoScrollToMixerOnRestore,
                () => ScheduleScrollToVolumeMixerSection());
        }

        private void MainWindowViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
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
            _shell.IsWindowVisible && _appVm.IsEditorTabsActive && _appVm.SelectedSettingsTabIndex == expectedTab
            && (!requireAutoScroll || (_appVm.Settings?.Miscellaneous.AutoScrollToMixerOnRestore ?? true));

        private async Task ExecuteRestoreMixerScrollAsync(int expectedTab, bool requireAutoScroll, CancellationToken cancellationToken)
        {
            try
            {
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded, cancellationToken);
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (!CanContinueRestoreMixerScroll(expectedTab, requireAutoScroll)) return;
                if (TryScrollToVolumeMixerSection(out RestoreMixerScrollState initialState, forceLayout: true)
                    && IsRestoreMixerScrollComplete(initialState))
                {
                    return;
                }

                if (!_appVm.HasPendingMixerRestoreWork())
                {
                    return;
                }

                await _appVm.WaitForMixerRestoreReadinessAsync(cancellationToken);
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, cancellationToken);

                RestoreMixerScrollState? previousState = null;
                Stopwatch stopwatch = Stopwatch.StartNew();
                for (int pass = 0; pass < 12 && stopwatch.ElapsedMilliseconds < 1500 && CanContinueRestoreMixerScroll(expectedTab, requireAutoScroll); pass++)
                {
                    bool observedLayoutUpdate = await AwaitNextMixerLayoutOrIdleAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanContinueRestoreMixerScroll(expectedTab, requireAutoScroll)) return;
                    if (!TryScrollToVolumeMixerSection(out RestoreMixerScrollState currentState, !observedLayoutUpdate)) continue;

                    if (previousState.HasValue
                        && AreEquivalent(previousState.Value, currentState)
                        && IsRestoreMixerScrollComplete(currentState))
                    {
                        break;
                    }

                    previousState = currentState;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void CancelPendingRestoreMixerScroll() =>
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _restoreMixerScrollCts);

        private void ResetMainContentScrollToTop()
        {
            CancelPendingTabScrollRestore();
            if (DeviceTabControl.SelectedIndex is >= 0 and < 4)
                _tabScrollOffsets[DeviceTabControl.SelectedIndex] = 0;
            if (MainContentScrollViewer == null || MainContentScrollViewer.VerticalOffset <= 0.5)
            {
                return;
            }

            MainContentScrollViewer.ScrollToVerticalOffset(0);
            MainContentScrollViewer.UpdateLayout();
        }

        private async Task<bool> AwaitNextMixerLayoutOrIdleAsync(CancellationToken cancellationToken)
        {
            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher) || MainContentScrollViewer == null)
            {
                return false;
            }

            using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task layoutTask = WaitForNextLayoutUpdateAsync(waitCts.Token);
            Task idleTask = Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, cancellationToken).Task;
            Task completedTask = await Task.WhenAny(layoutTask, idleTask);
            waitCts.Cancel();
            try
            {
                await completedTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            return ReferenceEquals(completedTask, layoutTask);
        }

        private Task WaitForNextLayoutUpdateAsync(CancellationToken cancellationToken)
        {
            if (MainContentScrollViewer == null)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            CancellationTokenRegistration registration = default;
            void Detach()
            {
                MainContentScrollViewer.LayoutUpdated -= handler;
                registration.Dispose();
            }

            handler = (_, _) =>
            {
                Detach();
                completion.TrySetResult(null);
            };
            MainContentScrollViewer.LayoutUpdated += handler;
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(() =>
                {
                    if (Dispatcher.CheckAccess())
                    {
                        Detach();
                    }
                    else if (!AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
                    {
                        _ = Dispatcher.BeginInvoke(Detach);
                    }

                    completion.TrySetCanceled(cancellationToken);
                });
            }

            return completion.Task;
        }

        private bool TryScrollToVolumeMixerSection(out RestoreMixerScrollState state, bool forceLayout = false)
        {
            state = default;
            if (MainContentScrollViewer?.Content is not Visual scrollContent || VolumeMixerHeader is not { Visibility: Visibility.Visible })
            {
                return false;
            }

            if (forceLayout)
            {
                MainContentScrollViewer.UpdateLayout();
            }

            Point headerPosition = VolumeMixerHeader.TransformToAncestor(scrollContent).Transform(new Point(0, 0));
            double targetOffset = ClampScrollOffset(headerPosition.Y, MainContentScrollViewer.ScrollableHeight);
            MainContentScrollViewer.ScrollToVerticalOffset(targetOffset);
            state = new RestoreMixerScrollState(headerPosition.Y, targetOffset, MainContentScrollViewer.ScrollableHeight);
            return true;
        }

        private static bool AreEquivalent(RestoreMixerScrollState left, RestoreMixerScrollState right)
        {
            const double tolerance = 0.5;
            return Math.Abs(left.HeaderOffsetY - right.HeaderOffsetY) < tolerance
                && Math.Abs(left.TargetOffset - right.TargetOffset) < tolerance
                && Math.Abs(left.ScrollableHeight - right.ScrollableHeight) < tolerance;
        }

        private static bool IsRestoreMixerScrollComplete(RestoreMixerScrollState state) =>
            state.ScrollableHeight + 0.5 >= state.HeaderOffsetY;

        internal static double ClampScrollOffset(double headerOffsetY, double scrollableHeight) =>
            Math.Max(0, Math.Min(headerOffsetY, scrollableHeight));

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
            DeviceTabControl.Focus();
        }

        private void RestoreTabScrollPosition(int tabIndex)
        {
            bool restorePending = _tabScrollRestoreWork is { Status: DispatcherOperationStatus.Pending };
            CancelPendingTabScrollRestore();
            if (_scrollTabIndex >= 0 && !restorePending)
                _tabScrollOffsets[_scrollTabIndex] = MainContentScrollViewer.VerticalOffset;
            _scrollTabIndex = tabIndex;
            MainContentScrollViewer.ScrollToVerticalOffset(0);
            _tabScrollRestoreWork = Dispatcher.BeginInvoke(() =>
            {
                _tabScrollRestoreWork = null;
                if (_allowCloseForRuntimeShutdown || DeviceTabControl.SelectedIndex != tabIndex) return;
                MainContentScrollViewer.UpdateLayout();
                if (DeviceTabControl.SelectedIndex != tabIndex) return;
                MainContentScrollViewer.ScrollToVerticalOffset(_tabScrollOffsets[tabIndex]);
                MainContentScrollViewer.UpdateLayout();
            }, DispatcherPriority.Loaded);
        }

        private void CancelPendingTabScrollRestore()
        {
            _tabScrollRestoreWork?.Abort();
            _tabScrollRestoreWork = null;
        }

        private void VolumeMixer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            CancelPendingRestoreMixerScroll();
            if (MainContentScrollViewer == null || e.Delta == 0)
            {
                return;
            }

            double targetOffset = MainWindowInteractionHelper.ClampMouseWheelOffset(
                MainContentScrollViewer.VerticalOffset,
                MainContentScrollViewer.ScrollableHeight,
                e.Delta);
            MainContentScrollViewer.ScrollToVerticalOffset(targetOffset);
            e.Handled = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            DisposeSettingsFind();
            CancelPendingTabScrollRestore();
            _workAreaLifetime?.Dispose();
            _workAreaLifetime = null;
            TaskbarItemInfo = null;
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            _appVm.PropertyChanged -= MainWindowViewModel_PropertyChanged;
            CancelPendingRestoreMixerScroll();
            base.OnClosed(e);
        }
    }
}

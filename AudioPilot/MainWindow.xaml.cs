using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.ViewModels;
using AudioPilot.Views;
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
        private const int WM_DPICHANGED = 0x02E0;
        private readonly AppViewModel _appVm;
        private readonly AppShellService _shell;
        private readonly MainWindowVisibilityCoordinator _visibilityCoordinator;
        private readonly Func<Task> _requestShutdown;
        private readonly Logger _logger;
        private HwndSource? _windowSource;
        private ListBox? _savedRoutinesListBox;
        private InputDevicePanel? _inputDevicePanel;
        private CancellationTokenSource? _restoreMixerScrollCts;
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

            WindowFirstPresentationHelper.Prepare(this);
            InitializeComponent();
            DataContext = _appVm;
            WindowThemeResolver.ApplyWindowTheme(this, _appVm.Theme);
            IsVisibleChanged += MainWindow_IsVisibleChanged;

            _logger.Info(
                "MainWindow",
                () => $"main-window-view-constructed | elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }

        internal void NavigateTo(MainWindowOpenTarget target)
        {
            int? tabIndex = target switch
            {
                MainWindowOpenTarget.Output => 0,
                MainWindowOpenTarget.Input => 1,
                MainWindowOpenTarget.Routines => 2,
                MainWindowOpenTarget.Settings => 3,
                _ => null,
            };

            if (tabIndex.HasValue)
            {
                _appVm.SelectedSettingsTabIndex = tabIndex.Value;
                if (target == MainWindowOpenTarget.Settings)
                {
                    ResetMainContentScrollToTop();
                }
            }
        }

        internal void AllowCloseForRuntimeShutdown()
        {
            _allowCloseForRuntimeShutdown = true;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            WindowThemeResolver.ApplyWindowTheme(this, _appVm.Theme);
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WndProc);
            _shell.RefreshIconsForCurrentDpi();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_DPICHANGED)
            {
                return IntPtr.Zero;
            }

            try
            {
                _shell.RefreshIconsForCurrentDpi();
            }
            catch (Exception ex)
            {
                _logger.Warning("MainWindow", () => $"dpi-refresh-failed | error={ex.GetType().Name}", nameof(WndProc), ex);
            }

            return IntPtr.Zero;
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

        private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppConstants.Links.RepositoryUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Warning("MainWindow", () => $"repository-link-open-failed | error={ex.GetType().Name}", nameof(RepoLink_RequestNavigate), ex);
            }

            e.Handled = true;
        }

        private void RepoLinkBorder_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Space or Key.Enter))
            {
                return;
            }

            RepoLink_RequestNavigate(sender, new RequestNavigateEventArgs(new Uri(AppConstants.Links.RepositoryUrl), null));
            e.Handled = true;
        }

        private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            _ = AboutDocumentLauncher.TryOpen(_logger, "MainWindow");
            e.Handled = true;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
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
            _appVm.HandleWindowVisibilityChanged(_shell.IsWindowVisible);
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
                ScheduleScrollToVolumeMixerSection);
        }

        private void ScheduleScrollToVolumeMixerSection()
        {
            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
            {
                return;
            }

            CancellationTokenSource nextScrollCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(ref _restoreMixerScrollCts);
            _ = AppDispatcherHelper.InvokeAsync(
                Dispatcher,
                _logger,
                () => ExecuteRestoreMixerScrollAsync(nextScrollCts),
                "Failed to schedule delayed mixer scroll",
                nameof(MainWindow_IsVisibleChanged));
        }

        private async Task ExecuteRestoreMixerScrollAsync(CancellationTokenSource ownedScrollCts)
        {
            try
            {
                CancellationToken cancellationToken = ownedScrollCts.Token;
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded, cancellationToken);
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, cancellationToken);

                if (_shell.IsWindowVisible
                    && TryScrollToVolumeMixerSection(out RestoreMixerScrollState initialState, forceLayout: true)
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
                for (int pass = 0; pass < 12 && stopwatch.ElapsedMilliseconds < 1500 && _shell.IsWindowVisible; pass++)
                {
                    bool observedLayoutUpdate = await AwaitNextMixerLayoutOrIdleAsync(cancellationToken);
                    if (!_shell.IsWindowVisible
                        || !TryScrollToVolumeMixerSection(out RestoreMixerScrollState currentState, !observedLayoutUpdate))
                    {
                        continue;
                    }

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
            finally
            {
                AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _restoreMixerScrollCts, ownedScrollCts);
            }
        }

        private void CancelPendingRestoreMixerScroll() =>
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _restoreMixerScrollCts);

        private void ResetMainContentScrollToTop()
        {
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
            if (MainContentScrollViewer?.Content is not Visual scrollContent || VolumeMixerHeader is null)
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
            if (!MainWindowInteractionHelper.ShouldClearRootFocus(e.OriginalSource as DependencyObject, this))
            {
                return;
            }

            Keyboard.ClearFocus();
            Focus();
            OutputDevicePanel.UnselectAll();
            _inputDevicePanel?.UnselectAll();
            MainWindowInteractionHelper.UnselectListBoxIfSelected(_savedRoutinesListBox);
        }

        private void InputDevicePanel_Loaded(object sender, RoutedEventArgs e) =>
            _inputDevicePanel = sender as InputDevicePanel;

        private void SavedRoutinesListBox_Loaded(object sender, RoutedEventArgs e) =>
            _savedRoutinesListBox = sender as ListBox;

        private void VolumeMixer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox listBox)
            {
                MainWindowInteractionHelper.UnselectListBoxIfSelected(listBox);
            }
        }

        private void DeviceTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, sender) || VolumeMixer == null || !VolumeMixer.IsKeyboardFocusWithin)
            {
                return;
            }

            Keyboard.ClearFocus();
            DeviceTabControl.Focus();
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
                e.Delta);
            MainContentScrollViewer.ScrollToVerticalOffset(targetOffset);
            e.Handled = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            CancelPendingRestoreMixerScroll();
            _windowSource?.RemoveHook(WndProc);
            _windowSource = null;
            base.OnClosed(e);
        }
    }
}

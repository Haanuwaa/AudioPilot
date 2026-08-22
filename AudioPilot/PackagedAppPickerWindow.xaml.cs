using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot
{
    public partial class PackagedAppPickerWindow : Window
    {
        private readonly ILogger _logger;
        private readonly Func<Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>> _refreshAppsAsync;
        private readonly IAppDialogService _dialogs;
        private bool _isRefreshing;
        private bool _isClosed;

        internal PackagedAppPickerWindow(
            PackagedAppPickerViewModel viewModel,
            Func<Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>> refreshAppsAsync,
            ILogger? logger = null,
            IAppDialogService? dialogService = null)
        {
            ArgumentNullException.ThrowIfNull(refreshAppsAsync);

            _logger = logger ?? Logger.Instance;
            _refreshAppsAsync = refreshAppsAsync;
            _dialogs = dialogService ?? AppDialogServiceProvider.Current;
            InitializeComponent();
            DialogWindowHelper.Initialize(this, viewModel);
        }

        internal string SelectedAppUserModelId =>
            DialogWindowHelper.TryGetViewModel(this, out PackagedAppPickerViewModel? viewModel)
                ? viewModel.ConfirmedAppUserModelId
                : string.Empty;

        internal string SelectedAppDisplayName =>
            DialogWindowHelper.TryGetViewModel(this, out PackagedAppPickerViewModel? viewModel)
                ? viewModel.ConfirmedDisplayName
                : string.Empty;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            if (DialogWindowHelper.TryGetViewModel(this, out PackagedAppPickerViewModel? viewModel))
            {
                viewModel.ReplaceApps([]);
            }
            base.OnClosed(e);
        }

        private void Select_Click(object sender, RoutedEventArgs e)
        {
            TryConfirmSelection();
        }

        private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left &&
                e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(AppsList, source) is ListBoxItem { IsSelected: true })
            {
                TryConfirmSelection();
                e.Handled = true;
            }
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Down || Keyboard.Modifiers != ModifierKeys.None || _isRefreshing || AppsList.Items.Count == 0)
            {
                return;
            }

            if (AppsList.SelectedIndex < 0)
            {
                AppsList.SelectedIndex = 0;
            }
            AppsList.ScrollIntoView(AppsList.SelectedItem);
            AppsList.UpdateLayout();
            if (AppsList.ItemContainerGenerator.ContainerFromIndex(AppsList.SelectedIndex) is ListBoxItem item)
            {
                item.Focus();
            }
            e.Handled = true;
        }

        private void FocusSearch_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!SearchTextBox.IsEnabled)
            {
                return;
            }

            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
        }

        private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            _ = AboutDocumentLauncher.TryOpen(_logger, "PackagedAppPickerWindow");
            e.Handled = true;
        }

        private void TryConfirmSelection()
        {
            if (_isClosed || _isRefreshing ||
                !DialogWindowHelper.TryGetViewModel(this, out PackagedAppPickerViewModel? viewModel) ||
                !viewModel.ConfirmSelection())
            {
                return;
            }

            DialogResult = true;
        }

        private async void Refresh_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_isClosed)
            {
                return;
            }
            if (_isRefreshing)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("PackagedAppPickerWindow", "packaged-app-picker-refresh-skip | reason=already-refreshing", nameof(Refresh_Executed));
                }
                return;
            }

            if (!DialogWindowHelper.TryGetViewModel(this, out PackagedAppPickerViewModel? viewModel))
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("PackagedAppPickerWindow", "packaged-app-picker-refresh-skip | reason=viewmodel-unavailable", nameof(Refresh_Executed));
                }
                return;
            }

            _isRefreshing = true;
            bool restoreSearchFocus = SearchTextBox.IsKeyboardFocusWithin;
            int searchSelectionStart = SearchTextBox.SelectionStart;
            int searchSelectionLength = SearchTextBox.SelectionLength;
            SetRefreshingState(true);
            double? preservedScrollOffset = TryGetAppsListScrollOffset();
            _logger.Debug(
                "PackagedAppPickerWindow",
                () => $"packaged-app-picker-refresh-start | restoreSearchFocus={restoreSearchFocus} preserveScroll={preservedScrollOffset.HasValue}",
                nameof(Refresh_Executed));

            try
            {
                IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> apps = await _refreshAppsAsync();
                if (_isClosed)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("PackagedAppPickerWindow", "packaged-app-picker-refresh-abort | reason=window-closed", nameof(Refresh_Executed));
                    }
                    return;
                }

                viewModel.ReplaceApps(apps);

                if (preservedScrollOffset.HasValue)
                {
                    RestoreAppsListScrollOffset(preservedScrollOffset.Value);
                }
                else if (viewModel.SelectedApp.HasValue)
                {
                    AppsList.ScrollIntoView(viewModel.SelectedApp.Value);
                }
                else
                {
                    AppsList.Focus();
                }

                _logger.Info(
                    "PackagedAppPickerWindow",
                    () => $"packaged-app-picker-refresh-complete | result=success appCount={apps.Count} restoreSearchFocus={restoreSearchFocus} restoreScroll={preservedScrollOffset.HasValue}",
                    nameof(Refresh_Executed));
            }
            catch (Exception ex)
            {
                if (_isClosed)
                {
                    return;
                }

                _logger.Warning(
                    "PackagedAppPickerWindow",
                    "packaged-app-picker-refresh-failed | result=failure",
                    nameof(Refresh_Executed),
                    ex);
                await _dialogs.ShowInformationAsync("The packaged app list could not be refreshed right now.", DialogText.Captions.InvalidSettings, this);
            }
            finally
            {
                _isRefreshing = false;
                if (!_isClosed)
                {
                    SetRefreshingState(false);
                    if (restoreSearchFocus)
                    {
                        RestoreSearchFocus(searchSelectionStart, searchSelectionLength);
                    }
                }
            }
        }

        [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Accesses XAML-generated instance controls.")]
        private void SetRefreshingState(bool isRefreshing)
        {
            RefreshButton.IsEnabled = !isRefreshing;
            RefreshButton.Content = isRefreshing ? "Refreshing..." : "Refresh";
            SearchTextBox.IsEnabled = !isRefreshing;
            AppsList.IsEnabled = !isRefreshing;
            if (isRefreshing)
            {
                SelectButton.SetCurrentValue(IsEnabledProperty, false);
            }
            else
            {
                SelectButton.GetBindingExpression(IsEnabledProperty)?.UpdateTarget();
            }
        }

        [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Accesses XAML-generated instance controls.")]
        private double? TryGetAppsListScrollOffset()
        {
            return FindDescendant<ScrollViewer>(AppsList)?.VerticalOffset;
        }

        private void RestoreAppsListScrollOffset(double offset)
        {
            _ = Dispatcher.BeginInvoke(
                () =>
                {
                    if (_isClosed)
                    {
                        return;
                    }

                    ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(AppsList);
                    if (scrollViewer == null)
                    {
                        return;
                    }

                    AppsList.UpdateLayout();
                    double targetOffset = Math.Max(0, Math.Min(offset, scrollViewer.ScrollableHeight));
                    scrollViewer.ScrollToVerticalOffset(targetOffset);
                },
                DispatcherPriority.Background);
        }

        private void RestoreSearchFocus(int selectionStart, int selectionLength)
        {
            _ = Dispatcher.BeginInvoke(
                () =>
                {
                    if (_isClosed)
                    {
                        return;
                    }

                    SearchTextBox.Focus();
                    int textLength = SearchTextBox.Text?.Length ?? 0;
                    int boundedSelectionStart = Math.Max(0, Math.Min(selectionStart, textLength));
                    int boundedSelectionLength = Math.Max(0, Math.Min(selectionLength, textLength - boundedSelectionStart));
                    SearchTextBox.Select(boundedSelectionStart, boundedSelectionLength);
                },
                DispatcherPriority.Background);
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && !ReferenceEquals(source, AppsFrame))
            {
                if (source is ListBoxItem or ScrollBar)
                {
                    return;
                }

                source = source switch
                {
                    Visual visual => VisualTreeHelper.GetParent(visual),
                    System.Windows.Media.Media3D.Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
                    _ => LogicalTreeHelper.GetParent(source)
                };
            }

            if (ReferenceEquals(source, AppsFrame))
            {
                AppsList.Focus();
                AppsList.UnselectAll();
            }
        }

        private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is T match)
                {
                    return match;
                }

                T? descendant = FindDescendant<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }
    }
}

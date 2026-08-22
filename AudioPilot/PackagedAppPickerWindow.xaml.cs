using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
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
                ? GetConfirmedOrSelectedAppUserModelId(viewModel)
                : string.Empty;

        internal string SelectedAppDisplayName =>
            DialogWindowHelper.TryGetViewModel(this, out PackagedAppPickerViewModel? viewModel)
                ? GetConfirmedOrSelectedDisplayName(viewModel)
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
                ResetAppsForClose(viewModel);
            }
            base.OnClosed(e);
        }

        internal static void ResetAppsForClose(PackagedAppPickerViewModel? viewModel)
        {
            viewModel?.ReplaceApps([]);
        }

        private void Select_Click(object sender, RoutedEventArgs e)
        {
            TryConfirmSelection(setDialogResultOnFailure: true);
        }

        private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            TryConfirmSelection(setDialogResultOnFailure: false);
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

        private bool TryConfirmSelection(bool setDialogResultOnFailure)
        {
            if (!DialogWindowHelper.TryGetViewModel(this, out PackagedAppPickerViewModel? viewModel))
            {
                if (setDialogResultOnFailure)
                {
                    DialogResult = false;
                }

                return false;
            }

            if (!viewModel.ConfirmSelection())
            {
                if (setDialogResultOnFailure)
                {
                    DialogResult = false;
                }

                return false;
            }

            DialogResult = true;
            return true;
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_isRefreshing)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("PackagedAppPickerWindow", "packaged-app-picker-refresh-skip | reason=already-refreshing", nameof(Refresh_Click));
                }
                return;
            }

            if (!DialogWindowHelper.TryGetViewModel(this, out PackagedAppPickerViewModel? viewModel))
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("PackagedAppPickerWindow", "packaged-app-picker-refresh-skip | reason=viewmodel-unavailable", nameof(Refresh_Click));
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
                nameof(Refresh_Click));

            try
            {
                IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> apps = await _refreshAppsAsync();
                if (_isClosed)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("PackagedAppPickerWindow", "packaged-app-picker-refresh-abort | reason=window-closed", nameof(Refresh_Click));
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
                    Keyboard.Focus(AppsFrame);
                }

                _logger.Info(
                    "PackagedAppPickerWindow",
                    () => $"packaged-app-picker-refresh-complete | result=success appCount={apps.Count} restoreSearchFocus={restoreSearchFocus} restoreScroll={preservedScrollOffset.HasValue}",
                    nameof(Refresh_Click));
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "PackagedAppPickerWindow",
                    "packaged-app-picker-refresh-failed | result=failure",
                    nameof(Refresh_Click),
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
            if (!ShouldClearSelectionForPreviewMouseDown(
                    source,
                    AppsList,
                    AppsFrame))
            {
                return;
            }

            Keyboard.Focus(AppsFrame);
            AppsList.UnselectAll();
        }

        private static bool ShouldClearSelectionForPreviewMouseDown(
            DependencyObject? source,
            DependencyObject appsList,
            DependencyObject appsFrame)
        {
            return ShouldClearSelectionForPreviewMouseDown(
                IsDescendantOf(source, appsList),
                IsDescendantOf(source, appsFrame));
        }

        internal static bool ShouldClearSelectionForPreviewMouseDownForTests(
            bool sourceIsInAppsList,
            bool sourceIsInAppsFrame)
        {
            return ShouldClearSelectionForPreviewMouseDown(sourceIsInAppsList, sourceIsInAppsFrame);
        }

        private static bool ShouldClearSelectionForPreviewMouseDown(
            bool sourceIsInAppsList,
            bool sourceIsInAppsFrame)
        {
            return !sourceIsInAppsList && sourceIsInAppsFrame;
        }

        private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, ancestor))
                {
                    return true;
                }

                source = source switch
                {
                    Visual visual => VisualTreeHelper.GetParent(visual),
                    System.Windows.Media.Media3D.Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
                    _ => LogicalTreeHelper.GetParent(source)
                };
            }

            return false;
        }

        private static string GetConfirmedOrSelectedAppUserModelId(PackagedAppPickerViewModel viewModel)
        {
            return !string.IsNullOrWhiteSpace(viewModel.ConfirmedAppUserModelId)
                ? viewModel.ConfirmedAppUserModelId
                : viewModel.SelectedAppUserModelId;
        }

        private static string GetConfirmedOrSelectedDisplayName(PackagedAppPickerViewModel viewModel)
        {
            return !string.IsNullOrWhiteSpace(viewModel.ConfirmedDisplayName)
                ? viewModel.ConfirmedDisplayName
                : viewModel.SelectedApp?.DisplayName ?? string.Empty;
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

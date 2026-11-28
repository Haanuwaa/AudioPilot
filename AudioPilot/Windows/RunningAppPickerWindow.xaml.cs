using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot;

public partial class RunningAppPickerWindow : Window
{
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, Task<IReadOnlyList<RunningAppIdentity>>> _loadAppsAsync;
    private readonly IAppDialogService _dialogs;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly MixerAppIconProvider _iconProvider = new();
    private readonly Dictionary<Image, long> _iconRequestVersions = [];
    private long _nextIconVersion;
    private bool _isRefreshing;
    private bool _isClosed;

    internal RunningAppPickerWindow(
        RunningAppPickerViewModel viewModel,
        Func<CancellationToken, Task<IReadOnlyList<RunningAppIdentity>>> loadAppsAsync,
        ILogger? logger = null,
        IAppDialogService? dialogService = null,
        bool loadOnOpen = true,
        string? preferredTargetPath = null)
    {
        ArgumentNullException.ThrowIfNull(loadAppsAsync);

        _logger = logger ?? Logger.Instance;
        _loadAppsAsync = loadAppsAsync;
        _dialogs = dialogService ?? AppDialogServiceProvider.Current;
        InitializeComponent();
        DialogWindowHelper.Initialize(this, viewModel);

        if (loadOnOpen)
        {
            InitialLoadingPanel.Visibility = Visibility.Visible;
            RefreshButton.IsEnabled = false;
            Loaded += LoadInitialApps;
        }

        async void LoadInitialApps(object sender, RoutedEventArgs e)
        {
            Loaded -= LoadInitialApps;
            await LoadAppsAsync(isInitialLoad: true, preferredTargetPath);
        }
    }

    internal string SelectedTargetPath =>
        DialogWindowHelper.TryGetViewModel(this, out RunningAppPickerViewModel? viewModel)
            ? viewModel.ConfirmedTargetPath
            : string.Empty;

    internal string SelectedDisplayName =>
        DialogWindowHelper.TryGetViewModel(this, out RunningAppPickerViewModel? viewModel)
            ? viewModel.ConfirmedDisplayName
            : string.Empty;

    internal bool SelectedIsPackagedApp =>
        DialogWindowHelper.TryGetViewModel(this, out RunningAppPickerViewModel? viewModel)
        && viewModel.ConfirmedIsPackagedApp;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _iconRequestVersions.Clear();
        _iconProvider.Dispose();
        if (DialogWindowHelper.TryGetViewModel(this, out RunningAppPickerViewModel? viewModel))
        {
            viewModel.ReplaceApps([]);
        }
        base.OnClosed(e);
    }

    private void Select_Click(object sender, RoutedEventArgs e) => TryConfirmSelection();

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
        if (!SearchTextBox.IsEnabled) return;
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
        e.Handled = true;
    }

    private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _ = AppHelp.TryOpen(_logger, "RunningAppPickerWindow", AppHelpTopic.RoutineTriggers);
        e.Handled = true;
    }

    private void TryConfirmSelection()
    {
        if (_isClosed || _isRefreshing ||
            !DialogWindowHelper.TryGetViewModel(this, out RunningAppPickerViewModel? viewModel) ||
            !viewModel.ConfirmSelection())
        {
            return;
        }

        DialogResult = true;
    }

    private async void Refresh_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        await LoadAppsAsync(isInitialLoad: false);
    }

    private async Task LoadAppsAsync(bool isInitialLoad, string? preferredTargetPath = null)
    {
        if (_isClosed || _isRefreshing || !DialogWindowHelper.TryGetViewModel(this, out RunningAppPickerViewModel? viewModel))
        {
            return;
        }

        _isRefreshing = true;
        CancellationToken token = _lifetime.Token;
        bool restoreSearchFocus = !isInitialLoad && SearchTextBox.IsKeyboardFocusWithin;
        int searchSelectionStart = SearchTextBox.SelectionStart;
        int searchSelectionLength = SearchTextBox.SelectionLength;
        SetRefreshingState(true, keepSearchEnabled: isInitialLoad);
        Task<IReadOnlyList<RunningAppIdentity>>? pending = null;

        bool focusAppsList = false;
        try
        {
            pending = _loadAppsAsync(token);
            IReadOnlyList<RunningAppIdentity> apps = await pending.WaitAsync(token);
            if (_isClosed) return;

            viewModel.ReplaceApps(apps);
            if (isInitialLoad) viewModel.TrySelectTargetPath(preferredTargetPath);

            if (viewModel.SelectedApp.HasValue)
            {
                AppsList.ScrollIntoView(viewModel.SelectedApp.Value);
            }
            else if (!isInitialLoad)
            {
                focusAppsList = true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending != null) _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            if (_isClosed) return;
            _logger.Warning("RunningAppPickerWindow", "running-app-picker-load-failed | result=failure", nameof(LoadAppsAsync), ex);
            try
            {
                await _dialogs.ShowInformationAsync("The running app list could not be loaded right now.", DialogText.Captions.InvalidSettings, this, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        finally
        {
            _isRefreshing = false;
            if (!_isClosed)
            {
                InitialLoadingPanel.Visibility = Visibility.Collapsed;
                SetRefreshingState(false);
                if (restoreSearchFocus)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_isClosed) return;
                        SearchTextBox.Focus();
                        int len = SearchTextBox.Text?.Length ?? 0;
                        int start = Math.Clamp(searchSelectionStart, 0, len);
                        SearchTextBox.Select(start, Math.Clamp(searchSelectionLength, 0, len - start));
                    }, DispatcherPriority.Background);
                }
                else if (focusAppsList)
                {
                    AppsList.Focus();
                }
            }
        }
    }

    private void SetRefreshingState(bool isRefreshing, bool keepSearchEnabled = false)
    {
        RefreshButton.IsEnabled = !isRefreshing;
        LocalizationContext.SetText(RefreshButton, ContentControl.ContentProperty, new(isRefreshing ? "PackagedApp_Refreshing" : "Ui_Refresh"));
        SearchTextBox.IsEnabled = !isRefreshing || keepSearchEnabled;
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

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null && !ReferenceEquals(source, AppsFrame))
        {
            if (source is ListBoxItem or ScrollBar) return;
            source = source switch
            {
                Visual visual => VisualTreeHelper.GetParent(visual),
                System.Windows.Media.Media3D.Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
                _ => LogicalTreeHelper.GetParent(source),
            };
        }

        if (ReferenceEquals(source, AppsFrame))
        {
            AppsList.Focus();
            AppsList.UnselectAll();
        }
    }

    private async void AppIcon_Loaded(object sender, RoutedEventArgs e) => await UpdateAppIconAsync((Image)sender);

    private async void AppIcon_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Image { IsLoaded: true } image) await UpdateAppIconAsync(image);
    }

    private void AppIcon_Unloaded(object sender, RoutedEventArgs e)
    {
        var image = (Image)sender;
        _iconRequestVersions.Remove(image);
        image.Source = null;
    }

    private async Task UpdateAppIconAsync(Image image)
    {
        long version = ++_nextIconVersion;
        _iconRequestVersions[image] = version;
        image.Source = null;
        if (_isClosed || image.DataContext is not RunningAppIdentity app || app.ProcessId <= 0) return;

        Task<ImageSource?>? request = _iconProvider.GetAsync((uint)app.ProcessId, string.Empty, app.StartedUtcTicks);
        if (request == null) return;

        try
        {
            ImageSource? icon = await request;
            if (!_isClosed && _iconRequestVersions.TryGetValue(image, out long current) && current == version)
            {
                image.Source = icon;
            }
        }
        catch
        {
        }
    }
}

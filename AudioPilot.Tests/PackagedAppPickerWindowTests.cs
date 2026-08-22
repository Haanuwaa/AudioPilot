using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class PackagedAppPickerWindowTests
{
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void SelectedRows_UseReadableTextAndIgnoreScrollbarDoubleClicks(AppTheme theme)
    {
        WithPicker((window, viewModel, _) =>
        {
            var list = (ListBox)window.FindName("AppsList");
            list.SelectedIndex = 0;
            list.UpdateLayout();
            var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
            TextBlock name = Assert.Single(Descendants(item).OfType<TextBlock>(), text => text.Text == "Music Player");
            TextBlock appId = Assert.Single(Descendants(item).OfType<TextBlock>(), text => text.Text == "Music!App");
            Color expected = ((SolidColorBrush)window.FindResource(SystemColors.HighlightTextBrushKey)).Color;
            Assert.Equal(expected, Assert.IsType<SolidColorBrush>(name.Foreground).Color);
            Assert.Equal(expected, Assert.IsType<SolidColorBrush>(appId.Foreground).Color);

            ScrollBar scrollbar = Descendants(list).OfType<ScrollBar>().First();
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
                Source = scrollbar,
            });
            Assert.Empty(viewModel.ConfirmedAppUserModelId);
            Assert.NotNull(viewModel.SelectedApp);

            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                Source = list,
            });
            Assert.Null(viewModel.SelectedApp);
            Assert.False(((Button)window.FindName("SelectButton")).IsEnabled);
            Assert.Null(window.DialogResult);
        }, theme: theme);
    }

    [Fact]
    public void Refresh_DisablesConfirmationAndRetainsTheSelectedAppOnSuccess()
    {
        TaskCompletionSource<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> completion = new();
        int requests = 0;
        WithPicker((window, viewModel, dialogs) =>
        {
            viewModel.SearchText = "Music";
            viewModel.TrySelectAppUserModelId("Music!App");
            NavigationCommands.Refresh.Execute(null, window);
            NavigationCommands.Refresh.Execute(null, window);
            Assert.Equal(1, requests);
            var select = (Button)window.FindName("SelectButton");
            Assert.False(select.IsEnabled);
            select.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Empty(viewModel.ConfirmedAppUserModelId);

            completion.SetResult([new("Music Player Updated", "Music!App", "Music", "App")]);
            PumpDispatcher();
            Assert.True(select.IsEnabled);
            Assert.Equal("Music", viewModel.SearchText);
            Assert.Equal("Music Player Updated", viewModel.SelectedApp?.DisplayName);
            Assert.Empty(dialogs.Requests);
        }, () => { requests++; return completion.Task; });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshFailure_PreservesExistingResultsAndDoesNotShowDialogsAfterClose(bool closeFirst)
    {
        TaskCompletionSource<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> completion = new();
        WithPicker((window, viewModel, dialogs) =>
        {
            viewModel.TrySelectAppUserModelId("Music!App");
            NavigationCommands.Refresh.Execute(null, window);
            if (closeFirst)
            {
                window.Close();
            }
            completion.SetException(new InvalidOperationException("Test inventory failure"));
            PumpDispatcher();
            Assert.Equal(closeFirst ? 0 : 1, dialogs.Requests.Count);
            if (closeFirst)
            {
                Assert.Empty(viewModel.FilteredApps);
                Assert.Empty(window.SelectedAppUserModelId);
            }
            else
            {
                Assert.Equal("Music!App", viewModel.SelectedAppUserModelId);
                Assert.True(((Button)window.FindName("SelectButton")).IsEnabled);
            }
        }, () => completion.Task);
    }

    private static void WithPicker(
        Action<PackagedAppPickerWindow, PackagedAppPickerViewModel, RecordingAppDialogService> action,
        Func<Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>>? refresh = null,
        AppTheme theme = AppTheme.Dark)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current;
            ResourceDictionary originalResources = application.Resources;
            Window? originalMainWindow = application.MainWindow;
            ResourceDictionary resources = new() { ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter() };
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative),
            });
            var viewModel = new PackagedAppPickerViewModel([new("Music Player", "Music!App", "Music", "App")]);
            var dialogs = new RecordingAppDialogService();
            PackagedAppPickerWindow? window = null;
            try
            {
                application.Resources = resources;
                window = new PackagedAppPickerWindow(viewModel, refresh ?? (() => Task.FromResult<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>([])), dialogService: dialogs);
                FrameworkElement content = Assert.IsType<FrameworkElement>(window.Content, exactMatch: false);
                content.Measure(new Size(424, 322));
                content.Arrange(new Rect(0, 0, 424, 322));
                content.UpdateLayout();
                PumpDispatcher();
                action(window, viewModel, dialogs);
            }
            finally
            {
                window?.Close();
                application.Resources = originalResources;
                application.MainWindow = originalMainWindow;
            }
        });
    }

    private static void PumpDispatcher() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}

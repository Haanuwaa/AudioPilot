using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class RoutineEditorWindowTests
{
    private static readonly string[] MenuIconNames = ["Undo", "Redo", "Cut", "Copy", "Paste", "Delete", "SelectAll"];

    [Theory]
    [InlineData(AppTheme.Dark, RoutineTriggerKind.Scheduled)]
    [InlineData(AppTheme.Light, RoutineTriggerKind.Scheduled)]
    [InlineData(AppTheme.Dark, RoutineTriggerKind.Application)]
    [InlineData(AppTheme.Light, RoutineTriggerKind.Application)]
    public void CompactEditor_KeepsControlsInViewportAndVolumeOnlyRoutineSubmittable(AppTheme theme, RoutineTriggerKind trigger)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current;
            ResourceDictionary originalResources = application.Resources;
            Window? originalMainWindow = application.MainWindow;
            ResourceDictionary resources = new()
            {
                ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
                ["InverseBoolToVisibilityConverter"] = new AudioPilot.Helpers.InverseBooleanToVisibilityConverter(),
            };
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative),
            });
            foreach (string name in MenuIconNames)
            {
                resources[$"AppMenu{name}Icon"] = new DrawingImage();
            }

            using var viewModel = new RoutineEditorViewModel([], [], new AudioRoutine
            {
                Name = "Volume routine",
                TriggerKind = trigger,
                MasterVolumePercent = 45,
                TriggerAppPath = @"C:\Apps\Player.exe",
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            });
            RoutineEditorWindow? window = null;
            try
            {
                application.Resources = resources;
                window = new RoutineEditorWindow(viewModel);
                FrameworkElement content = Assert.IsType<FrameworkElement>(window.Content, exactMatch: false);
                content.Measure(new Size(424, 442));
                content.Arrange(new Rect(0, 0, 424, 442));
                content.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

                var viewer = (ScrollViewer)window.FindName("RoutineEditorScrollViewer");
                Assert.Equal(0, viewer.VerticalOffset);
                Assert.True(viewModel.IsVolumeTargetsExpanded);
                Assert.True(Assert.Single(Descendants(content).OfType<Button>(), button => button.IsDefault).IsEnabled);
                Assert.Null(viewModel.Validate());
                Assert.Null(window.ResultRoutine);

                FrameworkElement body = Assert.IsType<FrameworkElement>(viewer.Content, exactMatch: false);
                FrameworkElement[] fields = [.. Descendants(body).OfType<FrameworkElement>()
                    .Where(element => element is TextBox or ComboBox or CheckBox && element.ActualWidth > 0 && element.ActualHeight > 0)];
                Assert.NotEmpty(fields);
                foreach (FrameworkElement field in fields)
                {
                    Rect bounds = field.TransformToAncestor(body).TransformBounds(new Rect(field.RenderSize));
                    Assert.True(bounds.Left >= -0.5 && bounds.Right <= viewer.ViewportWidth + 0.5,
                        $"{field.GetType().Name} extends outside the editor viewport: {bounds}, viewport={viewer.ViewportWidth}");
                }
            }
            finally
            {
                window?.Close();
                application.Resources = originalResources;
                application.MainWindow = originalMainWindow;
            }
        });
    }

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

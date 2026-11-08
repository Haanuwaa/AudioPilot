using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

[Collection("WpfApplicationIsolation")]
[Trait(TestCategories.Name, TestCategories.VisualWpf)]
public sealed partial class KeyboardFocusScrollBehaviorTests
{
    [VisualIntegrationTheory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void KeyboardTraversal_KeepsWholeControlsVisible_AfterThemeReplacement(AppTheme theme)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Window window = TestWindowFactory.CreateOffscreenWindow(width: 360, height: 240);
            var panel = new StackPanel { Margin = new Thickness(16) };
            TextBox[] fields = [.. Enumerable.Range(0, 10).Select(index =>
                new TextBox { Text = $"Field {index}", Height = 32, Margin = new Thickness(0, 8, 0, 8) })];
            foreach (TextBox field in fields) panel.Children.Add(field);
            var viewer = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            window.Content = viewer;
            var behavior = new KeyboardFocusScrollBehavior();
            behavior.Attach(viewer);
            try
            {
                TestWindowFactory.ShowWindowForTest(window);
                window.Activate();
                foreach (AppTheme currentTheme in new[] { theme, theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark })
                {
                    window.Resources.MergedDictionaries.Clear();
                    window.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"/AudioPilot;component/Themes/{currentTheme}Theme.xaml", UriKind.Relative),
                    });
                    window.UpdateLayout();
                    Assert.True(fields[0].Focus());
                    nint handle = new WindowInteropHelper(window).Handle;
                    _ = SendMessage(handle, 0x0100, 0x27, 1);
                    _ = SendMessage(handle, 0x0101, 0x27, unchecked((nint)0xC0000001));
                    Assert.IsType<KeyboardDevice>(InputManager.Current.MostRecentInputDevice, exactMatch: false);
                    for (int index = 1; index < fields.Length; index++)
                    {
                        Assert.True(fields[index - 1].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                        AssertVisible(fields[index]);
                    }
                    Assert.True(viewer.VerticalOffset > 0);
                    for (int index = fields.Length - 2; index >= 0; index--)
                    {
                        Assert.True(fields[index + 1].MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
                        AssertVisible(fields[index]);
                    }

                    Assert.True(fields[^1].Focus());
                    Assert.True(fields[0].Focus());
                    AssertVisible(fields[0]);
                }

                void AssertVisible(TextBox field)
                {
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    Assert.Same(field, Keyboard.FocusedElement);
                    Rect bounds = field.TransformToAncestor(viewer).TransformBounds(new Rect(field.RenderSize));
                    Assert.InRange(bounds.Top, 11, viewer.ViewportHeight);
                    Assert.InRange(bounds.Bottom, 0, viewer.ViewportHeight - 11);
                }
            }
            finally
            {
                behavior.Detach();
                window.Close();
            }
        });
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}

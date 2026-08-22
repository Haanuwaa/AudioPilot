using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Helpers;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Tests.Behaviors;

[Collection("WpfApplicationIsolation")]
[Trait(TestCategories.Name, TestCategories.VisualWpf)]
public sealed class HotkeyCaptureWindowTests
{
    private static readonly string[] ThemeChoices = ["Dark", "Light"];
    private static readonly string[] DeviceChoices = ["Desk", "Headset"];

    [VisualIntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrayHide_EndsEditingAndDoesNotRecaptureOnRestore(bool minimize)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var down = new HashSet<int> { 0x77 };
            var capture = new HotkeyCaptureSession(down.Contains);
            var window = new TrayTestWindow { Width = 320, Height = 140, ShowInTaskbar = false };
            var field = new TextBox { Margin = new Thickness(16), Height = 32 };
            var panel = new StackPanel();
            panel.Children.Add(new Button { Content = "Other control" });
            panel.Children.Add(field);
            window.Content = panel;
            var behavior = new HotkeyCaptureBehavior(capture, () => 0) { Target = new HotkeyViewModel() };
            Interaction.GetBehaviors(field).Add(behavior);
            try
            {
                window.Show();
                WindowFirstPresentationHelper.Activate(window);
                MainWindowInteractionHelper.ClearWidgetFocus(window);
                TestWindowFactory.CompleteInitialLayout(window);
                Assert.True(field.Focus());
                Assert.True(capture.IsActive);
                down.Add(0x87);
                var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(field));
                field.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F24)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                if (minimize) window.WindowState = WindowState.Minimized;
                else
                {
                    MainWindowInteractionHelper.ClearWidgetFocus(window);
                    window.Hide();
                }
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(window.IsVisible);
                Assert.True(capture.IsActive);
                var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.Changed += () => { if (!capture.IsActive) resumed.TrySetResult(); };
                down.Remove(0x87);
                await resumed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(capture.IsActive);
                Assert.Contains(0x77, down);
                Assert.NotSame(field, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(field)));
                window.Show();
                WindowFirstPresentationHelper.Activate(window);
                MainWindowInteractionHelper.ClearWidgetFocus(window);
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(field.IsKeyboardFocusWithin);
                Assert.False(capture.IsActive);
                Assert.True(field.Focus());
                Assert.True(capture.IsActive);
            }
            finally
            {
                Interaction.GetBehaviors(field).Remove(behavior);
                window.Close();
                down.Clear();
                capture.TryResume();
            }
        });
    }

    [VisualIntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrayHide_ClearsOrdinaryWidgetFocusWithoutChangingValues(bool minimize)
    {
        SharedStaDispatcherHost.Run(() =>
        {
            var field = new TextBox { Text = "Unsaved draft" };
            var button = new Button { Content = "Save" };
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = 42 };
            var combo = new ComboBox { ItemsSource = ThemeChoices, SelectedIndex = 1 };
            var list = new ListBox { ItemsSource = DeviceChoices, SelectedIndex = 1 };
            Control[] widgets = [field, button, slider, combo, list];
            var panel = new StackPanel();
            foreach (Control widget in widgets) panel.Children.Add(widget);
            var window = new TrayTestWindow { Width = 320, Height = 300, ShowInTaskbar = false, Content = panel };
            try
            {
                window.Show();
                WindowFirstPresentationHelper.Activate(window);
                TestWindowFactory.CompleteInitialLayout(window);
                foreach (Control widget in widgets)
                {
                    Assert.True(widget.Focus());
                    if (minimize) window.WindowState = WindowState.Minimized;
                    else
                    {
                        MainWindowInteractionHelper.ClearWidgetFocus(window);
                        window.Hide();
                    }
                    window.Show();
                    WindowFirstPresentationHelper.Activate(window);
                    MainWindowInteractionHelper.ClearWidgetFocus(window);
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.All(widgets, control => Assert.False(control.IsKeyboardFocusWithin));
                    Assert.Same(window, FocusManager.GetFocusedElement(window));
                    Assert.Equal("Unsaved draft", field.Text);
                    Assert.Equal(42, slider.Value);
                    Assert.Equal(1, combo.SelectedIndex);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private sealed class TrayTestWindow : Window
    {
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState != WindowState.Minimized) return;
            MainWindowInteractionHelper.ClearWidgetFocus(this);
            ShowActivated = false;
            Hide();
            Opacity = 0;
            ShowInTaskbar = false;
            WindowState = WindowState.Normal;
        }
    }
}

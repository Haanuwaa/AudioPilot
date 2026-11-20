using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Platform;

[Collection("WpfApplicationIsolation")]
public sealed partial class WindowThemeHelperTests
{
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void PlainTextInputsUseOwnedThemedMenusAndReadoutsRemainMenuFree(AppTheme theme)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var application = Application.Current;
            ResourceDictionary previous = application.Resources;
            try
            {
                application.Resources = [];
                WindowThemeHelper.ApplyThemeResources(application.Resources, theme, highContrast: false);
                var first = new TextBox { Text = "Search text" };
                var second = new TextBox { Text = "Another field" };
                var fields = new StackPanel();
                fields.Children.Add(first);
                fields.Children.Add(second);
                fields.Measure(new Size(400, 200));
                fields.Arrange(new Rect(0, 0, 400, 200));
                Assert.NotNull(first.Style);
                first.ApplyTemplate();
                second.ApplyTemplate();
                var menu = Assert.IsType<ContextMenu>(first.ContextMenu);
                Assert.NotSame(menu, second.ContextMenu);
                Assert.Same(application.Resources["AppContextMenuStyle"], menu.Style);
                Assert.True(menu.OverridesDefaultStyle);
                Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.Same(application.Resources["AppContextMenuItemStyle"], item.Style));
                var custom = new ContextMenu();
                second.ContextMenu = custom;
                Assert.Same(custom, second.ContextMenu);
                var readout = new TextBox { Style = (Style)application.Resources["AppSelectableTextStyle"], Text = "Read-only details" };
                Assert.True(readout.IsReadOnly);
                Assert.Null(readout.ReadLocalValue(FrameworkElement.ContextMenuProperty));
                Assert.False(readout.IsReadOnlyCaretVisible);
            }
            finally { application.Resources = previous; }
        });
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);

    [Fact]
    public void ThemeResources_ReuseUnchangedPaletteAndRestoreAfterHighContrast()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var resources = new ResourceDictionary();
            foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: false);
                ResourceDictionary normal = Assert.Single(resources.MergedDictionaries);
                Color normalBackground = Assert.IsType<SolidColorBrush>(normal["WindowBackgroundBrush"]).Color;
                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: false);
                Assert.Same(normal, Assert.Single(resources.MergedDictionaries));

                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: true);
                ResourceDictionary contrast = Assert.Single(resources.MergedDictionaries);
                Assert.NotSame(normal, contrast);
                AssertBrushColor(contrast, "WindowBackgroundBrush", SystemColors.WindowColor);
                AssertBrushColor(contrast, "LinkHoverBrush", SystemColors.WindowTextColor);
                AssertBrushColor(contrast, "AudioMeterLowBrush", SystemColors.WindowTextColor);
                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: true);
                Assert.Same(contrast, Assert.Single(resources.MergedDictionaries));

                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: false);
                ResourceDictionary restored = Assert.Single(resources.MergedDictionaries);
                Assert.NotSame(contrast, restored);
                AssertBrushColor(restored, "WindowBackgroundBrush", normalBackground);
            }
        });
    }

    [Fact]
    public void ThemeResources_RemoveDuplicatesAndPreserveOtherResources()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var resources = new ResourceDictionary();
            var unrelated = new ResourceDictionary { ["Sentinel"] = "preserved" };
            resources.MergedDictionaries.Add(unrelated);
            WindowThemeHelper.ApplyThemeResources(resources, AppTheme.Dark, highContrast: false);
            resources.MergedDictionaries.Add(resources.MergedDictionaries[0]);

            WindowThemeHelper.ApplyThemeResources(resources, AppTheme.Light, highContrast: false);

            Assert.Equal(2, resources.MergedDictionaries.Count);
            Assert.Same(unrelated, resources.MergedDictionaries[1]);
            Assert.Equal("preserved", resources["Sentinel"]);
            Assert.EndsWith("LightTheme.xaml", resources.MergedDictionaries[0].Source.OriginalString);
        });
    }

    [Fact]
    public void LiveThemeChanges_PreservePickerViewportAndRecolorExistingRows()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application app = Application.Current;
            ResourceDictionary previousResources = app.Resources;
            Window? previousWindow = app.MainWindow;
            QuickDevicePickerWindow? picker = null;
            try
            {
                app.Resources = [];
                WindowThemeHelper.ApplyThemeResources(app.Resources, AppTheme.Dark, highContrast: false);
                picker = new QuickDevicePickerWindow(_ => throw new InvalidOperationException("Unshown picker."),
                    _ => throw new InvalidOperationException("No device switch was requested."), 0);
                WindowThemeHelper.ApplyThemeResources(app.Resources, AppTheme.Dark, highContrast: false);
                var model = Assert.IsType<QuickDevicePickerViewModel>(picker.DataContext);
                model.Update([.. Enumerable.Range(0, 60).Select(index => new QuickDevicePickerItem(
                    $"device-{index}", $"Output device {index}", null, Output: true, Available: true, Current: false, CanReconnect: false))]);
                var list = Assert.IsType<ListBox>(picker.FindName("DeviceList"));
                var root = Assert.IsType<Grid>(picker.Content);
                Layout(root);
                var viewport = WpfVisualTree.Descendants(list).OfType<ScrollViewer>().First();
                var first = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var row = Assert.IsType<Border>(first.Template.FindName("Bd", first));
                var itemsHost = Assert.IsType<VirtualizingStackPanel>(VisualTreeHelper.GetParent(first));
                Assert.True(first.IsSelected);
                Assert.Same(app.Resources["AccentBrush"], row.Background);
                viewport.ScrollToVerticalOffset(200);
                Layout(root);
                Assert.Equal(200, viewport.VerticalOffset);
                WindowThemeHelper.ApplyThemeResources(app.Resources, AppTheme.Light, highContrast: true);
                Layout(root);
                Assert.True(list.IsAncestorOf(viewport));
                Assert.True(list.IsAncestorOf(itemsHost));
                Assert.Equal(200, viewport.VerticalOffset);
                Assert.Equal(0, list.SelectedIndex);
                viewport.ScrollToTop();
                Layout(root);
                first = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                row = Assert.IsType<Border>(first.Template.FindName("Bd", first));
                AssertBrushColor(app.Resources, "AccentBrush", SystemColors.HighlightColor);
                Assert.Same(app.Resources["AccentBrush"], row.Background);

                WindowThemeHelper.ApplyThemeResources(app.Resources, AppTheme.Light, highContrast: false);
                Layout(root);
                list.SelectedIndex = 1;
                SetReadOnlyState(first, typeof(UIElement), "IsMouseOverPropertyKey", true);
                Assert.Same(app.Resources["ControlHoverBrush"], row.Background);
            }
            finally
            {
                picker?.Close();
                app.MainWindow = previousWindow;
                app.Resources = previousResources;
            }
        });
    }

    [Fact]
    public void ComboBoxHighlight_FollowsKeyboardAndPointerWithPairedThemeColors()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application app = Application.Current;
            ResourceDictionary previousResources = app.Resources;
            Window? previousWindow = app.MainWindow;
            Window? host = null;
            try
            {
                app.Resources = [];
                WindowThemeHelper.ApplyThemeResources(app.Resources, AppTheme.Dark, highContrast: false);
                var keyboardItem = new ComboBoxItem { Content = "Next option" };
                var pointerItem = new ComboBoxItem { Content = "Another option" };
                var selectedItem = new ComboBoxItem { Content = "Current option", IsSelected = true };
                var root = new StackPanel { Children = { keyboardItem, pointerItem, selectedItem } };
                host = new Window { Content = root };
                Layout(root);
                SetReadOnlyState(keyboardItem, typeof(ComboBoxItem), "IsHighlightedPropertyKey", true);
                SetReadOnlyState(pointerItem, typeof(UIElement), "IsMouseOverPropertyKey", true);

                foreach ((AppTheme theme, bool contrast) in new[] { (AppTheme.Dark, false), (AppTheme.Light, false), (AppTheme.Light, true) })
                {
                    WindowThemeHelper.ApplyThemeResources(app.Resources, theme, contrast);
                    Layout(root);
                    var selectedBorder = Assert.IsType<Border>(selectedItem.Template.FindName("Bd", selectedItem));
                    AssertColor((Brush)app.Resources["AccentBrush"], selectedBorder.Background);
                    Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(selectedBorder.BorderBrush).Color);
                    foreach (ComboBoxItem active in new[] { keyboardItem, pointerItem })
                    {
                        var border = Assert.IsType<Border>(active.Template.FindName("Bd", active));
                        AssertColor((Brush)app.Resources["AccentSelectionHoverBrush"], border.Background);
                        AssertColor((Brush)app.Resources["DialogPrimaryActionForegroundBrush"], active.Foreground);
                        AssertColor(active.Foreground, border.BorderBrush);
                    }
                }
            }
            finally
            {
                host?.Close();
                app.MainWindow = previousWindow;
                app.Resources = previousResources;
            }
        });
    }

    [Fact]
    public void ApplicationTheme_UpdatesSecondaryWindowChromeAndBackground()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current;
            ResourceDictionary originalResources = application.Resources;
            Window? originalMainWindow = application.MainWindow;
            var main = new Window();
            var secondary = new Window();
            var overlay = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent };
            try
            {
                application.Resources = [];
                application.MainWindow = main;
                main.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
                secondary.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
                nint mainHandle = new WindowInteropHelper(main).EnsureHandle();
                secondary.Owner = main;
                nint secondaryHandle = new WindowInteropHelper(secondary).EnsureHandle();
                nint overlayHandle = new WindowInteropHelper(overlay).EnsureHandle();
                var overlayTarget = Assert.IsType<HwndTarget>(HwndSource.FromHwnd(overlayHandle).CompositionTarget);
                Color overlayBackground = overlayTarget.BackgroundColor;
                foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.Dark })
                {
                    WindowThemeResolver.ApplyApplicationTheme(theme);
                    Assert.Equal(overlayBackground, overlayTarget.BackgroundColor);
                    foreach (Window window in new[] { main, secondary })
                    {
                        nint handle = window == main ? mainHandle : secondaryHandle;
                        var target = Assert.IsType<HwndTarget>(HwndSource.FromHwnd(handle).CompositionTarget);
                        Color background = Assert.IsType<SolidColorBrush>(window.Background).Color;
                        Assert.Equal(Color.FromRgb(background.R, background.G, background.B), target.BackgroundColor);
                        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                        {
                            Assert.Equal(0, DwmGetWindowAttribute(handle, 20, out int dark, sizeof(int)));
                            Assert.Equal(theme == AppTheme.Dark && !SystemParameters.HighContrast ? 1 : 0, dark);
                        }
                    }
                }
            }
            finally
            {
                overlay.Close();
                secondary.Close();
                main.Close();
                application.MainWindow = originalMainWindow;
                application.Resources = originalResources;
            }
        });
    }

    [Fact]
    public void ApplyWindowTheme_AfterDispatcherShutdown_DoesNotDispatch()
    {
        Window? window = null;
        TestExecutionGuards.RunSta(() =>
        {
            window = new Window();
            window.Close();
        });
        Assert.NotNull(window);
        Assert.True(window.Dispatcher.HasShutdownFinished);
        WindowThemeResolver.ApplyWindowTheme(window, AppTheme.Dark);
    }

    [Fact]
    public void ApplyHighContrastPalette_UsesCurrentSystemColorPairs()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var frozenBackground = new SolidColorBrush(Colors.Red);
            frozenBackground.Freeze();
            var resources = new ResourceDictionary
            {
                ["WindowBackgroundBrush"] = frozenBackground,
                ["ControlBackgroundBrush"] = new SolidColorBrush(Colors.Red),
                ["AccentBrush"] = new SolidColorBrush(Colors.Red),
                ["AccentSelectionHoverBrush"] = new SolidColorBrush(Colors.Red),
                ["AccentTextBrush"] = new SolidColorBrush(Colors.Red),
                ["CheckMarkBrush"] = new SolidColorBrush(Colors.Red),
                ["TextBrush"] = new SolidColorBrush(Colors.Red),
                ["PlaceholderTextBrush"] = new SolidColorBrush(Colors.Red),
                ["BorderBrush"] = new SolidColorBrush(Colors.Red),
                ["TrayMenuHoverBackgroundBrush"] = new SolidColorBrush(Colors.Red),
                ["TrayMenuHoverForegroundBrush"] = new SolidColorBrush(Colors.Red),
                ["KeyboardFocusOuterBrush"] = new SolidColorBrush(Colors.Red),
            };

            WindowThemeHelper.ApplyHighContrastPalette(resources);

            Assert.NotSame(frozenBackground, resources["WindowBackgroundBrush"]);
            Assert.Equal(Colors.Red, frozenBackground.Color);
            AssertBrushColor(resources, "WindowBackgroundBrush", SystemColors.WindowColor);
            AssertBrushColor(resources, "ControlBackgroundBrush", SystemColors.WindowColor);
            AssertBrushColor(resources, "AccentBrush", SystemColors.HighlightColor);
            AssertBrushColor(resources, "AccentSelectionHoverBrush", SystemColors.HighlightColor);
            AssertBrushColor(resources, "AccentTextBrush", SystemColors.WindowTextColor);
            AssertBrushColor(resources, "CheckMarkBrush", SystemColors.HighlightTextColor);
            AssertBrushColor(resources, "TextBrush", SystemColors.WindowTextColor);
            AssertBrushColor(resources, "PlaceholderTextBrush", SystemColors.WindowTextColor);
            AssertBrushColor(resources, "BorderBrush", SystemColors.WindowTextColor);
            AssertBrushColor(resources, "TrayMenuHoverBackgroundBrush", SystemColors.HighlightColor);
            AssertBrushColor(resources, "TrayMenuHoverForegroundBrush", SystemColors.HighlightTextColor);
            AssertBrushColor(resources, "KeyboardFocusOuterBrush", SystemColors.HighlightColor);
        });
    }

    private static void AssertBrushColor(ResourceDictionary resources, string key, Color expected)
    {
        var brush = Assert.IsType<SolidColorBrush>(resources[key]);
        Assert.Equal(expected, brush.Color);
        Assert.Equal(1d, brush.Opacity);
    }

    private static void SetReadOnlyState(Control control, Type owner, string field, bool value)
    {
        var key = Assert.IsType<DependencyPropertyKey>(owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null));
        control.SetValue(key, value);
    }

    private static void AssertColor(Brush expected, Brush actual) =>
        Assert.Equal(Assert.IsType<SolidColorBrush>(expected).Color, Assert.IsType<SolidColorBrush>(actual).Color);

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(460, 440));
        element.Arrange(new Rect(0, 0, 460, 440));
        element.UpdateLayout();
        element.Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);
        element.UpdateLayout();
    }
}

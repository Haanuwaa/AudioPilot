using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Controls;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed partial class MainWindowPresentationTests
{
    [Theory]
    [InlineData(AppTheme.Dark, 0)]
    [InlineData(AppTheme.Light, 0)]
    [InlineData(AppTheme.Dark, 1)]
    [InlineData(AppTheme.Light, 1)]
    public async Task ToolbarThemeFeedbackBelongsToTheEditedTab_AndPreservesSettingsEdits(AppTheme theme, int tab)
    {
        await WithWindow(theme, async (window, model) =>
        {
            AppTheme changed = theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
            bool sounds = model.SettingsPlayAppSoundsDraft;
            model.SelectedSettingsTabIndex = tab;
            model.Theme = changed;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(tab == 0 ? TabSaveState.Unsaved : TabSaveState.None, model.OutputTabSaveState);
            Assert.Equal(tab == 1 ? TabSaveState.Unsaved : TabSaveState.None, model.InputTabSaveState);
            Assert.Equal(TabSaveState.None, model.SettingsTabSaveState);
            Assert.Contains("Theme", tab == 0 ? model.OutputTabFeedbackText : model.InputTabFeedbackText, StringComparison.Ordinal);
            model.SelectedSettingsTabIndex = 1 - tab;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(tab == 0 ? TabSaveState.Unsaved : TabSaveState.None, model.OutputTabSaveState);
            Assert.Equal(tab == 1 ? TabSaveState.Unsaved : TabSaveState.None, model.InputTabSaveState);
            model.SettingsPlayAppSoundsDraft = !sounds;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(TabSaveState.Unsaved, model.SettingsTabSaveState);
            Assert.Contains("App sounds", model.SettingsTabFeedbackText, StringComparison.Ordinal);
            Assert.DoesNotContain("Theme", model.SettingsTabFeedbackText, StringComparison.Ordinal);
            model.SettingsPlayAppSoundsDraft = sounds;
            model.Theme = theme;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(TabSaveState.None, model.OutputTabSaveState);
            Assert.Equal(TabSaveState.None, model.InputTabSaveState);
            Assert.Equal(TabSaveState.None, model.SettingsTabSaveState);
            model.Theme = changed;
            await model.SaveSettingsForTestsAsync();
            Assert.Equal(changed, TestPrivateAccess.GetField<SettingsService>(model, "_settings").LoadSettings().Theme);
            Assert.NotEqual(TabSaveState.Unsaved, model.OutputTabSaveState);
            Assert.NotEqual(TabSaveState.Unsaved, model.InputTabSaveState);
            Assert.Equal(TabSaveState.None, model.SettingsTabSaveState);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task SchedulerAvailabilityFollowsLiveStartup_AndRetainsTheMethodPreference(AppTheme theme)
    {
        await WithWindow(theme, async (window, model) =>
        {
            ((TabControl)window.FindName("DeviceTabControl")).SelectedIndex = 3;
            await Layout(window, 414, 340);
            var scheduler = Assert.Single(WpfVisualTree.Descendants((FrameworkElement)window.Content).OfType<CheckBox>(),
                control => AutomationProperties.GetName(control) == "Task Scheduler for startup");
            model.SettingsUseScheduledStartupDraft = true;
            model.SettingsRunAtStartupDraft = true;
            Assert.False(model.RunAtStartup);
            Assert.False(scheduler.IsEnabled);
            model.RunAtStartup = true;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.True(scheduler.IsEnabled);
            model.RunAtStartup = false;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.False(scheduler.IsEnabled);
            Assert.True(scheduler.IsChecked);
            Assert.True(model.SettingsUseScheduledStartupDraft);
        });
    }

    private static readonly string[] MenuIcons = ["Copy", "Cut", "Delete", "Duplicate", "Expand", "Paste", "Redo", "SelectAll", "Undo", "Output", "Input", "Stop", "SetDefault", "Settings"];
    private static readonly string[] ConfirmationLabels = ["Confirm", "_Confirm"];
    private static readonly double[] RoutineLayoutWidths = [414, 446, 620];

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task NotSavedFeedbackAndFieldValidationUseTheThemeWithoutChangingFieldSize(AppTheme theme)
    {
        await WithWindow(theme, async (window, model) =>
        {
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            tabs.SelectedIndex = 3;
            model.SettingsForegroundVolumeControlsExpanded = true;
            await Layout(window, 414, 340);
            var field = Assert.Single(WpfVisualTree.Descendants((FrameworkElement)window.Content).OfType<TextBox>(), control =>
                control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(AppViewModel.SettingsOverlayDurationSecondsDraft));
            Size original = field.RenderSize;
            model.SettingsOverlayDurationSecondsDraft = "99";
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.True(Validation.GetHasError(field));
            var unrelated = Assert.Single(WpfVisualTree.Descendants((FrameworkElement)window.Content).OfType<TextBox>(), control =>
                control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == nameof(AppViewModel.SettingsForegroundVolumeStepPercentDraft));
            Assert.False(Validation.GetHasError(unrelated));
            Assert.Contains("0.5 to 10", AutomationProperties.GetHelpText(field), StringComparison.Ordinal);
            Assert.Equal(ColorOf((Brush)window.FindResource("DialogErrorBrush")), ColorOf(field.BorderBrush));
            Assert.Equal(original, field.RenderSize);

            await TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "ui-validation");
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            var tab = (TabItem)tabs.Items[3];
            Assert.Equal(TabSaveState.NotSaved, tab.Tag);
            var indicator = Assert.Single(WpfVisualTree.Descendants(tab).OfType<ContentControl>(), control => control.Name == "TabSaveIndicator");
            var mark = Assert.IsType<System.Windows.Shapes.Path>(indicator.Template.FindName("NotSavedMark", indicator));
            Assert.Equal(Visibility.Visible, mark.Visibility);
            Assert.Equal(ColorOf((Brush)window.FindResource("DialogErrorBrush")), ColorOf(mark.Stroke));
            Assert.Contains("weren't saved", AutomationProperties.GetItemStatus(tab), StringComparison.Ordinal);
            var dialogs = TestPrivateAccess.GetField<TestDoubles.RecordingAppDialogService>(model, "_dialogs");
            Assert.Empty(dialogs.WarningMessages);
            Assert.Empty(dialogs.ErrorMessages);

            model.SettingsOverlayDurationSecondsDraft = "3.5";
            await TestPrivateAccess.InvokeNonPublicTask(model, "RunAutoSaveAsync", "ui-correction");
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.False(Validation.GetHasError(field));
            Assert.Equal(TabSaveState.None, model.SettingsTabSaveState);
            Assert.Equal(original, field.RenderSize);
        }, autoSave: true);
    }

    [Fact]
    public async Task DiagnosticReviewNavigatesWithoutApplyingDraftSettings()
    {
        await WithWindow(AppTheme.Dark, async (window, model) =>
        {
            model.SettingsAutoSaveEnabledDraft = true;
            Assert.True(model.IsAutoSavePendingActivation);
            TestWindowFactory.ShowWindowForLayoutTest(window);
            window.ReviewDiagnosticSetting(new(3, "Log level"));
            await Layout(window, 414, 340);
            Assert.Equal(3, model.SelectedSettingsTabIndex);
            Assert.Equal("Log level", ((TextBox)window.FindName("SettingsSearchBox")).Text);
            Assert.NotEqual("No matches", ((TextBlock)window.FindName("SettingsSearchStatus")).Text);
            window.ReviewDiagnosticSetting(new(1));
            await Layout(window, 414, 340);
            Assert.Equal(1, model.SelectedSettingsTabIndex);
            Assert.True(model.SettingsAutoSaveEnabledDraft);
            Assert.True(model.IsAutoSavePendingActivation);
            Assert.False(model.IsAutoSaveActive);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task SettingsKeepsNavigationAndApplyVisible_AndFindDoesNotCaptureHotkeys(AppTheme theme)
    {
        await WithWindow(theme, async (window, model) =>
        {
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            tabs.SelectedIndex = 3;
            await Layout(window, 414, 340);
            Assert.Equal(3, tabs.SelectedIndex);
            var root = (FrameworkElement)window.Content;
            var viewer = (ScrollViewer)window.FindName("MainContentScrollViewer");
            var apply = Assert.Single(WpfVisualTree.Descendants(root).OfType<Button>(), button => ReferenceEquals(button.Command, model.ApplySettingsCommand));
            Assert.False(viewer.IsAncestorOf(tabs));
            Assert.False(viewer.IsAncestorOf(apply));
            viewer.ScrollToEnd();
            await Layout(window, 414, 340);
            AssertVisible(tabs, root); AssertVisible(apply, root);
            var footer = Assert.IsType<WrapPanel>(apply.Parent);
            Rect footerBounds = footer.TransformToAncestor(root).TransformBounds(new Rect(footer.RenderSize));
            Assert.InRange(Math.Abs(footerBounds.Left + footerBounds.Width / 2 - root.ActualWidth / 2), 0, 1);
            var settings = (FrameworkElement)tabs.SelectedContent;
            var reset = Assert.Single(WpfVisualTree.Descendants(settings).OfType<Button>(), button => ReferenceEquals(button.Command, model.ResetToDefaultsCommand));
            var diagnostics = Assert.Single(WpfVisualTree.Descendants(settings).OfType<Button>(), button => ReferenceEquals(button.Command, model.OpenDiagnosticsCommand));
            var perApp = Assert.Single(WpfVisualTree.Descendants(settings).OfType<Button>(), button => ReferenceEquals(button.Command, model.ResetPerAppAudioRoutingCommand));
            var layout = Assert.Single(WpfVisualTree.Descendants(settings).OfType<Button>(), button => ReferenceEquals(button.Command, model.ResetWindowLayoutCommand));
            Assert.Equal("Diagnostics\u2026", diagnostics.Content);
            Assert.Equal("Reset per-app audio\u2026", perApp.Content);
            Assert.Equal("Reset app settings\u2026", reset.Content);
            Assert.Equal("Import settings\u2026", Assert.Single(WpfVisualTree.Descendants(root).OfType<Button>(), button => ReferenceEquals(button.Command, model.ImportSettingsCommand)).Content);
            Assert.Equal("Export settings\u2026", Assert.Single(WpfVisualTree.Descendants(root).OfType<Button>(), button => ReferenceEquals(button.Command, model.ExportSettingsCommand)).Content);
            Button[] maintenance = [diagnostics, layout, perApp, reset];
            double gap = layout.TranslatePoint(new Point(), settings).Y - diagnostics.TranslatePoint(new Point(), settings).Y - diagnostics.ActualHeight;
            for (int index = 1; index < maintenance.Length; index++)
            {
                var previous = maintenance[index - 1];
                var current = maintenance[index];
                Assert.InRange(Math.Abs(diagnostics.ActualWidth - current.ActualWidth), 0, 1);
                Assert.InRange(Math.Abs(current.TranslatePoint(new Point(), settings).Y - previous.TranslatePoint(new Point(), settings).Y - previous.ActualHeight - gap), 0, 1);
            }
            var field = Assert.Single(WpfVisualTree.Descendants(settings).OfType<TextBox>(), text =>
                Interaction.GetBehaviors(text).OfType<HotkeyCaptureBehavior>().Any(behavior => ReferenceEquals(behavior.Target, model.SettingsShowCurrentTrackHotkeyDraftCapture)));
            Assert.False(ApplicationCommands.Find.CanExecute(null, field));
            Assert.True(ApplicationCommands.Find.CanExecute(null, window));
            var search = (TextBox)window.FindName("SettingsSearchBox");
            Assert.Equal("Find a setting\u2026", TextBoxPlaceholderBehavior.GetText(search));
            search.Text = "media seek step duration";
            await Layout(window, 414, 340);
            Assert.NotEqual("No matches", ((TextBlock)window.FindName("SettingsSearchStatus")).Text);
            Assert.Contains(WpfVisualTree.Descendants(settings).OfType<Expander>(), expander => expander.IsExpanded && expander.Header is TextBlock { Text: "Seek" });
            var seek = Assert.Single(WpfVisualTree.Descendants(settings).OfType<TextBox>(), text => AutomationProperties.GetName(text) == "Media seek step duration");
            AssertCentered(seek);
            search.Text = "check automatically";
            await Layout(window, 414, 340);
            var checkForUpdates = Assert.Single(WpfVisualTree.Descendants(settings).OfType<CheckBox>(), check => AutomationProperties.GetName(check) == "Check automatically");
            AssertCentered(checkForUpdates);
            Capture(root, $"settings-search-{theme}", 414, 340);
            search.Clear();
            await Layout(window, 414, 340);
            viewer.ScrollToEnd();
            await Layout(window, 414, 340);
            Capture(root, $"settings-{theme}", 414, 340);
            WpfVisualAssertions.NoBindingErrors(root);
            tabs.SelectedIndex = 0;
            await Layout(window, 414, 340);
            Assert.DoesNotContain(WpfVisualTree.Descendants((FrameworkElement)viewer.Content).OfType<Button>(), button => ReferenceEquals(button.Command, model.ResetToDefaultsCommand));

            void AssertCentered(FrameworkElement target)
            {
                var content = (FrameworkElement)viewer.Content;
                double midpoint = target.TranslatePoint(new Point(0, target.ActualHeight / 2), content).Y - viewer.VerticalOffset;
                Assert.InRange(Math.Abs(midpoint - viewer.ViewportHeight / 2), 0, 1);
            }
        });
    }

    [Fact]
    public async Task SettingsFindHighlightDismissesOnInteraction_AndReturnsForSearchNavigation()
    {
        await WithWindow(AppTheme.Dark, async (window, model) =>
        {
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            tabs.SelectedIndex = 3;
            TestWindowFactory.ShowWindowForLayoutTest(window);
            var search = (TextBox)window.FindName("SettingsSearchBox");
            var next = (Button)window.FindName("NextSettingButton");
            var previous = (Button)window.FindName("PreviousSettingButton");
            search.Text = "check automatically";
            await Layout(window, 414, 340);
            var find = TestPrivateAccess.GetField<SettingsFindController>(window, "_settingsFind");
            var match = Assert.IsType<CheckBox>(find.CurrentMatch);
            var layer = AdornerLayer.GetAdornerLayer(match);
            Assert.NotNull(layer);
            Assert.Single(layer.GetAdorners(match) ?? []);

            search.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            var arrow = Assert.Single(WpfVisualTree.Descendants(next).OfType<System.Windows.Shapes.Path>());
            arrow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert.Single(layer.GetAdorners(match) ?? []);

            RoutedEventArgs[] interactions =
            [
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent },
                new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent },
                new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent },
                new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, search, match) { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent },
            ];
            foreach (RoutedEventArgs input in interactions)
            {
                match.RaiseEvent(input);
                Assert.Empty(layer.GetAdorners(match) ?? []);
                Assert.Equal("check automatically", search.Text);
                Assert.Same(match, find.CurrentMatch);
                Assert.Equal("1 of 1", find.Status);
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Layout(window, 414, 340);
                Assert.Single(layer.GetAdorners(match) ?? []);
            }

            previous.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            match.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await Layout(window, 414, 340);
            Assert.Empty(layer.GetAdorners(match) ?? []);

            search.Text = "overlay position";
            var apply = Assert.Single(WpfVisualTree.Descendants((FrameworkElement)window.Content).OfType<Button>(), button => ReferenceEquals(button.Command, model.ApplySettingsCommand));
            apply.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await Layout(window, 414, 340);
            var newMatch = Assert.IsType<ComboBox>(find.CurrentMatch);
            Assert.Equal("Overlay position", AutomationProperties.GetName(newMatch));
            Assert.Empty(layer.GetAdorners(newMatch) ?? []);
            Assert.Equal("1 of 1", ((TextBlock)window.FindName("SettingsSearchStatus")).Text);

            previous.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Layout(window, 414, 340);
            Assert.Single(layer.GetAdorners(newMatch) ?? []);
            search.Text = "check automatically";
            await Layout(window, 414, 340);
            Assert.Single(layer.GetAdorners(match) ?? []);

            tabs.SelectedIndex = 0;
            tabs.SelectedIndex = 3;
            await Layout(window, 414, 340);
            Assert.Equal("check automatically", search.Text);
            Assert.Empty(layer.GetAdorners(match) ?? []);
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Layout(window, 414, 340);
            Assert.Single(layer.GetAdorners(match) ?? []);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 414, false)]
    [InlineData(AppTheme.Light, 414, false)]
    [InlineData(AppTheme.Dark, 640, false)]
    [InlineData(AppTheme.Light, 640, false)]
    [InlineData(AppTheme.Dark, 414, true)]
    [InlineData(AppTheme.Light, 414, true)]
    public async Task ApplyingAutoSavePreservesSettingsLayoutAndScroll(AppTheme theme, double width, bool autoSave)
    {
        using var clock = new ManualTimeProvider();
        await WithWindow(theme, async (window, model) =>
        {
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            var viewer = (ScrollViewer)window.FindName("MainContentScrollViewer");
            tabs.SelectedIndex = 3;
            await Layout(window, width, 400);
            var notice = Assert.Single(WpfVisualTree.Descendants((FrameworkElement)window.Content).OfType<Grid>(), grid => grid.Name == "SettingsSaveModeNotice");
            var autoSaveBox = Assert.Single(WpfVisualTree.Descendants((FrameworkElement)window.Content).OfType<CheckBox>(),
                box => box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == nameof(AppViewModel.SettingsAutoSaveEnabledDraft));
            double target = autoSaveBox.TranslatePoint(new Point(), viewer).Y + viewer.VerticalOffset - viewer.ViewportHeight / 2;
            viewer.ScrollToVerticalOffset(target);
            await Layout(window, width, 400);
            double offset = viewer.VerticalOffset;
            double position = autoSaveBox.TranslatePoint(new Point(), viewer).Y;
            double extent = viewer.ExtentHeight;
            double noticeHeight = notice.ActualHeight;
            var offsets = new List<double>();
            void OnScroll(object sender, ScrollChangedEventArgs e) => offsets.Add(viewer.VerticalOffset);
            viewer.ScrollChanged += OnScroll;
            try
            {
                model.SettingsAutoSaveEnabledDraft = !autoSave;
                await model.ApplySettingsForTestsAsync();
                await Layout(window, width, 400);
            }
            finally { viewer.ScrollChanged -= OnScroll; }
            Assert.Equal(!autoSave, model.IsAutoSaveActive);
            var pill = Assert.Single(notice.Children.OfType<Border>());
            Assert.Equal(model.IsAutoSaveActive ? Visibility.Visible : Visibility.Collapsed, pill.Visibility);
            Assert.Equal(3, tabs.SelectedIndex);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - offset), 0, 1);
            Assert.InRange(Math.Abs(autoSaveBox.TranslatePoint(new Point(), viewer).Y - position), 0, 1);
            Assert.InRange(Math.Abs(viewer.ExtentHeight - extent), 0, 1);
            Assert.InRange(Math.Abs(notice.ActualHeight - noticeHeight), 0, 1);
            Assert.All(offsets, value => Assert.InRange(Math.Abs(value - offset), 0, 1));
        }, settingsFeedbackTimeProvider: clock, autoSave: autoSave);
    }

    [Theory]
    [InlineData(AppTheme.Dark, true, false)]
    [InlineData(AppTheme.Light, true, false)]
    [InlineData(AppTheme.Dark, false, false)]
    [InlineData(AppTheme.Light, false, false)]
    [InlineData(AppTheme.Dark, true, true)]
    public async Task WindowLayoutResetPreservesTheScrollPositionAndBottomAlignment(AppTheme theme, bool atBottom, bool switchTabs)
    {
        await WithWindow(theme, async (window, model) =>
        {
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            tabs.SelectedIndex = 3;
            window.Width = 720;
            window.Height = 620;
            TestWindowFactory.ShowWindowForLayoutTest(window);
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            var viewer = (ScrollViewer)window.FindName("MainContentScrollViewer");
            viewer.ScrollToVerticalOffset(atBottom ? viewer.ScrollableHeight : viewer.ScrollableHeight / 3);
            window.UpdateLayout();
            double offset = viewer.VerticalOffset;
            Assert.True(offset > 0);
            using var directory = new TestScopedDirectory(nameof(WindowLayoutResetPreservesTheScrollPositionAndBottomAlignment));
            using var logs = TestLoggerScope.CreateInMemory("layout-scroll.log");
            var placement = new MainWindowPlacementService(Path.Combine(directory.Root, "window-placement.json"), logs.Logger);
            if (switchTabs)
            {
                tabs.SelectedIndex = 0;
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            }
            await placement.ChangeLayoutAsync(new(Reset: true), window, TestContext.Current.CancellationToken);
            if (switchTabs) tabs.SelectedIndex = 3;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            double expected = atBottom ? viewer.ScrollableHeight : Math.Min(offset, viewer.ScrollableHeight);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - expected), 0, 1);
            WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task TabsRememberIndependentScrollPositions_AndRapidChangesDoNotOverwriteThem(AppTheme theme)
    {
        await WithWindow(theme, async (window, model) =>
        {
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            var viewer = (ScrollViewer)window.FindName("MainContentScrollViewer");
            viewer.ScrollToVerticalOffset(90);
            await Layout(window, 414, 340);
            double outputOffset = viewer.VerticalOffset;
            Assert.True(outputOffset > 0);
            tabs.SelectedIndex = 3;
            await Layout(window, 414, 340);
            Assert.Equal(0, viewer.VerticalOffset);
            viewer.ScrollToVerticalOffset(400);
            await Layout(window, 414, 340);
            double settingsOffset = viewer.VerticalOffset;
            Assert.True(settingsOffset > outputOffset);
            tabs.SelectedIndex = 1;
            await Layout(window, 414, 340);
            Assert.Equal(0, viewer.VerticalOffset);
            viewer.ScrollToVerticalOffset(60);
            await Layout(window, 414, 340);
            double inputOffset = viewer.VerticalOffset;
            Assert.True(inputOffset > 0);
            tabs.SelectedIndex = 3;
            await Layout(window, 414, 340);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - settingsOffset), 0, 1);
            tabs.SelectedIndex = 0;
            tabs.SelectedIndex = 1;
            tabs.SelectedIndex = 3;
            await Layout(window, 414, 340);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - settingsOffset), 0, 1);
            tabs.SelectedIndex = 0;
            await Layout(window, 414, 340);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - outputOffset), 0, 1);
            tabs.SelectedIndex = 1;
            await Layout(window, 414, 340);
            Assert.InRange(Math.Abs(viewer.VerticalOffset - inputOffset), 0, 1);
            window.NavigateTo(AudioPilot.Services.UI.MainWindowOpenTarget.Settings);
            await Layout(window, 414, 340);
            Assert.Equal(0, viewer.VerticalOffset);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task DefaultHeightKeepsSaveVisibleOnBothDeviceTabs_WithoutFeedbackChangingTheViewport(AppTheme theme)
    {
        using var clock = new ManualTimeProvider();
        await WithWindow(theme, async (window, model) =>
        {
            TestWindowFactory.ShowWindowForLayoutTest(window);
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            var viewer = (ScrollViewer)window.FindName("MainContentScrollViewer");
            var root = (FrameworkElement)window.Content;
            var save = Assert.Single(WpfVisualTree.Descendants(root).OfType<Button>(), button => ReferenceEquals(button.Command, model.SaveSettingsCommand));
            string originalDuration = model.SettingsOverlayDurationSecondsDraft;
            for (int tab = 0; tab < 2; tab++)
            {
                tabs.SelectedIndex = tab;
                TestWindowFactory.CompleteInitialLayout(window);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                viewer.ScrollToTop(); viewer.UpdateLayout();
                double viewport = viewer.ViewportHeight;
                await TestPrivateAccess.InvokeNonPublicTask(model, "ShowSettingsSaveFeedbackAsync", "Settings saved successfully.", SettingsFeedbackTabs.Output | SettingsFeedbackTabs.Input);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                viewer.UpdateLayout();
                Assert.Equal(viewport, viewer.ViewportHeight);
                var item = (TabItem)tabs.Items[tab];
                Assert.Equal(TabSaveState.Saved, item.Tag);
                var border = Assert.IsType<Border>(item.Template.FindName("Bd", item));
                Assert.Equal(ColorOf((Brush)window.FindResource("AccentBrush")), ColorOf(border.BorderBrush));
                var indicator = Assert.Single(WpfVisualTree.Descendants(item).OfType<ContentControl>(), control => control.Name == "TabSaveIndicator");
                Assert.Equal(Visibility.Visible, indicator.Visibility);
                var check = Assert.IsType<System.Windows.Shapes.Path>(indicator.Template.FindName("SavedCheck", indicator));
                Assert.Equal(Visibility.Visible, check.Visibility);
                Assert.Equal(ColorOf((Brush)window.FindResource("DialogSuccessBrush")), ColorOf(check.Stroke));
                double headerWidth = item.ActualWidth;
                Assert.Contains("saved", AutomationProperties.GetItemStatus(item), StringComparison.OrdinalIgnoreCase);
                Rect bounds = save.TransformToAncestor(viewer).TransformBounds(new Rect(save.RenderSize));
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= viewer.ViewportHeight,
                    $"tab={tab}, height={window.Height}, save={bounds}, viewport={viewer.ViewportHeight}");
                Capture(root, $"save-visible-{theme}-{tab}", (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight));
                model.SettingsOverlayDurationSecondsDraft = "3.5";
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                viewer.UpdateLayout();
                Assert.Equal(TabSaveState.Unsaved, model.SettingsTabSaveState);
                Assert.Equal(TabSaveState.Saved, item.Tag);
                var settings = (TabItem)tabs.Items[3];
                Assert.Equal(TabSaveState.Unsaved, settings.Tag);
                var settingsBorder = Assert.IsType<Border>(settings.Template.FindName("Bd", settings));
                Assert.NotEqual(ColorOf((Brush)window.FindResource("DialogWarningBrush")), ColorOf(settingsBorder.BorderBrush));
                var settingsIndicator = Assert.Single(WpfVisualTree.Descendants(settings).OfType<ContentControl>(), control => control.Name == "TabSaveIndicator");
                Assert.Equal(Visibility.Visible, settingsIndicator.Visibility);
                var dot = Assert.IsType<System.Windows.Shapes.Ellipse>(settingsIndicator.Template.FindName("PendingDot", settingsIndicator));
                Assert.Equal(Visibility.Visible, dot.Visibility);
                Assert.Equal(ColorOf((Brush)window.FindResource("TextBrush")), ColorOf(dot.Fill));
                Assert.Equal(Visibility.Visible, indicator.Visibility);
                Assert.InRange(Math.Abs(headerWidth - item.ActualWidth), 0, 0.1);
                Assert.Contains("Apply settings", AutomationProperties.GetItemStatus(settings), StringComparison.Ordinal);
                Assert.Empty(Interaction.GetBehaviors(settings).OfType<HoverInfoPopupBehavior>());
                Assert.Contains(Interaction.GetBehaviors(settingsIndicator).OfType<HoverInfoPopupBehavior>(), behavior => behavior.Text?.StartsWith("Unsaved: Overlay duration.", StringComparison.Ordinal) == true);
                Assert.Equal(viewport, viewer.ViewportHeight);
                Capture(root, $"unsaved-{theme}-{tab}", (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight));
                clock.Advance(TimeSpan.FromSeconds(4));
                typeof(AppViewModel).GetMethod("SaveConfirmationTimer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [null, EventArgs.Empty]);
                viewer.UpdateLayout();
                Assert.Equal(TabSaveState.None, item.Tag);
                Assert.Equal(Visibility.Collapsed, indicator.Visibility);
                Assert.InRange(headerWidth - item.ActualWidth, 19, 21);
                Assert.Equal(TabSaveState.Unsaved, model.SettingsTabSaveState);
                model.SettingsOverlayDurationSecondsDraft = originalDuration;
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            }
        }, settingsFeedbackTimeProvider: clock);
    }

    [Fact]
    public async Task TabStatusHelpStaysInTheHeaderAndClearsWhenEditsAreReverted()
    {
        await WithWindow(AppTheme.Dark, async (window, model) =>
        {
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            tabs.SelectedIndex = 3;
            TestWindowFactory.ShowWindowForLayoutTest(window);
            string originalDuration = model.SettingsOverlayDurationSecondsDraft;
            string originalMuteHotkey = model.SettingsMuteMicHotkeyDraft;
            model.SettingsOverlayDurationSecondsDraft = "3.5";
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            var tab = (TabItem)tabs.Items[3];
            var page = Assert.IsType<FrameworkElement>(tab.Content, exactMatch: false);
            var indicator = Assert.Single(WpfVisualTree.Descendants(tab).OfType<ContentControl>(), control => control.Name == "TabSaveIndicator");
            var help = Assert.Single(Interaction.GetBehaviors(indicator).OfType<HoverInfoPopupBehavior>());
            int indicatorFocusEvents = 0;
            indicator.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => indicatorFocusEvents++), handledEventsToo: true);
            page.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, page) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            Assert.Equal(0, indicatorFocusEvents);
            Assert.Null(TestPrivateAccess.GetField<CancellationTokenSource?>(help, "_hoverDelayCts"));
            model.SettingsMuteMicHotkeyDraft = "Ctrl+Alt+Shift+F11";
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Contains("Microphone mute hotkey", help.Text ?? string.Empty, StringComparison.Ordinal);
            model.SettingsOverlayDurationSecondsDraft = originalDuration;
            model.SettingsMuteMicHotkeyDraft = originalMuteHotkey;
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);
            Assert.Empty(help.Text ?? string.Empty);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task ColoredLabelsInheritTheirControlForeground_AndDefaultPaletteMeetsTextContrast(AppTheme theme)
    {
        await WithWindow(theme, async (window, model) =>
        {
            var tabs = (TabControl)window.FindName("DeviceTabControl"); tabs.SelectedIndex = 3;
            await Layout(window, 414, 340);
            var settings = (FrameworkElement)tabs.SelectedContent;
            var selected = WpfVisualTree.Descendants(settings).OfType<ToggleButton>().Where(button => button.IsChecked == true && button.Content is "Multimedia" or "Communications" or "Console").ToArray();
            Assert.NotEmpty(selected);
            foreach (var button in selected)
            {
                var label = Assert.Single(WpfVisualTree.Descendants(button).OfType<TextBlock>(), text => text.Text == button.Content.ToString());
                Assert.Equal(ColorOf(button.Foreground), ColorOf(label.Foreground));
                Assert.True(Contrast(ColorOf(label.Foreground), ColorOf((Brush)window.FindResource("AccentBrush"))) >= 4.5);
            }
            var root = (FrameworkElement)window.Content;
            var apply = Assert.Single(WpfVisualTree.Descendants(root).OfType<Button>(), button => ReferenceEquals(button.Command, model.ApplySettingsCommand));
            var applyLabel = Assert.Single(WpfVisualTree.Descendants(apply).OfType<TextBlock>());
            Assert.Equal(ColorOf(apply.Foreground), ColorOf(applyLabel.Foreground));
            Assert.True(Contrast(ColorOf(applyLabel.Foreground), ColorOf((Brush)window.FindResource("PrimaryActionHoverBrush"))) >= 4.5);
            Assert.True(Contrast(ColorOf((Brush)window.FindResource("AccentTextBrush")), ColorOf(window.Background)) >= 4.5);
            foreach (string label in ConfirmationLabels)
            {
                var dialog = new AppDialogWindow(new AppDialogRequest("Review", "Confirm", AppDialogKind.Question,
                    [new(label, AppDialogResult.Acknowledged, AppDialogActionStyle.Primary, isDefault: true, isCancel: true)]));
                try
                {
                    await Layout(dialog, 440, 220);
                    var button = Assert.Single(dialog.ActionsPanel.Children.OfType<Button>());
                    var rendered = WpfVisualTree.Descendants(button).OfType<TextBlock>().ToArray();
                    Assert.NotEmpty(rendered);
                    Assert.All(rendered, text => Assert.Equal(ColorOf(button.Foreground), ColorOf(text.Foreground)));
                }
                finally { dialog.Close(); }
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task MixerReadoutsAndMuteControlsRemainAccessible_AndNamesGrowWhenResized(AppTheme theme)
    {
        await WithWindow(theme, async (window, model) =>
        {
            var mixer = (ListBox)window.FindName("VolumeMixer");
            var item = new AudioSessionItem("Application with a deliberately long descriptive name", 65, false, false, processId: 42, isMuted: true);
            mixer.ItemsSource = new[] { item };
            await Layout(window, 414, 340);
            var row = Assert.Single(WpfVisualTree.Descendants(mixer).OfType<Button>(), button => AutomationProperties.GetName(button) == item.DisplayName);
            double narrow = row.ActualWidth;
            Assert.Contains(WpfVisualTree.Descendants(mixer).OfType<TextBlock>(), text => text.Text == "65%");
            var mute = Assert.Single(WpfVisualTree.Descendants(mixer).OfType<Button>(), button => ReferenceEquals(button.Command, model.ActiveMixer.ToggleMuteCommand));
            Assert.Same(model.ActiveMixer.ToggleMuteCommand, mute.Command);
            Assert.Same(item, mute.CommandParameter);
            var icon = Assert.IsType<System.Windows.Shapes.Path>(mute.Content);
            Assert.Same(AppMenuGlyphs.OutputMuted, icon.Data);
            Assert.Equal($"Unmute {item.DisplayName}", AutomationProperties.GetName(mute));
            Assert.Equal("Muted", AutomationProperties.GetItemStatus(mute));
            var slider = Assert.Single(WpfVisualTree.Descendants(mixer).OfType<Slider>());
            Assert.Equal("Muted", AutomationProperties.GetItemStatus(slider));
            Assert.Contains("Right-click", AutomationProperties.GetHelpText(slider), StringComparison.Ordinal);
            var behaviors = Interaction.GetBehaviors(slider);
            Assert.DoesNotContain(behaviors, behavior => behavior is SliderValuePopupBehavior);
            var mouseMute = Assert.Single(behaviors.OfType<SliderThumbRightClickBehavior>());
            var keyboardMute = Assert.Single(behaviors.OfType<SliderKeyboardAccelerationBehavior>());
            Assert.Same(model.ActiveMixer.ToggleMuteCommand, mouseMute.Command);
            Assert.Same(model.ActiveMixer.ToggleMuteCommand, keyboardMute.MuteCommand);
            Assert.Same(item, mouseMute.CommandParameter);
            Assert.Same(item, keyboardMute.MuteCommandParameter);
            Assert.True(slider.ActualHeight >= 24);
            Assert.NotNull(VisualTreeHelper.HitTest(slider, new Point(slider.ActualWidth / 2, 1)));
            ((ScrollViewer)window.FindName("MainContentScrollViewer")).ScrollToEnd();
            await Layout(window, 414, 340);
            Capture((FrameworkElement)window.Content, $"mixer-compact-{theme}", 414, 340);
            await Layout(window, 900, 340);
            Assert.True(row.ActualWidth > narrow + 100);
            ((ScrollViewer)window.FindName("MainContentScrollViewer")).ScrollToEnd();
            await Layout(window, 900, 340);
            Capture((FrameworkElement)window.Content, $"mixer-{theme}", 900, 340);
            item.SetMuteFromSystem(false);
            await Layout(window, 900, 340);
            Assert.Equal("Unmuted", AutomationProperties.GetItemStatus(slider));
            Assert.Equal(false, slider.Tag);
            Assert.Same(AppMenuGlyphs.Output, icon.Data);
            Assert.Equal($"Mute {item.DisplayName}", AutomationProperties.GetName(mute));
            ((TabControl)window.FindName("DeviceTabControl")).SelectedIndex = 1;
            await Layout(window, 900, 340);
            Assert.Same(AppMenuGlyphs.Input, icon.Data);
            Assert.Same(model.ActiveMixer.ToggleMuteCommand, mute.Command);
            item.SetMuteFromSystem(true);
            await Layout(window, 900, 340);
            Assert.Same(AppMenuGlyphs.InputMuted, icon.Data);
            Capture((FrameworkElement)window.Content, $"recording-mixer-{theme}", 900, 340);
            WindowThemeResolver.ApplyWindowTheme(window, theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
            await Layout(window, 900, 340);
            Assert.Same(window.FindResource(typeof(Slider)), slider.Style);
            Assert.Equal("Muted", AutomationProperties.GetItemStatus(slider));
            Assert.Equal("Muted", AutomationProperties.GetItemStatus(mute));
            var master = new AudioSessionItem("Master Volume", 75, true, false);
            var microphone = new AudioSessionItem("Microphone Volume", 90, false, true);
            var sounds = new AudioSessionItem("System Sounds", 55, false, false, isSystemSounds: true);
            mixer.ItemsSource = new[] { master, microphone, sounds };
            for (int tab = 0; tab < 2; tab++)
            {
                ((TabControl)window.FindName("DeviceTabControl")).SelectedIndex = tab;
                for (int state = 0; state < 2; state++)
                {
                    bool muted = state != 0;
                    master.SetMuteFromSystem(muted);
                    microphone.SetMuteFromSystem(muted);
                    sounds.SetMuteFromSystem(muted);
                    await Layout(window, 900, 340);
                    var masterButton = Assert.Single(WpfVisualTree.Descendants(mixer).OfType<Button>(), button => button.Name == "MixerMuteButton" && ReferenceEquals(button.CommandParameter, master));
                    var micButton = Assert.Single(WpfVisualTree.Descendants(mixer).OfType<Button>(), button => button.Name == "MixerMuteButton" && ReferenceEquals(button.CommandParameter, microphone));
                    var soundsButton = Assert.Single(WpfVisualTree.Descendants(mixer).OfType<Button>(), button => button.Name == "MixerMuteButton" && ReferenceEquals(button.CommandParameter, sounds));
                    Assert.Same(muted ? AppMenuGlyphs.OutputMuted : AppMenuGlyphs.Output, Assert.IsType<System.Windows.Shapes.Path>(masterButton.Content).Data);
                    Assert.Same(muted ? AppMenuGlyphs.InputMuted : AppMenuGlyphs.Input, Assert.IsType<System.Windows.Shapes.Path>(micButton.Content).Data);
                    Assert.Same(muted ? AppMenuGlyphs.OutputMuted : AppMenuGlyphs.Output, Assert.IsType<System.Windows.Shapes.Path>(soundsButton.Content).Data);
                }
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, "en")]
    [InlineData(AppTheme.Light, "en")]
    [InlineData(AppTheme.Dark, "es")]
    [InlineData(AppTheme.Light, "es")]
    public async Task RoutineActionsFitTheirLabelsAtCompactAndWiderWidths(AppTheme theme, string language)
    {
        await WithWindow(theme, async (window, model) =>
        {
            model.Routines.Add(new AudioRoutine { Id = "layout", Name = "Routine 1", MasterVolumePercent = 35 });
            model.SettingsLanguageDraft = language;
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            tabs.SelectedIndex = 2;
            foreach (double width in RoutineLayoutWidths)
            {
                await Layout(window, width, 340);
                var content = (FrameworkElement)tabs.SelectedContent;
                var actions = WpfVisualTree.Descendants(content).OfType<Button>().Where(button => button.Content is string).ToArray();
                Assert.Contains(actions, button => Equals(button.Content, language == "es" ? "Quitar" : "Remove"));
                Assert.Contains(actions, button => Equals(button.Content, language == "es" ? "Desactivar" : "Disable"));
                foreach (var button in actions)
                {
                    var label = new TextBlock
                    {
                        Text = (string)button.Content,
                        FontFamily = button.FontFamily,
                        FontSize = button.FontSize,
                        FontWeight = button.FontWeight,
                        FontStyle = button.FontStyle,
                        FontStretch = button.FontStretch,
                    };
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double available = button.ActualWidth - button.Padding.Left - button.Padding.Right
                        - button.BorderThickness.Left - button.BorderThickness.Right;
                    Assert.True(available + 1 >= label.DesiredSize.Width,
                        $"{button.Content} needs {label.DesiredSize.Width:F1}px, but has {available:F1}px at width {width}.");
                }
            }
            await Layout(window, 414, 340);
            ((ScrollViewer)window.FindName("MainContentScrollViewer")).ScrollToTop();
            await Layout(window, 414, 340);
            Capture((FrameworkElement)window.Content, $"routines-compact-{theme}-{language}", 414, 340);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, Orientation.Vertical)]
    [InlineData(AppTheme.Light, Orientation.Vertical)]
    [InlineData(AppTheme.Dark, Orientation.Horizontal)]
    [InlineData(AppTheme.Light, Orientation.Horizontal)]
    public async Task ScrollbarThumbUsesNativeTravel_WithAUsableMinimumAndShortTrackFallback(AppTheme theme, Orientation orientation)
    {
        await WithWindow(theme, (window, _) =>
        {
            var bar = new ScrollBar
            {
                Orientation = orientation,
                Maximum = 100000,
                ViewportSize = 10,
                Style = (Style)window.FindResource(typeof(ScrollBar)),
            };
            Size size = orientation == Orientation.Vertical ? new Size(10, 200) : new Size(200, 10);
            bar.Measure(size); bar.Arrange(new Rect(size)); bar.UpdateLayout();
            var track = Assert.IsType<AppScrollBarTrack>(bar.Template.FindName("PART_Track", bar));
            double length = orientation == Orientation.Vertical ? track.Thumb.ActualHeight : track.Thumb.ActualWidth;
            Assert.InRange(length, 24, 25);
            bar.Value = bar.Maximum;
            bar.UpdateLayout();
            Point position = track.Thumb.TranslatePoint(new Point(), track);
            double farEdge = orientation == Orientation.Vertical ? position.Y + length : position.X + length;
            double available = orientation == Orientation.Vertical ? track.ActualHeight : track.ActualWidth;
            Assert.InRange(Math.Abs(farEdge - available), 0, 1);
            size = orientation == Orientation.Vertical ? new Size(10, 20) : new Size(20, 10);
            bar.Measure(size); bar.Arrange(new Rect(size)); bar.UpdateLayout();
            Assert.Equal(Visibility.Visible, track.Visibility);
            length = orientation == Orientation.Vertical ? track.Thumb.ActualHeight : track.Thumb.ActualWidth;
            available = orientation == Orientation.Vertical ? track.ActualHeight : track.ActualWidth;
            Assert.InRange(length, 1, available / 2 + 1);
            Assert.True(double.IsFinite(track.ValueFromDistance(1, 1)));
            size = orientation == Orientation.Vertical ? new Size(10, 2) : new Size(2, 10);
            bar.Measure(size); bar.Arrange(new Rect(size)); bar.UpdateLayout();
            Assert.Equal(Visibility.Hidden, track.Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PausedStateNotifiesTheUiWhenResumedFromAWorker()
    {
        await WithWindow(AppTheme.Dark, async (window, model) =>
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.PauseAutomaticRoutines(null);
            Assert.True(model.AutomaticRoutinesPaused);
            Assert.True(model.ResumeAutomaticRoutinesCommand.CanExecute(null));
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(model.AutomaticRoutinesPaused) && !model.AutomaticRoutinesPaused)
                {
                    Assert.True(window.Dispatcher.CheckAccess());
                    changed.TrySetResult();
                }
            };
            await Task.Run(model.ResumeAutomaticRoutines, TestContext.Current.CancellationToken);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(model.AutomaticRoutinesPaused);
        });
    }

    [VisualIntegrationTheory]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [InlineData(false)]
    [InlineData(true)]
    public Task InterruptedFirstShowRestoresSavedPlacement(bool reopenImmediately) => WithWindow(AppTheme.Dark, async (window, _) =>
    {
        using var directory = new TestScopedDirectory(nameof(InterruptedFirstShowRestoresSavedPlacement));
        var placement = new MainWindowPlacementService(Path.Combine(directory.Root, "placement.json"), Logger.Instance);
        var saved = await placement.ChangeLayoutAsync(new(X: 100, Y: 100, Width: 740, Height: 640));
        await using var manager = new AppMainWindowManager(Application.Current, new TestDoubles.RecordingAppDialogService(), placement: placement);
        manager.SetWindowFactory(() => window);
        Task<bool>? reopened = null;
        window.ContentRendered += Interrupt;

        void Interrupt(object? sender, EventArgs args)
        {
            window.ContentRendered -= Interrupt;
            manager.Hide();
            if (reopenImmediately) reopened = manager.ShowAsync();
        }

        Assert.Equal(reopenImmediately, await manager.ShowAsync());
        if (reopenImmediately) Assert.NotNull(reopened);
        Assert.True(await (reopened ?? manager.ShowAsync()));
        var actual = await manager.GetLayoutAsync();
        Assert.Equal((saved.X, saved.Y, saved.Width, saved.Height), (actual.X, actual.Y, actual.Width, actual.Height));
    });

    [VisualIntegrationFact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    public Task ReopeningHiddenWindowRefitsItsBoundsToTheWorkArea() => WithWindow(AppTheme.Dark, async (window, _) =>
    {
        using var directory = new TestScopedDirectory(nameof(ReopeningHiddenWindowRefitsItsBoundsToTheWorkArea));
        var placement = new MainWindowPlacementService(Path.Combine(directory.Root, "placement.json"), Logger.Instance);
        await using var manager = new AppMainWindowManager(Application.Current, new TestDoubles.RecordingAppDialogService(), placement: placement);
        manager.SetWindowFactory(() => window);
        Assert.True(await manager.ShowAsync());
        var original = await manager.GetLayoutAsync();
        Assert.True(manager.Hide());

        nint handle = new WindowInteropHelper(window).Handle;
        var monitor = WindowPlacementHelper.GetLayoutMonitor(window: handle);
        int workAreaBottom = monitor.WorkTop + monitor.WorkHeight;
        WindowPlacementHelper.ApplyLayout(handle, original with { Y = workAreaBottom - 60 });
        var hidden = await placement.GetLayoutAsync(window);
        Assert.True(hidden.Y + hidden.Height > workAreaBottom);

        Assert.True(await manager.ShowAsync());
        var actual = await manager.GetLayoutAsync();
        Assert.True(actual.Y + actual.Height <= workAreaBottom);
        Assert.Equal((original.Width, original.Height), (actual.Width, actual.Height));
    });

    private static async Task WithWindow(AppTheme theme, Func<MainWindow, AppViewModel, Task> action, TimeProvider? settingsFeedbackTimeProvider = null, bool autoSave = false, int initialTab = 0)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources; Window? previousWindow = app.MainWindow;
            var resources = new ResourceDictionary
            {
                ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
                ["InverseBoolToVisibilityConverter"] = new InverseBooleanToVisibilityConverter(),
                ["CycleDeviceListBoxItemStyle"] = new Style(typeof(ListBoxItem)) { Setters = { new Setter(FrameworkElement.HeightProperty, 24d), new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)) } },
            };
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative) });
            foreach (string name in MenuIcons) resources[$"AppMenu{name}Icon"] = new DrawingImage();
            app.Resources = resources;
            using var workspace = new TestSettingsWorkspace(nameof(MainWindowPresentationTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, settingsFeedbackTimeProvider: settingsFeedbackTimeProvider);
            var settings = new Settings { Theme = theme };
            settings.Miscellaneous.AutoSaveEnabled = autoSave;
            harness.SetCachedSettings(settings);
            if (autoSave) harness.SettingsService.SaveSettings(settings);
            var model = harness.ViewModel;
            TestPrivateAccess.SetField(model, "_mixersConnected", true);
            model.SelectedSettingsTabIndex = initialTab;
            var shell = TestPrivateAccess.GetField<AppShellService>(model, "_shell");
            var window = new MainWindow(new MainWindowDependencies(model, shell, new MainWindowVisibilityCoordinator(Logger.Instance), () => Task.CompletedTask));
            TestPrivateAccess.GetField<TestDoubles.TestAppMainWindowManager>(shell, "_windowManager").VisibilityWindow = window;
            try { await Layout(window, 414, 340); await action(window, model); }
            finally { window.AllowCloseForRuntimeShutdown(); window.Close(); app.Resources = previous; app.MainWindow = previousWindow; }
        });
    }

    private static async Task Layout(Window window, double width, double height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        root.UpdateLayout();
    }

    private static void AssertVisible(FrameworkElement element, FrameworkElement root)
    {
        Rect rect = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Assert.InRange(rect.Top, 0, root.ActualHeight); Assert.InRange(rect.Bottom, 0, root.ActualHeight + 1);
        Assert.InRange(rect.Right, 0, root.ActualWidth + 1);
    }

    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { double v = channel / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static void Capture(FrameworkElement root, string name, int width, int height)
    {
        if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        width = Math.Max(width, (int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right));
        height = Math.Max(height, (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)root.FindResource("WindowBackgroundBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using AudioPilot.Behaviors;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    private static readonly double[] SettingsLayoutWidths = [414d, 484d, 704d];
    private static readonly string[] BehaviorCheckboxOrder = [nameof(AppViewModel.SettingsAutoSaveEnabledDraft), nameof(AppViewModel.SettingsPlayAppSoundsDraft), nameof(AppViewModel.SettingsAutoScrollToMixerOnRestoreDraft), nameof(AppViewModel.SettingsSuppressDeviceStartupWarningsDraft)];

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public Task SettingsFormColumnsAndMaintenanceFitDuringLiveLanguageChanges(AppTheme theme) => WithWindow(theme, async (window, model) =>
    {
        var scope = (FrameworkElement)((TabControl)window.FindName("DeviceTabControl")).SelectedContent;
        foreach (Expander expander in WpfVisualTree.Descendants(scope).OfType<Expander>())
            ExpanderScrollBehavior.SetExpandedWithoutScrolling(expander, true);
        foreach (string language in LiveLanguageSequence)
            foreach (double width in SettingsLayoutWidths)
            {
                model.SettingsLanguageDraft = language;
                await Layout(window, width, 600);
                Control[] fields = [.. WpfVisualTree.Descendants(scope).OfType<Control>()
                    .Where(field => field is TextBox or ComboBox && field.Width == 190)];
                Assert.True(fields.Length >= 25);
                double column = fields[0].TransformToAncestor(scope).Transform(new Point()).X;
                foreach (Control field in fields)
                {
                    Assert.InRange(Math.Abs(field.TransformToAncestor(scope).Transform(new Point()).X - column), 0, 1);
                    Assert.InRange(field.ActualWidth, 189.5, 190.5);
                    Assert.True(field.TransformToAncestor(scope).Transform(new Point(field.ActualWidth, 0)).X <= scope.ActualWidth + 1);
                }
                Assert.All(WpfVisualTree.Descendants(scope).OfType<Expander>(), expander =>
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(expander))));
                var roles = WpfVisualTree.Descendants(scope).OfType<WrapPanel>()
                    .Where(panel => panel.Children.OfType<ToggleButton>().Count() == 3).ToArray();
                Assert.Equal(2, roles.Length);
                Assert.All(roles, panel => Assert.Equal(HorizontalAlignment.Left, panel.HorizontalAlignment));
                CheckBox[] behavior = [.. WpfVisualTree.Descendants(scope).OfType<CheckBox>().Where(check =>
                    BehaviorCheckboxOrder.Contains(BindingOperations.GetBinding(check, ToggleButton.IsCheckedProperty)?.Path.Path ?? string.Empty))];
                Assert.Equal(BehaviorCheckboxOrder, behavior.Select(check => BindingOperations.GetBinding(check, ToggleButton.IsCheckedProperty)!.Path.Path));
                Assert.Equal(0, Grid.GetRow(behavior[0]));
                Assert.Equal(0, Grid.GetRow(behavior[1]));
                Assert.Equal(1, Grid.GetRow(behavior[2]));
                Assert.Equal(1, Grid.GetRow(behavior[3]));
                Button[] maintenance = [.. WpfVisualTree.Descendants(scope).OfType<Button>().Where(button =>
                    ReferenceEquals(button.Command, model.OpenDiagnosticsCommand) || ReferenceEquals(button.Command, model.ResetWindowLayoutCommand) ||
                    ReferenceEquals(button.Command, model.ResetPerAppAudioRoutingCommand) || ReferenceEquals(button.Command, model.ResetToDefaultsCommand))];
                Assert.Equal(4, maintenance.Length);
                foreach (Button button in maintenance)
                {
                    var caption = new TextBlock { Text = Assert.IsType<string>(button.Content), FontFamily = button.FontFamily, FontSize = button.FontSize };
                    caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double available = button.ActualWidth - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right;
                    Assert.True(caption.DesiredSize.Width <= available + 1, $"Maintenance caption '{caption.Text}' needs {caption.DesiredSize.Width:F1}, available {available:F1}.");
                    var row = Assert.IsType<WrapPanel>(VisualTreeHelper.GetParent(button));
                    TextBlock description = Assert.Single(row.Children.OfType<TextBlock>());
                    Point start = description.TransformToAncestor(row).Transform(new Point());
                    Assert.True(start.X + description.ActualWidth <= row.ActualWidth + 1);
                    if (width == 414) Assert.True(start.Y >= button.ActualHeight);
                }
                WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
            }
    }, initialTab: 3);

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public Task SettingsShortcutSearchAndRegroupingPreserveDraftsExpansionAndScroll(AppTheme theme) => WithWindow(theme, async (window, model) =>
    {
        var tabs = (TabControl)window.FindName("DeviceTabControl");
        var scope = (FrameworkElement)tabs.SelectedContent;
        var media = WpfVisualTree.Descendants(scope).OfType<Expander>().Single(control => control.Name == "MediaShortcutsExpander");
        var viewer = (ScrollViewer)window.FindName("MainContentScrollViewer");
        var search = (TextBox)window.FindName("SettingsSearchBox");
        TabSaveState beforeExpansion = model.SettingsTabSaveState;
        ExpanderScrollBehavior.SetExpandedWithoutScrolling(media, true);
        ExpanderScrollBehavior.SetExpandedWithoutScrolling(media, false);
        Assert.Equal(beforeExpansion, model.SettingsTabSaveState);
        model.OutputCycleDevices.Add(new CycleDevice { Id = "pending-output", Name = "Pending output" });
        CycleDevice output = Assert.Single(model.OutputCycleDevices);
        model.SettingsPlayPauseHotkeyDraft = "Ctrl+Alt+P";
        object shortcut = model.SettingsPlayPauseHotkeyDraftCapture;
        await Layout(window, 484, 500);
        search.Text = model.Localization["Ui_PlayPause"].TrimEnd(':');
        await Layout(window, 484, 500);
        Assert.True(media.IsExpanded);
        search.Clear();
        await Layout(window, 484, 500);
        Assert.False(media.IsExpanded);
        ExpanderScrollBehavior.SetExpandedWithoutScrolling(media, true);
        var startup = WpfVisualTree.Descendants(scope).OfType<CheckBox>().Single(control => control.Name == "SettingsRunAtStartupCheckBox");
        startup.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Assert.True(model.SettingsRunAtStartupDraft);
        Assert.True(model.RunAtStartup);
        viewer.ScrollToBottom();
        await Layout(window, 484, 500);
        model.SettingsLanguageDraft = "es";
        await Layout(window, 484, 500);
        Assert.Same(scope, tabs.SelectedContent);
        Assert.Same(shortcut, model.SettingsPlayPauseHotkeyDraftCapture);
        Assert.Same(output, Assert.Single(model.OutputCycleDevices));
        Assert.Equal("Ctrl+Alt+P", model.SettingsPlayPauseHotkeyDraft.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.True(media.IsExpanded);
        Assert.True(startup.IsChecked);
        Assert.InRange(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight), 0, 1);
        Assert.Equal(TabSaveState.Unsaved, model.OutputTabSaveState);
        Assert.Equal(TabSaveState.Unsaved, model.SettingsTabSaveState);
        WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
    }, initialTab: 3);
}

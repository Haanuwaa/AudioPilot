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
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    private static readonly double[] SettingsLayoutWidths = [414d, 484d, 704d];
    private static readonly string[] BehaviorCheckboxOrder = [nameof(AppViewModel.SettingsAutoSaveEnabledDraft), nameof(AppViewModel.SettingsPlayAppSoundsDraft), nameof(AppViewModel.SettingsAutoScrollToMixerOnRestoreDraft)];

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
                    .Where(field => (field is TextBox or ComboBox or AudioPilot.Controls.HotkeyCaptureBox) && field.Width == 190)];
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
                var startupPanel = Assert.Single(WpfVisualTree.Descendants(scope).OfType<AudioPilot.Views.SettingsSystemPanel>());
                Assert.Single(WpfVisualTree.Descendants(startupPanel).OfType<CheckBox>(),
                    check => check.Name == "SuppressDeviceStartupWarningsCheckBox");
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

    [Fact]
    public Task SettingsDecimalInputsAndOptionalValidation_WorkAcrossLocalesAndDisabledStates() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        var tabs = (TabControl)window.FindName("DeviceTabControl");
        var scope = Assert.IsType<FrameworkElement>(tabs.SelectedContent, exactMatch: false);

        // Volume step fields attach NumericOnlyBehavior (allowing fractional 0.1..100 input, not IntegerOnlyBehavior).
        foreach (Expander expander in WpfVisualTree.Descendants(scope).OfType<Expander>())
        {
            ExpanderScrollBehavior.SetExpandedWithoutScrolling(expander, true);
        }
        await Layout(window, 484, 500);
        TextBox[] volumeStepBoxes =
        [
            .. WpfVisualTree.Descendants(scope).OfType<TextBox>().Where(static box =>
                BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path is
                    nameof(AppViewModel.SettingsMasterVolumeStepPercentDraft) or
                    nameof(AppViewModel.SettingsMicVolumeStepPercentDraft) or
                    nameof(AppViewModel.SettingsForegroundVolumeStepPercentDraft)),
        ];
        Assert.Equal(3, volumeStepBoxes.Length);
        foreach (TextBox box in volumeStepBoxes)
        {
            var behaviors = Interaction.GetBehaviors(box);
            Assert.Contains(behaviors, static behavior => behavior is NumericOnlyBehavior);
            Assert.DoesNotContain(behaviors, static behavior => behavior is IntegerOnlyBehavior);
        }

        // Overlay duration accepts both decimal comma and dot regardless of Windows locale.
        model.SettingsOverlayEnabledDraft = true;
        model.SettingsOverlayDurationSecondsDraft = "2,5";
        Assert.False(model.HasErrors);
        model.SettingsOverlayDurationSecondsDraft = "2.5";
        Assert.False(model.HasErrors);

        // Disabled overlay duration and unused foreground volume step do not block Apply settings.
        model.SettingsOverlayEnabledDraft = false;
        model.SettingsOverlayDurationSecondsDraft = string.Empty;
        model.SettingsForegroundVolumeUpHotkeyDraft = string.Empty;
        model.SettingsForegroundVolumeDownHotkeyDraft = string.Empty;
        model.SettingsForegroundVolumeStepPercentDraft = string.Empty;
        Assert.False(model.HasErrors);

        var apply = Assert.IsType<RelayCommand>(model.ApplySettingsCommand);
        apply.Execute(null);
        await apply.LastExecutionTaskForTests;
        Assert.Equal(TabSaveState.Saved, model.SettingsTabSaveState);

        // Clearing an optional disabled overlay duration or unset foreground volume step must not mark Settings dirty.
        model.SettingsOverlayDurationSecondsDraft = string.Empty;
        model.SettingsForegroundVolumeStepPercentDraft = string.Empty;
        Assert.False(TestPrivateAccess.InvokeNonPublic<bool>(model, "HasPendingLocalEditsForRefresh"));

        // Exporting with unsaved local edits warns before opening the file picker.
        model.SettingsPlayAppSoundsDraft = !model.SettingsPlayAppSoundsDraft;
        var dialogs = Assert.IsType<TestDoubles.RecordingAppDialogService>(TestPrivateAccess.GetField<IAppDialogService>(model, "_dialogs"));
        dialogs.YesNoResponse = AppDialogResult.Cancelled;
        bool exportDialogOpened = false;
        AppViewModel.ExportSettingsDialogForTests = _ => { exportDialogOpened = true; return (false, string.Empty); };
        try
        {
            var export = Assert.IsType<RelayCommand>(model.ExportSettingsCommand);
            export.Execute(null);
            await export.LastExecutionTaskForTests;
            Assert.False(exportDialogOpened);
            Assert.Contains(dialogs.Requests, static req => req.Message.Contains("unsaved changes", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            AppViewModel.ResetSettingsTransferDialogsForTests();
        }
    }, initialTab: 3);

    [Fact]
    public Task Pass2Polish_ReverseTabCycling_SpanishCompactLayout_RoleSearch_AndLocalizedChrome() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        // Ctrl+Shift+Tab cycles backward cleanly from Output (0) -> Settings (3) -> Routines (2) -> Input (1) -> Output (0).
        Assert.Equal(0, model.SelectedSettingsTabIndex);
        model.PreviousSettingsTabCommand.Execute(null);
        Assert.Equal(3, model.SelectedSettingsTabIndex);
        model.PreviousSettingsTabCommand.Execute(null);
        Assert.Equal(2, model.SelectedSettingsTabIndex);
        model.PreviousSettingsTabCommand.Execute(null);
        Assert.Equal(1, model.SelectedSettingsTabIndex);
        model.PreviousSettingsTabCommand.Execute(null);
        Assert.Equal(0, model.SelectedSettingsTabIndex);

        // Switch to Spanish and verify taskbar thumb descriptions, MixerGlobalMuteControls WrapPanel fit at compact width, All Devices translation without touching real device names, and routine clipboard text.
        model.Localization.SetLanguage("es");
        model.AvailableOutputDeviceNames.Clear();
        model.AvailableOutputDeviceNames.Add("All Devices");
        model.AvailableOutputDeviceNames.Add("Output");
        model.SelectedAvailableOutputIndex = 0;
        await Layout(window, 414, 440);
        Assert.Equal(model.Localization["Ui_PlayPause"].TrimEnd(':'), window.TaskbarItemInfo.ThumbButtonInfos[1].Description);
        var outputPanel = Assert.IsType<FrameworkElement>(window.FindName("OutputDevicePanel"), exactMatch: false);
        var availableCombo = Assert.IsType<ComboBox>(outputPanel.FindName("AvailableDevicesComboBox"));
        var renderedAll = Assert.IsType<TextBlock>(WpfVisualTree.Descendants(availableCombo).OfType<TextBlock>().First());
        Assert.Equal(model.Localization["Ui_AllDevices"], renderedAll.Text);
        model.SelectedAvailableOutputIndex = 1;
        await Layout(window, 414, 440);
        var renderedRealDevice = Assert.IsType<TextBlock>(WpfVisualTree.Descendants(availableCombo).OfType<TextBlock>().First());
        Assert.Equal("Output", renderedRealDevice.Text);
        var muteControls = Assert.IsType<WrapPanel>(window.FindName("MixerGlobalMuteControls"));
        Assert.InRange(muteControls.ActualWidth, 1, 414);
        var routine = new AudioRoutine { Name = "Juego", Enabled = true };
        string clipboard = AppViewModel.BuildRoutineClipboardText(routine, model.Localization);
        Assert.Contains("Rutina: Juego", clipboard, StringComparison.Ordinal);
        Assert.Contains("Estado:", clipboard, StringComparison.Ordinal);

        // Settings role pills are searchable via SettingsFindController, have distinct Output/Input automation names, and UpdaterPilotButton only shows "Opening..." when IsOpeningUpdater is true.
        model.SelectedSettingsTabIndex = 3;
        await Layout(window, 414, 440);
        var scope = Assert.IsType<FrameworkElement>(((TabControl)window.FindName("DeviceTabControl")).SelectedContent, exactMatch: false);
        var updaterButton = WpfVisualTree.Descendants(scope).OfType<Button>().Single(static button => button.Name == "UpdaterPilotButton");
        updaterButton.IsEnabled = false;
        Assert.Equal(model.Localization["Ui_OpenUpdater"], updaterButton.Content);
        updaterButton.ClearValue(UIElement.IsEnabledProperty);
        model.SetOpeningUpdater(true);
        Assert.False(updaterButton.IsEnabled);
        Assert.Equal(model.Localization["Ui_Opening"], updaterButton.Content);
        model.SetOpeningUpdater(false);
        var scroller = (ScrollViewer)window.FindName("MainContentScrollViewer");
        using var find = new SettingsFindController(scope, scroller);
        find.Search(model.Localization["Picker_MultimediaRole"], revealMatch: false);
        Assert.True(find.HasMatches);
        var rolePills = WpfVisualTree.Descendants(scope).OfType<ToggleButton>()
            .Where(static button => !string.IsNullOrEmpty(AutomationProperties.GetName(button))
                && AutomationProperties.GetName(button).Contains("Rol ", StringComparison.Ordinal))
            .Select(AutomationProperties.GetName)
            .ToArray();
        Assert.Equal(6, rolePills.Distinct(StringComparer.Ordinal).Count());
        WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
    }, initialTab: 0);
}

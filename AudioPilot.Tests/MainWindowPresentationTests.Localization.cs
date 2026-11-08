using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AudioPilot.Behaviors;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    private static readonly double[] DeviceWindowWidths = [Constants.AppConstants.WindowLayout.MinimumWidth, Constants.AppConstants.WindowLayout.DefaultWidth];
    private static readonly string[] LiveLanguageSequence = ["en", "es", "en"];

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public Task MicrophoneMonitorCaptionsFitDuringLiveLanguageChanges(AppTheme theme) => WithWindow(theme, async (window, model) =>
    {
        model.SelectedSettingsTabIndex = 1;
        model.ApplyAudioEndpointTestStateForTests(new(1, AudioEndpointTestKind.Input, AudioEndpointTestPhase.Completed,
            new("mic", "Desk microphone"), "Stopped"));
        TestWindowFactory.ShowWindowForLayoutTest(window);
        var panel = Assert.Single(WpfVisualTree.Descendants(window).OfType<Border>(), border => border.Name == "MicrophoneTestPanel");
        var captions = WpfVisualTree.Descendants(panel).OfType<Label>().ToArray();
        Assert.Equal(2, captions.Length);
        foreach (string language in LiveLanguageSequence)
            foreach (double width in DeviceWindowWidths)
            {
                window.Width = width;
                model.SettingsLanguageDraft = language;
                await SettleMixer(window);
                Assert.True(model.AudioTesting.IsInputTestPanelVisible);
                Assert.Equal([model.Localization["Ui_MonitorOutput"], model.Localization["Ui_MonitorVolume"]], captions.Select(label => label.Content));
                foreach (Label caption in captions)
                {
                    var text = new TextBlock { Text = (string)caption.Content, FontFamily = caption.FontFamily, FontSize = caption.FontSize, FontWeight = caption.FontWeight };
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double available = caption.ActualWidth - caption.Padding.Left - caption.Padding.Right;
                    Assert.True(text.DesiredSize.Width <= available + 0.5,
                        $"'{text.Text}' needs {text.DesiredSize.Width:F1} DIPs, but has {available:F1} at window width {width}.");
                }
            }
    });

    [Fact]
    public Task ThemeSelectorFitsEveryLocalizedChoice_AtDefaultAndNarrowWindowWidths() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        var selector = (ComboBox)window.FindName("ThemeSelectorComboBox");
        TestWindowFactory.ShowWindowForLayoutTest(window);
        foreach (string language in LiveLanguageSequence)
            for (int tab = 0; tab < 2; tab++)
                foreach (double width in DeviceWindowWidths)
                    foreach (AppTheme choice in new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark })
                    {
                        model.SettingsLanguageDraft = language;
                        model.SelectedSettingsTabIndex = tab;
                        window.Width = width;
                        selector.SelectedItem = choice;
                        await SettleMixer(window);
                        var presenter = (ContentPresenter)selector.Template.FindName("ContentSite", selector);
                        var label = Assert.Single(WpfVisualTree.Descendants(presenter).OfType<TextBlock>());
                        var natural = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight };
                        natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Assert.True(presenter.ActualWidth + 0.5 >= natural.DesiredSize.Width,
                            $"Theme '{label.Text}' needs {natural.DesiredSize.Width:F1} DIPs, but has {presenter.ActualWidth:F1} at window width {width}.");
                        if (width == Constants.AppConstants.WindowLayout.DefaultWidth)
                        {
                            var actions = (StackPanel)((Button)window.FindName("RefreshDevicesButton")).Parent;
                            var toolbar = (WrapPanel)actions.Parent;
                            double optionsY = toolbar.Children[1].TranslatePoint(new Point(), toolbar).Y;
                            Assert.InRange(Math.Abs(optionsY - actions.TranslatePoint(new Point(), toolbar).Y), 0, 0.5);
                        }
                        Assert.Equal(choice, model.Theme);
                    }
    });

    [Fact]
    public Task FailedSaveFeedbackAndFieldGuidanceSwitchLanguageWithoutReplacingInvalidDraft() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        model.SelectedSettingsTabIndex = 3;
        model.SettingsOverlayDurationSecondsDraft = "invalid";
        await model.ApplySettingsForTestsAsync();
        await Layout(window, 414, 440);
        var tab = (TabItem)((TabControl)window.FindName("DeviceTabControl")).Items[3];
        Assert.Equal(TabSaveState.NotSaved, model.SettingsTabSaveState);
        Assert.StartsWith("Changes weren't saved.", AutomationProperties.GetItemStatus(tab), StringComparison.Ordinal);
        model.SettingsLanguageDraft = "es";
        await Layout(window, 414, 440);
        Assert.StartsWith(model.Localization["Message_ChangesWerenTSaved"], AutomationProperties.GetItemStatus(tab), StringComparison.Ordinal);
        Assert.Equal("invalid", model.SettingsOverlayDurationSecondsDraft);
        Assert.Equal(model.Localization["Message_OverlayDurationRange"], model.Localization.Format(model.SettingsFieldGuidance[nameof(model.SettingsOverlayDurationSecondsDraft)]));
        Assert.Equal(model.Localization["Message_OverlayDurationRange"],
            Assert.Single(model.GetErrors(nameof(model.SettingsOverlayDurationSecondsDraft)).Cast<string>()));
    });

    [Fact]
    public Task SpanishFixedWidthSettingsLabelsFitAtCompactWidth() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        model.SettingsLanguageDraft = "es";
        model.SelectedSettingsTabIndex = 3;
        await Layout(window, 414, 440);
        var tabs = (TabControl)window.FindName("DeviceTabControl");
        var labels = WpfVisualTree.Descendants((FrameworkElement)tabs.SelectedContent).OfType<Label>()
            .Where(label => label.ActualWidth > 0 && label.Width == 150 && label.Content is string).ToArray();
        Assert.NotEmpty(labels);
        Assert.All(labels, label =>
        {
            var content = new TextBlock
            {
                Text = (string)label.Content,
                FontFamily = label.FontFamily,
                FontSize = label.FontSize,
                FontWeight = label.FontWeight,
            };
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double available = label.ActualWidth - label.Padding.Left - label.Padding.Right;
            Assert.True(content.DesiredSize.Width <= available + 1,
                $"{label.Content} needs {content.DesiredSize.Width:F1} DIPs, but has {available:F1} DIPs.");
        });
        var settings = (FrameworkElement)tabs.SelectedContent;
        var warnings = Assert.Single(WpfVisualTree.Descendants(settings).OfType<CheckBox>(),
            box => box.Name == "SuppressDeviceStartupWarningsCheckBox");
        var warningLabel = Assert.IsType<TextBlock>(warnings.Content);
        Assert.Equal("Ocultar avisos de dispositivos al iniciar", warningLabel.Text);
        Assert.InRange(warningLabel.ActualHeight, warningLabel.DesiredSize.Height - 1, warningLabel.DesiredSize.Height + 1);
        Assert.False(warnings.IsChecked);
        warnings.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        Assert.True(model.SettingsSuppressDeviceStartupWarningsDraft);
        model.SettingsLanguageDraft = "en";
        await Layout(window, 414, 440);
        Assert.Equal("Hide device startup warnings", warningLabel.Text);
        Assert.True(warnings.IsChecked);
    });

    [Fact]
    public Task MixerLanguageChangeRetainsTheRoutingChoiceAndGroup() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        var mixer = model.ActiveMixer;
        var music = new AudioSessionItem("Music player", 30, false, false, processId: 42);
        var game = new AudioSessionItem("Game", 60, false, false, processId: 43);
        await mixer.ApplyRefreshResultsAsync(mixer.GetActiveRefreshGeneration(), null, [("pid:42", music), ("pid:43", game)],
            null, TestContext.Current.CancellationToken);
        mixer.ToggleApplicationSelection(music);
        mixer.ToggleApplicationSelection(game);
        var details = new ApplicationRoutingDetails();
        music.RoutingDetails = details;
        var choice = new ApplicationRoutingDevice(null, "Windows default") { LabelResource = "Mixer_WindowsDefault" };
        details.SetDevices([choice], choice);
        details.IsBusy = false;
        int writes = 0;
        details.SelectionRequested += _ => writes++;
        await Layout(window, 446, 440);
        var list = (ListBox)window.FindName("VolumeMixer");
        var combo = Assert.Single(WpfVisualTree.Descendants(list).OfType<ComboBox>(), control => ReferenceEquals(control.DataContext, music));
        model.SettingsLanguageDraft = "es";
        await Layout(window, 446, 440);
        Assert.Equal("Mezclador de volumen", ((Label)window.FindName("VolumeMixerHeader")).Content);
        Assert.Contains(WpfVisualTree.Descendants(list).OfType<TextBlock>(), text => text.Text == "· Selección: 2");
        Assert.Same(choice, combo.SelectedItem);
        Assert.Same(details, music.RoutingDetails);
        Assert.Equal(2, mixer.SelectedApplicationCount);
        Assert.Equal(0, writes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LanguageChangesPreserveDraftObjectsAndSettingsScroll(bool atBottom) => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        model.OutputCycleDevices.Add(new CycleDevice { Id = "draft-output", Name = "Untranslated device" });
        model.Hotkey.LoadFromString("Ctrl+Alt+F24");
        model.SettingsPlayAppSoundsDraft = false;
        var device = model.OutputCycleDevices.Single();
        var shortcut = model.Hotkey;
        model.SelectedSettingsTabIndex = 3;
        await Layout(window, 446, 440);
        var tabs = (TabControl)window.FindName("DeviceTabControl");
        var viewer = (ScrollViewer)window.FindName("MainContentScrollViewer");
        var search = (TextBox)window.FindName("SettingsSearchBox");
        search.Text = "overlay";
        await Layout(window, 446, 440);
        viewer.ScrollToVerticalOffset(atBottom ? viewer.ScrollableHeight : viewer.ScrollableHeight / 2);
        await Layout(window, 446, 440);
        double offset = viewer.VerticalOffset;
        Assert.True(offset > 0);
        object content = tabs.SelectedContent;

        model.SettingsLanguageDraft = "es";
        await Layout(window, 446, 440);
        Assert.Equal("Ajustes", ((TabItem)tabs.Items[3]).Header);
        Assert.Equal("SettingsTab", AutomationProperties.GetAutomationId((TabItem)tabs.Items[3]));
        Assert.Same(content, tabs.SelectedContent);
        Assert.Same(device, model.OutputCycleDevices.Single());
        Assert.Same(shortcut, model.Hotkey);
        Assert.Equal("Ctrl+Alt+F24", model.Hotkey.ToHotkeyString().Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.False(model.SettingsPlayAppSoundsDraft);
        Assert.Equal(3, tabs.SelectedIndex);
        Assert.Equal("overlay", search.Text);
        Assert.InRange(Math.Abs(viewer.VerticalOffset - (atBottom ? viewer.ScrollableHeight : offset)), 0, 1);
        var selector = WpfVisualTree.Descendants((FrameworkElement)tabs.SelectedContent).OfType<ComboBox>().Single(box => box.Name == "LanguageSelector");
        Assert.Equal("es", selector.SelectedValue);
        Assert.StartsWith("Cambia el texto al instante.", Interaction.GetBehaviors(selector).OfType<HoverInfoPopupBehavior>().Single().Text, StringComparison.Ordinal);
        Assert.Equal(TabSaveState.Unsaved, model.OutputTabSaveState);
        Assert.Equal(TabSaveState.Unsaved, model.SettingsTabSaveState);
        Assert.Contains("Pulsa «Guardar ajustes»", AutomationProperties.GetItemStatus((TabItem)tabs.Items[0]), StringComparison.Ordinal);
        WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
        model.SettingsLanguageDraft = "en";
        await Layout(window, 446, 440);
        Assert.Same(content, tabs.SelectedContent);
        Assert.False(model.SettingsPlayAppSoundsDraft);
    });
}

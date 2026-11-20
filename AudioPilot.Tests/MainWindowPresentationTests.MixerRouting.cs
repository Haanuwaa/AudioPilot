using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioPilot.Behaviors;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    private static readonly int[] MixerNavigationTabs = [2, 3, 1];

    [Fact]
    public Task NonDeviceTabsKeepTheHiddenMixerBoundToItsPreviousDirection() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        model.SelectedSettingsTabIndex = 1;
        await Layout(window, 414, 340);
        var list = (ListBox)window.FindName("VolumeMixer");
        var input = model.InputMixer;
        Assert.Same(input.GroupedSessions, list.ItemsSource);

        foreach (int tab in MixerNavigationTabs)
        {
            model.SelectedSettingsTabIndex = tab;
            await Layout(window, 414, 340);
            Assert.Same(input, model.ActiveMixer);
            Assert.Same(input.GroupedSessions, list.ItemsSource);
            Assert.Equal("Recording Mixer", model.ActiveMixerHeader);
        }

        model.SelectedSettingsTabIndex = 0;
        await Layout(window, 414, 340);
        Assert.Same(model.Mixer, model.ActiveMixer);
        Assert.Same(model.Mixer.GroupedSessions, list.ItemsSource);
        Assert.Equal("Volume Mixer", model.ActiveMixerHeader);
    });

    [Fact]
    public Task MixerEscapeClosesASelectorBeforeClearingTheGroup() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        var mixer = model.ActiveMixer;
        var first = new AudioSessionItem("First", 30, false, false, processId: 42);
        var second = new AudioSessionItem("Second", 60, false, false, processId: 43);
        await mixer.ApplyRefreshResultsAsync(mixer.GetActiveRefreshGeneration(), null, [("pid:42", first), ("pid:43", second)], null, TestContext.Current.CancellationToken);
        mixer.ToggleApplicationSelection(first); mixer.ToggleApplicationSelection(second);
        var root = (Grid)window.Content;
        var selector = new PopupFreeMixerSelector { Template = new ControlTemplate(typeof(ComboBox)) };
        root.Children.Add(selector);
        try
        {
            selector.IsDropDownOpen = true;
            Assert.True(selector.IsDropDownOpen);
            Assert.True(Escape(selector).Handled);
            Assert.False(selector.IsDropDownOpen);
            Assert.Equal(2, mixer.SelectedApplicationCount);
            Assert.True(Escape(root).Handled);
            Assert.Equal(0, mixer.SelectedApplicationCount);
            Assert.Equal(WindowState.Normal, window.WindowState);
        }
        finally { selector.IsDropDownOpen = false; root.Children.Remove(selector); }

        static KeyEventArgs Escape(UIElement target)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, new MixerKeyboardSource(), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(key);
            key.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(key);
            return key;
        }
    });

    /// <summary>Exercises dropdown keyboard handling without loading or displaying a native popup.</summary>
    private sealed class PopupFreeMixerSelector : ComboBox
    {
        static PopupFreeMixerSelector() => IsDropDownOpenProperty.OverrideMetadata(typeof(PopupFreeMixerSelector),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.None, null, static (_, value) => value));
    }

    private sealed class MixerKeyboardSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    [Fact]
    public Task MixerGroupGestureSurvivesFocusTransfers_AndOnlyItsOwningRowCanEndIt() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        var mixer = model.ActiveMixer;
        var first = new AudioSessionItem("Music", 30, false, false, processId: 42);
        var second = new AudioSessionItem("Game", 60, false, false, processId: 43);
        await mixer.ApplyRefreshResultsAsync(mixer.GetActiveRefreshGeneration(), null, [("pid:42", first), ("pid:43", second)], null, TestContext.Current.CancellationToken);
        mixer.DetachOwnedVolumeChangedHandler("pid:42", first); mixer.DetachOwnedVolumeChangedHandler("pid:43", second);
        first.VolumeChanged += mixer.PropagateGroupVolume; second.VolumeChanged += mixer.PropagateGroupVolume;
        mixer.ToggleApplicationSelectionModeCommand.Execute(null);
        mixer.ApplicationCommand.Execute(first); mixer.ApplicationCommand.Execute(second);
        await Layout(window, 414, 500);
        var list = (ListBox)window.FindName("VolumeMixer");
        var sliders = WpfVisualTree.Descendants(list).OfType<Slider>().ToArray();
        var one = Assert.Single(sliders, control => ReferenceEquals(control.DataContext, first));
        var two = Assert.Single(sliders, control => ReferenceEquals(control.DataContext, second));
        var name = Assert.Single(WpfVisualTree.Descendants(list).OfType<Button>(), control => AutomationProperties.GetName(control) == "Music");
        var selection = Assert.Single(WpfVisualTree.Descendants(list).OfType<CheckBox>(), control => ReferenceEquals(control.DataContext, first));
        Assert.Equal(Visibility.Visible, selection.Visibility);
        Assert.True(selection.IsChecked);
        var section = Assert.Single(WpfVisualTree.Descendants(list).OfType<Border>(), control => control.Name == "MixerApplicationSection" && control.Visibility == Visibility.Visible);
        Assert.Equal("Done", Assert.Single(WpfVisualTree.Descendants(section).OfType<Button>(), control => control.Name == "MixerSelectAppsButton").Content);
        static MixerGroupSliderBehavior Gesture(Slider slider)
        {
            for (DependencyObject? current = slider; current != null; current = VisualTreeHelper.GetParent(current))
                if (Interaction.GetBehaviors(current).OfType<MixerGroupSliderBehavior>().FirstOrDefault() is { } behavior) return behavior;
            throw new InvalidOperationException("The mixer row has no gesture behavior.");
        }
        Assert.Same(mixer, Gesture(one).Mixer);
        Assert.Same(mixer, Gesture(two).Mixer);
        static void Begin(Slider slider) => slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Begin(one);
        name.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, name, one) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        one.Value = 40;
        one.GetBindingExpression(Slider.ValueProperty)!.UpdateSource();
        Assert.Equal(70, second.Volume);
        Begin(two);
        name.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, name, two) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        one.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        two.Value = 80;
        two.GetBindingExpression(Slider.ValueProperty)!.UpdateSource();
        Assert.Equal(50, first.Volume);
        two.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        two.Value = 90;
        two.GetBindingExpression(Slider.ValueProperty)!.UpdateSource();
        Assert.Equal(50, first.Volume);
        first.VolumeChanged -= mixer.PropagateGroupVolume; second.VolumeChanged -= mixer.PropagateGroupVolume;
    });

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task InlineMixerRoutingRetainsSelectionWhenChoicesRefresh_AndFitsBothThemes(AppTheme theme)
    {
        await WithWindow(theme, async (window, model) =>
        {
            var mixer = model.ActiveMixer;
            var music = new AudioSessionItem("Music player", 30, false, false, processId: 42);
            var game = new AudioSessionItem("Game", 60, false, false, processId: 43);
            var sounds = new AudioSessionItem("System Sounds", 55, false, false, isSystemSounds: true);
            await mixer.ApplyRefreshResultsAsync(mixer.GetActiveRefreshGeneration(), null, [("pid:42", music), ("pid:43", game), ("system", sounds)], null, TestContext.Current.CancellationToken);
            await Layout(window, 414, 400);
            var list = (ListBox)window.FindName("VolumeMixer");
            var name = Assert.Single(WpfVisualTree.Descendants(list).OfType<Button>(), control => AutomationProperties.GetName(control) == music.DisplayName);
            var peer = UIElementAutomationPeer.CreatePeerForElement(name)!;
            Assert.Equal("Not selected for group volume and mute", peer.GetItemStatus());
            foreach (var item in new[] { music, game, sounds })
            {
                var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(item));
                Assert.Equal(item.DisplayName, UIElementAutomationPeer.CreatePeerForElement(row)!.GetName());
            }
            mixer.ToggleApplicationSelection(music); mixer.ToggleApplicationSelection(game);
            var details = new ApplicationRoutingDetails();
            music.RoutingDetails = details;
            int writes = 0;
            details.SelectionRequested += _ => writes++;
            await Layout(window, 414, 400);
            var combo = Assert.Single(WpfVisualTree.Descendants(list).OfType<ComboBox>(), control => ReferenceEquals(control.DataContext, music));
            Assert.DoesNotContain(WpfVisualTree.Descendants(list).OfType<Button>(), control => AutomationProperties.GetName(control) == "Refresh application devices");
            var retry = Assert.Single(WpfVisualTree.Descendants(list).OfType<Button>(), control => ReferenceEquals(control.DataContext, music) && AutomationProperties.GetName(control) == "Retry reading application routing");
            Assert.Equal(Visibility.Collapsed, retry.Visibility);
            var initial = new ApplicationRoutingDevice(null, "Windows default");
            details.SetDevices([initial, new("speakers", "Desk speakers")], initial);
            details.IsBusy = false;
            await Layout(window, 414, 400);
            Assert.Same(initial, combo.SelectedItem);
            var replacement = new ApplicationRoutingDevice("headset", "USB headset");
            details.IsBusy = true;
            details.SetDevices([new(null, "Windows default"), replacement], replacement);
            details.Status = "Device preference saved. Some apps need to restart audio.";
            details.IsBusy = false;
            await Layout(window, 414, 400);
            Assert.Same(replacement, combo.SelectedItem);
            Assert.Equal(0, writes);
            Assert.True(combo.ActualHeight >= 28);
            Assert.True(combo.ActualWidth >= 60);
            Assert.Equal(2, mixer.SelectedApplicationCount);
            Assert.Contains(WpfVisualTree.Descendants(list).OfType<TextBlock>(), text => text.Text == "· 2 selected");
            var section = Assert.Single(WpfVisualTree.Descendants(list).OfType<Border>(), control => control.Name == "MixerApplicationSection" && control.Visibility == Visibility.Visible);
            var header = Assert.Single(WpfVisualTree.Descendants(section).OfType<Grid>(), control => control.Name == "MixerApplicationsHeader");
            var select = Assert.Single(WpfVisualTree.Descendants(header).OfType<Button>(), control => control.Name == "MixerSelectAppsButton");
            Assert.Equal("Select apps", select.Content);
            var soundLabel = Assert.Single(WpfVisualTree.Descendants(list).OfType<Label>(), control => ReferenceEquals(control.DataContext, sounds));
            Assert.True(soundLabel.TransformToAncestor(list).Transform(new Point(0, soundLabel.ActualHeight)).Y <= header.TransformToAncestor(list).Transform(new Point()).Y);
            var scroll = (ScrollViewer)window.FindName("MainContentScrollViewer");
            scroll.ScrollToEnd();
            await Layout(window, 414, 400);
            Capture((FrameworkElement)window.Content, $"mixer-inline-group-{theme}", 414, 400);
            mixer.ToggleApplicationSelectionModeCommand.Execute(null);
            await Layout(window, 414, 400);
            scroll.ScrollToEnd();
            await Layout(window, 414, 400);
            Assert.Equal("Done", select.Content);
            AssertVisible(header, (FrameworkElement)window.Content);
            Rect headerBounds = header.TransformToAncestor(section).TransformBounds(new Rect(header.RenderSize));
            Assert.InRange(Math.Abs(headerBounds.Left + headerBounds.Width / 2 - section.ActualWidth / 2), 0, 1);
            var selection = Assert.Single(WpfVisualTree.Descendants(list).OfType<CheckBox>(), control => ReferenceEquals(control.DataContext, music));
            var glyph = (Border)selection.Template.FindName("checkBoxBorder", selection);
            var focus = new Control { Style = selection.FocusVisualStyle };
            focus.Measure(selection.RenderSize); focus.Arrange(new Rect(selection.RenderSize)); focus.UpdateLayout();
            var outline = (System.Windows.Shapes.Rectangle)focus.Template.FindName("SelectionFocusOutline", focus);
            Rect glyphBounds = glyph.TransformToAncestor(selection).TransformBounds(new Rect(glyph.RenderSize));
            Rect focusBounds = outline.TransformToAncestor(focus).TransformBounds(new Rect(outline.RenderSize));
            Assert.InRange(Math.Abs(glyphBounds.Left + glyphBounds.Width / 2 - focusBounds.Left - focusBounds.Width / 2), 0, .5);
            Assert.InRange(Math.Abs(glyphBounds.Top + glyphBounds.Height / 2 - focusBounds.Top - focusBounds.Height / 2), 0, .5);
            Assert.Equal(glyphBounds.Width + 4, focusBounds.Width);
            Assert.Equal(glyphBounds.Height + 4, focusBounds.Height);
            Capture((FrameworkElement)window.Content, $"mixer-inline-selecting-{theme}", 414, 400);
            details.HasError = true; details.NeedsReadRetry = true; details.Status = "Could not read this app's device preference. Retry or use Refresh.";
            await Layout(window, 414, 400);
            scroll.ScrollToEnd();
            await Layout(window, 414, 400);
            Assert.Equal(Visibility.Visible, retry.Visibility);
            Assert.True(retry.IsEnabled);
            Capture((FrameworkElement)window.Content, $"mixer-inline-retry-{theme}", 414, 400);
            details.HasError = false; details.NeedsReadRetry = false;
            details.Status = "Device preference saved. Some apps need to restart audio.";
            mixer.ToggleApplicationSelectionModeCommand.Execute(null);
            await Layout(window, 900, 400);
            scroll.ScrollToEnd();
            await Layout(window, 900, 400);
            Capture((FrameworkElement)window.Content, $"mixer-inline-group-wide-{theme}", 900, 400);
            Assert.True(name.ActualWidth > 160);
            Assert.Equal(Visibility.Collapsed, selection.Visibility);
            Assert.Equal("Selected for group volume and mute", peer.GetItemStatus());
            var selectedRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(music));
            var rowPeer = UIElementAutomationPeer.CreatePeerForElement(selectedRow)!;
            Assert.Equal("Selected for group volume and mute", rowPeer.GetItemStatus());
            var soundsRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(sounds));
            Assert.Empty(UIElementAutomationPeer.CreatePeerForElement(soundsRow)!.GetItemStatus());
            mixer.ClearApplicationSelection();
            Assert.False(music.IsGroupSelected);
            Assert.False(game.IsGroupSelected);
            await Layout(window, 900, 400);
            Assert.Equal("Not selected for group volume and mute", peer.GetItemStatus());
            Assert.Empty(rowPeer.GetItemStatus());
            WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
            await mixer.ApplyRefreshResultsAsync(mixer.GetActiveRefreshGeneration(), ["pid:42"], null, null, TestContext.Current.CancellationToken);
            await Layout(window, 414, 400);
            Assert.Single(WpfVisualTree.Descendants(list).OfType<Border>(), control => control.Name == "MixerApplicationSection" && control.Visibility == Visibility.Visible);
            await mixer.ApplyRefreshResultsAsync(mixer.GetActiveRefreshGeneration(), ["pid:43"], null, null, TestContext.Current.CancellationToken);
            await Layout(window, 414, 400);
            Assert.DoesNotContain(WpfVisualTree.Descendants(list).OfType<Border>(), control => control.Name == "MixerApplicationSection" && control.Visibility == Visibility.Visible);
        });
    }
}

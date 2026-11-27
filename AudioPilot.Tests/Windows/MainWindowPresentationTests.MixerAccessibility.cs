using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using AudioPilot.Controls;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    [Fact]
    public Task MixerHeaderActionsAreAccessible_WithoutRealizingMoreRows() => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        var rows = await AddViewportRows(model.Mixer, 128);
        var master = new AudioSessionItem("Master Volume", 50, true, false, endpointId: "speaker", endpointName: "Desk speakers",
            endpointRole: NAudio.CoreAudioApi.Role.Multimedia);
        var microphone = new AudioSessionItem("Microphone Volume", 60, false, true, endpointId: "microphone", endpointName: "Desk microphone",
            endpointRole: NAudio.CoreAudioApi.Role.Communications);
        await model.Mixer.ApplyRefreshResultsAsync(model.Mixer.GetActiveRefreshGeneration(), null,
            [("master", master), ("mic", microphone)], null, TestContext.Current.CancellationToken);
        window.Height = 500;
        TestWindowFactory.ShowWindowForLayoutTest(window);
        await SettleMixer(window);
        var list = (MixerListBox)window.FindName("VolumeMixer");
        int realized = WpfVisualTree.Descendants(list).OfType<ListBoxItem>().Count();
        Assert.InRange(realized, 1, 64);
        var listPeer = UIElementAutomationPeer.CreatePeerForElement(list);
        var header = Assert.Single(listPeer.GetChildren().OfType<FrameworkElementAutomationPeer>(), peer => peer.Owner is MixerGroupHeader);
        Assert.Equal(AutomationControlType.Group, header.GetAutomationControlType());
        Invoke("Select apps");
        await SettleMixer(window);
        Assert.True(model.Mixer.IsSelectingApplications);
        model.Mixer.ToggleApplicationSelection(rows[0]);
        Assert.True(model.Mixer.SelectedApplicationCount > 0);
        Invoke("Clear");
        await SettleMixer(window);
        Assert.Equal(0, model.Mixer.SelectedApplicationCount);
        Invoke("Select apps");
        await SettleMixer(window);
        Invoke("Done");
        await SettleMixer(window);
        Assert.False(model.Mixer.IsSelectingApplications);
        Assert.Equal(realized, WpfVisualTree.Descendants(list).OfType<ListBoxItem>().Count());
        Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
        Assert.Contains(WpfVisualTree.Descendants(list).OfType<TextBlock>(), text => text.Text == "Desk speakers");
        ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(master)).BringIntoView();
        await SettleMixer(window);
        Capture((FrameworkElement)window.Content, "mixer-endpoints-English", 440, 500);
        model.SettingsLanguageDraft = "es";
        await SettleMixer(window);
        Assert.Contains(WpfVisualTree.Descendants(list).OfType<TextBlock>(), text => text.Text == "Desk speakers");
        Assert.Equal(128, model.Mixer.Sessions.Count(item => item.IsApplication));
        Capture((FrameworkElement)window.Content, "mixer-endpoints-Spanish", 440, 500);

        void Invoke(string name)
        {
            var action = Assert.Single(header.GetChildren(), peer => peer.GetName() == name);
            Assert.IsType<IInvokeProvider>(action.GetPattern(PatternInterface.Invoke), exactMatch: false).Invoke();
        }
    });
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.ViewModels;

namespace AudioPilot.Views;

public partial class InputDevicePanel : UserControl
{
    private AudioTestingViewModel? _audioTesting;

    public InputDevicePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => AttachAudioTesting();
        Unloaded += (_, _) => DetachAudioTesting();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => AttachAudioTesting();

    private void AttachAudioTesting()
    {
        AudioTestingViewModel? next = (DataContext as AppViewModel)?.AudioTesting;
        if (ReferenceEquals(_audioTesting, next)) return;
        DetachAudioTesting();
        _audioTesting = next;
        if (_audioTesting != null)
        {
            PropertyChangedEventManager.AddHandler(_audioTesting, OnAudioTestingStatusChanged, nameof(AudioTestingViewModel.Status));
        }
    }

    private void DetachAudioTesting()
    {
        if (_audioTesting == null) return;
        PropertyChangedEventManager.RemoveHandler(_audioTesting, OnAudioTestingStatusChanged, nameof(AudioTestingViewModel.Status));
        _audioTesting = null;
    }

    private void OnAudioTestingStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!MicrophoneTestStatusText.IsVisible || string.IsNullOrWhiteSpace(MicrophoneTestStatusText.Text)) return;
            UIElementAutomationPeer? peer = (UIElementAutomationPeer?)(UIElementAutomationPeer.FromElement(MicrophoneTestStatusText)
                ?? UIElementAutomationPeer.CreatePeerForElement(MicrophoneTestStatusText));
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Loaded);
    }

    private void SwitchOrderListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        ListManagerWindow.OpenFromDeviceRow(SwitchOrderListBox, e, ListManagerMode.Input);

    private void MicrophoneTestPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (!IsVisible) return;
                if (MicrophoneTestPanel.IsVisible)
                {
                    StopMicrophoneTestButton.Focus();
                }
                else
                {
                    SwitchOrderListBox.Focus();
                }
            },
            DispatcherPriority.Input);
    }
}

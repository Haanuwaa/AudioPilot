using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AudioPilot.Views;

public partial class InputDevicePanel : UserControl
{
    public InputDevicePanel() => InitializeComponent();

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

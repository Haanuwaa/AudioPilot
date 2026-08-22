using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AudioPilot.Views;

public partial class InputDevicePanel : UserControl
{
    public InputDevicePanel() => InitializeComponent();

    internal void UnselectAll() => SwitchOrderListBox.UnselectAll();

    private void MicrophoneTestPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () =>
            {
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

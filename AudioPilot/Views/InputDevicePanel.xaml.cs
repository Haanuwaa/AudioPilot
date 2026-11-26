using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.ViewModels;

namespace AudioPilot.Views;

public partial class InputDevicePanel : UserControl
{
    public InputDevicePanel() => InitializeComponent();

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

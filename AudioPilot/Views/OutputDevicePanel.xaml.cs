using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.ViewModels;

namespace AudioPilot.Views;

public partial class OutputDevicePanel : UserControl
{
    public OutputDevicePanel() => InitializeComponent();

    private void SwitchOrderListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        ListManagerWindow.OpenFromDeviceRow(SwitchOrderListBox, e, ListManagerMode.Output);
}

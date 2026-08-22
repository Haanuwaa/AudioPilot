using System.Windows.Controls;

namespace AudioPilot.Views;

public partial class OutputDevicePanel : UserControl
{
    public OutputDevicePanel() => InitializeComponent();

    internal void UnselectAll() => SwitchOrderListBox.UnselectAll();
}

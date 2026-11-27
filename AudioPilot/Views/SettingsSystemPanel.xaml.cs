using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace AudioPilot.Views;

public partial class SettingsSystemPanel : UserControl
{
    public SettingsSystemPanel() => InitializeComponent();

    private void OpenUpdaterPilot_Click(object sender, RoutedEventArgs args)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.OpenUpdaterPilotFromPanel(sender, args);
        }
    }

    private void ProjectLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.HandleProjectLinkRequestNavigate(sender, e);
        }
    }

    private void ProjectLink_PreviewKeyDown(object sender, KeyEventArgs e) =>
        MainWindow.HandleProjectLinkPreviewKeyDown(sender, e);
}

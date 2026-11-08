using System.Windows;

namespace AudioPilot;

public partial class MainWindow
{
    private async void OpenUpdaterPilot_Click(object sender, RoutedEventArgs args)
    {
        if (Application.Current is App app) await app.OpenUpdaterPilotAsync(this);
    }
}

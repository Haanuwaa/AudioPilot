using System.Windows;
using System.Windows.Controls;

namespace AudioPilot;

public partial class MainWindow
{
    private async void OpenUpdaterPilot_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || !button.IsEnabled || Application.Current is not App app) return;
        double minimumWidth = button.MinWidth;
        button.SetCurrentValue(MinWidthProperty, Math.Max(minimumWidth, button.ActualWidth));
        button.SetCurrentValue(IsEnabledProperty, false);
        try { await app.OpenUpdaterPilotAsync(this); }
        finally
        {
            button.SetCurrentValue(IsEnabledProperty, true);
            button.SetCurrentValue(MinWidthProperty, minimumWidth);
        }
    }
}

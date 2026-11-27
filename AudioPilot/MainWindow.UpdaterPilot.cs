using System.Windows;
using System.Windows.Controls;

namespace AudioPilot;

public partial class MainWindow
{
    internal void OpenUpdaterPilotFromPanel(object sender, RoutedEventArgs args) =>
        OpenUpdaterPilot_Click(sender, args);

    private async void OpenUpdaterPilot_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || !button.IsEnabled || Application.Current is not App app) return;
        double minimumWidth = button.MinWidth;
        button.SetCurrentValue(MinWidthProperty, Math.Max(minimumWidth, button.ActualWidth));
        button.SetCurrentValue(IsEnabledProperty, false);
        try { await app.OpenUpdaterPilotAsync(this); }
        catch (Exception ex)
        {
            Logging.Logger.Instance.Warning("MainWindow", "updater-launch-click-failed", nameof(OpenUpdaterPilot_Click), ex);
        }
        finally
        {
            button.SetCurrentValue(IsEnabledProperty, true);
            button.SetCurrentValue(MinWidthProperty, minimumWidth);
        }
    }
}

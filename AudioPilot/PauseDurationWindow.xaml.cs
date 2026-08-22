using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Helpers;

namespace AudioPilot;

/// <summary>Collects a bounded, session-only pause duration without changing automation until confirmed.</summary>
public partial class PauseDurationWindow : Window
{
    internal TimeSpan? SelectedDuration { get; private set; }

    internal PauseDurationWindow()
    {
        InitializeComponent();
        Minutes.Text = "60";
        Minutes.SelectAll();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
    }

    private void Minutes_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool valid = TryGetDuration(out _);
        PauseButton.IsEnabled = valid;
        DurationHelp.Text = valid ? "From 1 minute to 24 hours. Applies to this app session only." : "Enter a whole number from 1 to 1440 minutes.";
        DurationHelp.SetResourceReference(TextBlock.ForegroundProperty, valid ? "MutedTextBrush" : "DialogWarningBrush");
    }

    private bool TryGetDuration(out TimeSpan duration)
    {
        duration = default;
        if (!int.TryParse(Minutes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes is < 1 or > Constants.AppConstants.Routines.AutomaticPauseMaxMinutes) return false;
        duration = TimeSpan.FromMinutes(minutes);
        return true;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetDuration(out TimeSpan duration)) return;
        SelectedDuration = duration;
        DialogResult = true;
    }
}

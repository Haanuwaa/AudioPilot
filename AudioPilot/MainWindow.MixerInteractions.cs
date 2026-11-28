using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Models;

namespace AudioPilot;

public partial class MainWindow
{
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (IsVisible && WindowState != WindowState.Minimized) _appVm.RefreshOpenMixerRoutingOnActivation();
    }

    private void MixerRoutingExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AudioSessionItem item } button
            || item.IsRoutingExpanded || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_allowCloseForRuntimeShutdown && button.IsLoaded && button.IsVisible
                && ReferenceEquals(button.DataContext, item) && item.IsRoutingExpanded)
                (ItemsControl.ContainerFromElement(VolumeMixer, button) as FrameworkElement)?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void MixerApplicationName_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || !_appVm.ActiveMixer.CanGroupApplications || sender is not FrameworkElement { DataContext: AudioSessionItem item }) return;
        _appVm.ActiveMixer.ToggleApplicationSelection(item);
        e.Handled = true;
    }

    private void MixerApplicationName_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || !_appVm.ActiveMixer.CanGroupApplications || sender is not FrameworkElement { DataContext: AudioSessionItem item }) return;
        if (!e.IsRepeat) _appVm.ActiveMixer.ToggleApplicationSelection(item);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;

        if (_appVm.AudioTesting.StopCommand.CanExecute(null))
        {
            _appVm.AudioTesting.StopCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_appVm.ClearMixerSelectionIfAny())
        {
            e.Handled = true;
            return;
        }

        if (_appVm.MinimizeCommand.CanExecute(null))
        {
            _appVm.MinimizeCommand.Execute(null);
            e.Handled = true;
        }
    }
}

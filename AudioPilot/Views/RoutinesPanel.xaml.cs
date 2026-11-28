using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Views;

public partial class RoutinesPanel : UserControl
{
    public RoutinesPanel() => InitializeComponent();

    private void SavedRoutinesListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (MainWindow.TryGetRoutineListItemFromEventSource(e.OriginalSource, out ListBoxItem? item))
        {
            MainWindow.SelectRoutineListItem(item);
        }
    }

    private void SavedRoutinesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || !MainWindow.TryGetRoutineListItemFromEventSource(e.OriginalSource, out ListBoxItem? item)
            || item?.DataContext is not AudioRoutine routine
            || sender is not ListBox { SelectedItems.Count: 1 }
            || DataContext is not AppViewModel viewModel
            || !viewModel.CanEditManagedList)
        {
            return;
        }

        viewModel.EditRoutine(routine, Window.GetWindow(this));
        e.Handled = true;
    }
}

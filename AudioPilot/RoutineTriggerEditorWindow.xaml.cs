using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot;

/// <summary>Edits one trigger draft; the owning routine editor retains the settings and discovery lifetime.</summary>
public partial class RoutineTriggerEditorWindow : Window
{
    private readonly RoutineEditorViewModel _viewModel;
    private readonly Action<Window, Button, CancellationToken> _browseApplication;
    private readonly Func<Window, Button, CancellationToken, Task> _pickApplication;
    private readonly CancellationTokenSource _lifetime = new();
    internal bool IsClosed { get; private set; }

    internal RoutineTriggerEditorWindow(RoutineEditorViewModel viewModel,
        Action<Window, Button, CancellationToken> browseApplication,
        Func<Window, Button, CancellationToken, Task> pickApplication)
    {
        _viewModel = viewModel;
        _browseApplication = browseApplication;
        _pickApplication = pickApplication;
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        _lifetime.Cancel();
        _viewModel.CancelTriggerEdit();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void ConfirmTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (!IsClosed && _viewModel.ConfirmTriggerEdit()) DialogResult = true;
    }

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (!IsClosed && sender is Button button) _browseApplication(this, button, _lifetime.Token);
    }

    private async void PickPackagedApp_Click(object sender, RoutedEventArgs e)
    {
        if (IsClosed || sender is not Button button) return;
        CancellationToken cancellationToken = _lifetime.Token;
        try { await _pickApplication(this, button, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void NetworkPicker_DropDownOpened(object sender, EventArgs e)
    {
        if (!IsClosed && !_viewModel.IsScanningNetworks) _ = _viewModel.RefreshNetworksAsync();
    }

    private void OpenHelp_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _ = AboutDocumentLauncher.TryOpen(Logger.Instance, "RoutineTriggerEditorWindow");
        e.Handled = true;
    }
}

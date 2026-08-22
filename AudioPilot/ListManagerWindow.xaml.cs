using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;
using NAudio.CoreAudioApi;

namespace AudioPilot;

internal sealed record ListManagerDetail(string Label, string Text);

public partial class ListManagerWindow : Window
{
    internal static void OpenFromDeviceRow(ListBox list, MouseButtonEventArgs e, ListManagerMode mode)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None
            || e.OriginalSource is not DependencyObject origin || ListBoxReorderBehavior.IsInteractive(origin)
            || ItemsControl.ContainerFromElement(list, origin) is not ListBoxItem { DataContext: CycleDevice device }
            || list.DataContext is not AppViewModel app) return;

        e.Handled = true;
        app.ShowListManager(mode, device.Id);
    }

    private readonly AppViewModel _app;
    private readonly AudioDeviceService _audio;
    private readonly ListManagerMode _mode;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly INotifyCollectionChanged _source;
    private readonly Func<CancellationToken, Task<ListManagerDeviceInventory>> _readDevices;
    private readonly HashSet<AudioRoutine> _observedRoutines = [];
    private readonly RelayCommand _reorder;
    private readonly ICommand _sourceReorder;
    private ListManagerDeviceInventory? _inventory;
    private string? _initialDeviceId;
    private DispatcherOperation? _pendingProjection;
    private ListManagerRow[] _displayed = [];
    private ListManagerDetail[] _details = [];
    private int _refreshRequested;
    private bool _reading;
    private bool _projecting;
    private volatile bool _closed;
    private bool IsRoutineList => _mode == ListManagerMode.Routines;
    private bool IsOutput => _mode == ListManagerMode.Output;
    private IReadOnlyList<CycleDevice> Cycle => IsOutput ? _app.OutputCycleDevices : _app.InputCycleDevices;
    private IList SourceItems => IsRoutineList ? _app.Routines : IsOutput ? _app.OutputCycleDevices : _app.InputCycleDevices;
    private bool CanReorder => !_closed && !_token.IsCancellationRequested && _app.CanEditManagedList
        && SortBox.SelectedIndex == 0 && FilterBox.SelectedIndex == 0 && string.IsNullOrWhiteSpace(SearchBox.Text);

    internal ListManagerWindow(AppViewModel app, AudioDeviceService audio, ListManagerMode mode, CancellationToken lifetime,
        string? initialDeviceId = null, Func<CancellationToken, Task<ListManagerDeviceInventory>>? readDevices = null)
    {
        _app = app;
        _audio = audio;
        _mode = mode;
        _initialDeviceId = initialDeviceId;
        _readDevices = readDevices ?? (token => app.ReadListManagerDevicesAsync(IsOutput, token));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _lifetime.Token;
        _source = (INotifyCollectionChanged)SourceItems;
        _sourceReorder = IsRoutineList ? app.ReorderRoutinesCommand : IsOutput ? app.ReorderOutputCycleDevicesCommand : app.ReorderInputCycleDevicesCommand;
        _reorder = new RelayCommand(ExecuteReorder, request => CanReorder && _sourceReorder.CanExecute(request));
        InitializeComponent();

        Title = mode switch
        {
            ListManagerMode.Output => "Manage output switch order",
            ListManagerMode.Input => "Manage input switch order",
            _ => "Manage routines",
        };
        IntroText.Text = IsRoutineList
            ? "Find and organize routines. Configured order also controls shared shortcut groups."
            : "Find, add, and organize devices. Configured order controls which device the cycle hotkey selects next.";
        AutomationProperties.SetName(SearchBox, IsRoutineList ? "Search by routine name, shortcut, or trigger" : "Search by device name");
        TextBoxPlaceholderBehavior.SetText(SearchBox, IsRoutineList ? "Name, shortcut, or trigger…" : "Device name…");
        AutomationProperties.SetName(Rows, Title);
        string[] filters = IsRoutineList
            ? ["All routines", "Enabled", "Disabled", "Needs attention"]
            : ["Switch order", "Connected", "Disconnected", "Available to add"];
        FilterBox.ItemsSource = filters;
        FilterBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
        SaveButton.Command = IsRoutineList ? app.SaveRoutinesCommand : app.SaveSettingsCommand;
        SaveButton.Content = IsRoutineList ? "_Save routines" : "_Save settings";
        Interaction.GetBehaviors(Rows).OfType<ListBoxReorderBehavior>().Single().ReorderCommand = _reorder;
        _source.CollectionChanged += Source_CollectionChanged;
        _app.PropertyChanged += App_PropertyChanged;
        if (IsRoutineList) ObserveCurrentRoutines();
        else
        {
            _audio.DeviceStateChanged += RequestDeviceRefresh;
            _audio.DefaultRoleChanged += Audio_DefaultRoleChanged;
        }
        Closed += (_, _) => Detach();
        Loaded += (_, _) =>
        {
            if (!IsRoutineList) RequestDeviceRefresh();
            QueueProjection();
        };
    }

    internal void Detach()
    {
        if (_closed) return;
        _closed = true;
        _source.CollectionChanged -= Source_CollectionChanged;
        _app.PropertyChanged -= App_PropertyChanged;
        _audio.DeviceStateChanged -= RequestDeviceRefresh;
        _audio.DefaultRoleChanged -= Audio_DefaultRoleChanged;
        foreach (AudioRoutine routine in _observedRoutines) routine.PropertyChanged -= Routine_PropertyChanged;
        _observedRoutines.Clear();
        _pendingProjection?.Abort();
        _reorder.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Source_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueProjection();

    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.IsAutoSaveActive) or nameof(AppViewModel.IsSavingRoutines)
            or nameof(AppViewModel.IsResettingSettings) or nameof(AppViewModel.AutomaticRoutinesPaused)
            or nameof(AppViewModel.OutputTabFeedbackText) or nameof(AppViewModel.InputTabFeedbackText)
            or nameof(AppViewModel.RoutinesTabFeedbackText))
            QueueProjection();
    }

    private void ObserveCurrentRoutines()
    {
        foreach (AudioRoutine stale in _observedRoutines.Except(_app.Routines).ToArray())
        {
            stale.PropertyChanged -= Routine_PropertyChanged;
            _observedRoutines.Remove(stale);
        }
        foreach (AudioRoutine routine in _app.Routines)
            if (_observedRoutines.Add(routine)) routine.PropertyChanged += Routine_PropertyChanged;
    }

    private void Routine_PropertyChanged(object? sender, PropertyChangedEventArgs e) => QueueProjection();

    private void Audio_DefaultRoleChanged(DataFlow flow, Role role, string? endpointId)
    {
        if (flow == DataFlow.All || flow == (IsOutput ? DataFlow.Render : DataFlow.Capture)) RequestDeviceRefresh();
    }

    private void RequestDeviceRefresh()
    {
        if (_closed || _token.IsCancellationRequested || Dispatcher.HasShutdownStarted
            || Interlocked.Exchange(ref _refreshRequested, 1) != 0) return;
        _ = Dispatcher.InvokeAsync(RefreshDevices, DispatcherPriority.Background);
    }

    private async void RefreshDevices()
    {
        if (_closed || _token.IsCancellationRequested || _reading) return;
        _reading = true;
        QueueProjection();
        try
        {
            while (!_closed && !_token.IsCancellationRequested && Interlocked.Exchange(ref _refreshRequested, 0) != 0)
            {
                try
                {
                    Task<ListManagerDeviceInventory> read = _readDevices(_token);
                    _ = read.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    var inventory = await read.WaitAsync(_token);
                    if (!_closed && !_token.IsCancellationRequested) _inventory = inventory;
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (_closed || _token.IsCancellationRequested) return;
                    _inventory = null;
                    _app.ReportListManagerDiscoveryFailure(ex);
                }
            }
        }
        finally
        {
            _reading = false;
            QueueProjection();
        }
    }

    private void QueueProjection()
    {
        if (_closed || _token.IsCancellationRequested || !IsLoaded || _pendingProjection?.Status == DispatcherOperationStatus.Pending) return;
        _pendingProjection = Dispatcher.InvokeAsync(ProjectRows, DispatcherPriority.Background);
    }

    private void ProjectRows()
    {
        if (_closed || _token.IsCancellationRequested) return;
        if (IsRoutineList) ObserveCurrentRoutines();
        ListManagerRow[] all = IsRoutineList
            ? [.. _app.Routines.Select(ListManagerProjection.BuildRoutineRow)]
            : ListManagerProjection.BuildDeviceRows(Cycle, _inventory);
        ListManagerRow[] visible = ListManagerProjection.GetVisibleRows(all, _mode, SearchBox.Text, FilterBox.SelectedIndex, SortBox.SelectedIndex);
        if (!_displayed.SequenceEqual(visible))
        {
            string[] selected = [.. Rows.SelectedItems.OfType<ListManagerRow>().Select(row => row.Id)];
            _projecting = true;
            try
            {
                _displayed = visible;
                Rows.ItemsSource = visible;
                foreach (ListManagerRow row in visible)
                    if (selected.Contains(row.Id, StringComparer.OrdinalIgnoreCase)) Rows.SelectedItems.Add(row);
            }
            finally { _projecting = false; }
        }
        if (_initialDeviceId is { } initialDeviceId)
        {
            _initialDeviceId = null;
            if (visible.FirstOrDefault(row => row.Position != null && string.Equals(row.Id, initialDeviceId, StringComparison.OrdinalIgnoreCase)) is { } selected)
            {
                Rows.SelectedItem = selected;
                Rows.ScrollIntoView(selected);
            }
        }

        bool availableToAdd = !IsRoutineList && FilterBox.SelectedIndex == 3;
        int total = IsRoutineList ? all.Length : all.Count(row => (row.Position == null) == availableToAdd);
        string itemLabel = IsRoutineList ? (total == 1 ? "routine" : "routines") : (total == 1 ? "device" : "devices");
        CountText.Text = $"{visible.Length} of {total} {itemLabel}";
        if (!IsRoutineList && _inventory == null)
            CountText.Text += _reading ? " · Checking device status…" : " · Device status unavailable";
        else if (!IsRoutineList && _inventory is { IsComplete: false })
            CountText.Text += " · Some device status is unavailable";
        RefreshStatusButton.Visibility = !IsRoutineList && !_reading && _inventory is not { IsComplete: true } ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = !IsRoutineList && _inventory == null && FilterBox.SelectedIndex > 0
            ? (_reading ? "Checking for devices…" : "Device status is unavailable. Switch back to the configured order to edit it.")
            : !IsRoutineList && _inventory is { IsComplete: false } && FilterBox.SelectedIndex > 0
            ? "Some devices could not be checked. Refresh status to try again."
            : total == 0 ? (IsRoutineList ? "No routines yet. Click Add to create one."
                : availableToAdd ? "No devices available to add."
                : "No devices in the switch order. Choose Add devices to browse available devices.")
            : "No matches. Try a different search or filter.";
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_projecting || !IsLoaded || _closed) return;
        ListManagerRow[] selected = [.. Rows.SelectedItems.OfType<ListManagerRow>()];
        ListManagerRow? row = selected.Length == 1 ? selected[0] : null;
        AudioRoutine? routine = row?.Item as AudioRoutine;
        bool editable = !_token.IsCancellationRequested && _app.CanEditManagedList;
        AddButton.IsEnabled = editable && (IsRoutineList || (!_reading && (FilterBox.SelectedIndex != 3
            || (_inventory is { IsComplete: true } && row is { Position: null, Connected: true }))));
        AddButton.Content = IsRoutineList ? "_Add" : FilterBox.SelectedIndex == 3 ? "_Add selected" : "_Add devices…";
        RemoveButton.IsEnabled = editable && selected.Length != 0 && selected.All(item => item.Position != null);
        int first = selected.Min(item => item.Position) ?? 0;
        int last = selected.Max(item => item.Position) ?? 0;
        bool canMove = CanReorder && first > 0;
        bool contiguous = last - first + 1 == selected.Length;
        MoveUpMenuItem.IsEnabled = canMove && contiguous && first > 1;
        MoveDownMenuItem.IsEnabled = canMove && contiguous && last < SourceItems.Count;
        MoveTopMenuItem.IsEnabled = canMove && last > selected.Length;
        MoveBottomMenuItem.IsEnabled = canMove && first <= SourceItems.Count - selected.Length;
        MoveButton.IsEnabled = MoveTopMenuItem.IsEnabled || MoveBottomMenuItem.IsEnabled;
        RoutineActions.Visibility = routine != null ? Visibility.Visible : Visibility.Collapsed;
        EditButton.IsEnabled = editable && routine != null;
        EnabledButton.IsEnabled = editable && routine != null;
        EnabledButton.Content = routine?.Enabled == true ? "_Disable" : "_Enable";
        DetailTitle.Text = row?.Name ?? (selected.Length == 0 ? "Select an item" : $"{selected.Length} items selected");
        DetailStatus.Text = row?.Status ?? string.Empty;
        DetailStatus.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
        ListManagerDetail[] details = row == null ? [] : routine == null ? BuildDeviceDetails(row) : BuildRoutineDetails(routine);
        if (!_details.SequenceEqual(details)) Details.ItemsSource = _details = details;
        FooterText.Text = IsRoutineList ? _app.RoutinesTabFeedbackText : IsOutput ? _app.OutputTabFeedbackText : _app.InputTabFeedbackText;
    }

    private ListManagerDetail[] BuildDeviceDetails(ListManagerRow row)
    {
        if (row.DuplicateOfPosition is int position)
        {
            return [new("Matching entry", $"Entry {position} shows this device’s Windows default roles.")];
        }

        string defaults = _inventory == null || row.Connected == null ? "Status unavailable" : string.IsNullOrEmpty(row.DefaultRoles)
            ? "No confirmed default role" : row.DefaultRoles;
        if (_inventory?.UnreadableRoles is { Count: > 0 } unreadable) defaults += $"\nStatus unavailable for {string.Join(", ", unreadable)}.";
        return [new("Windows default roles", defaults)];
    }

    private ListManagerDetail[] BuildRoutineDetails(AudioRoutine routine)
    {
        List<ListManagerDetail> details = [new("Actions", routine.RoutineDetailsTargetSummary), new("Triggers", routine.RoutineDetailsTriggerSummary)];
        if (routine.HasConditions) details.Add(new("Requirements", routine.ConditionsSummary));
        string next = RoutineScheduleCalculator.BuildPreview(routine, DateTime.UtcNow, _app.AutomaticRoutinesPaused);
        if (!string.IsNullOrWhiteSpace(next)) details.Add(new("Next scheduled trigger", next));
        string lastRun = routine.LastRunUtc is { } timestamp
            ? $"{timestamp.ToLocalTime():g}\n{routine.LastRunStatusText}"
            : "Never";
        details.Add(new("Last run this session", lastRun));
        if (routine.HasConflict) details.Add(new("Conflict", routine.ConflictSummary));
        if (routine.HasHotkeyWarning) details.Add(new("Shortcut", routine.HotkeyWarningSummary));
        if (routine.HasRoutineDetailsOptions) details.Add(new("Options", routine.RoutineDetailsOptionsSummary));
        return [.. details];
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ProjectRows();
    }

    private void View_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ProjectRows();
    }
    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void FocusSearch_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _ = SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void RefreshStatus_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !IsRoutineList && !_closed;
    private void RefreshStatus_Executed(object sender, ExecutedRoutedEventArgs e) => RequestDeviceRefresh();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || Rows.Items.Count == 0) return;
        if (Rows.SelectedIndex < 0) Rows.SelectedIndex = 0;
        Rows.ScrollIntoView(Rows.SelectedItem);
        Rows.UpdateLayout();
        if (Rows.ItemContainerGenerator.ContainerFromItem(Rows.SelectedItem) is ListBoxItem item) _ = item.Focus();
        else _ = Rows.Focus();
        e.Handled = true;
    }

    private void Rows_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Rows.SelectedItems.Count != 1) return;
        if (IsRoutineList) Edit_Click(sender, e);
        else if (FilterBox.SelectedIndex == 3) Add_Click(sender, e);
        else return;
        e.Handled = true;
    }

    private void Rows_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsRoutineList && e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject origin
            && ItemsControl.ContainerFromElement(Rows, origin) is ListBoxItem && Rows.SelectedItems.Count == 1)
            Edit_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.CanEditManagedList || _token.IsCancellationRequested) return;
        if (IsRoutineList) _app.AddRoutine(this);
        else if (FilterBox.SelectedIndex != 3)
        {
            FilterBox.SelectedIndex = 3;
            _ = SearchBox.Focus();
        }
        else if (!_reading && Volatile.Read(ref _refreshRequested) == 0 && Rows.SelectedItems.Count == 1
            && Rows.SelectedItem is ListManagerRow { Position: null, Connected: true } row
            && _inventory is { IsComplete: true }
            && _inventory.Active.FirstOrDefault(device => string.Equals(device.Id, row.Id, StringComparison.OrdinalIgnoreCase)) is { } device)
        {
            if (!_app.AddCycleDevice(device, IsOutput)) return;
            FilterBox.SelectedIndex = 0;
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.CanEditManagedList || _token.IsCancellationRequested || Rows.SelectedItems.Count != 1
            || Rows.SelectedItem is not ListManagerRow { Item: AudioRoutine routine }) return;
        _app.EditRoutine(routine, this);
    }

    private void SetEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.CanEditManagedList || _token.IsCancellationRequested || Rows.SelectedItems.Count != 1
            || Rows.SelectedItem is not ListManagerRow { Item: AudioRoutine routine } || !_app.Routines.Contains(routine)) return;
        routine.Enabled = !routine.Enabled;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.CanEditManagedList || _token.IsCancellationRequested) return;
        object[] selected = [.. Rows.SelectedItems.OfType<ListManagerRow>().Where(row => row.Position != null)
            .Select(row => row.Item).Where(SourceItems.Contains)];
        if (selected.Length == 0) return;
        if (IsRoutineList)
        {
            _app.SelectedRoutines.Clear();
            _app.SelectedRoutineIndex = _app.Routines.IndexOf((AudioRoutine)selected[0]);
            foreach (var routine in selected.Cast<AudioRoutine>())
                if (!_app.SelectedRoutines.Contains(routine)) _app.SelectedRoutines.Add(routine);
            _app.RemoveSelectedRoutine();
        }
        else
        {
            var selection = IsOutput ? _app.SelectedOutputCycleDevices : _app.SelectedInputCycleDevices;
            selection.Clear();
            var first = (CycleDevice)selected[0];
            if (IsOutput) _app.SelectedOutputCycleIndex = _app.OutputCycleDevices.IndexOf(first);
            else _app.SelectedInputCycleIndex = _app.InputCycleDevices.IndexOf(first);
            foreach (var device in selected.Cast<CycleDevice>())
                if (!selection.Contains(device)) selection.Add(device);
            (IsOutput ? _app.RemoveOutputCycleDeviceCommand : _app.RemoveInputCycleDeviceCommand).Execute(null);
        }
    }

    private void MoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!MoveButton.IsEnabled) return;
        MoveButton.ContextMenu.PlacementTarget = MoveButton;
        MoveButton.ContextMenu.IsOpen = true;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (MoveUpMenuItem.IsEnabled && Rows.SelectedItems.OfType<ListManagerRow>().Min(row => row.Position) is int first)
            MoveSelection(first - 2);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (MoveDownMenuItem.IsEnabled && Rows.SelectedItems.OfType<ListManagerRow>().Max(row => row.Position) is int last)
            MoveSelection(last + 1);
    }

    private void MoveTop_Click(object sender, RoutedEventArgs e) => MoveSelection(0);
    private void MoveBottom_Click(object sender, RoutedEventArgs e) => MoveSelection(SourceItems.Count);

    private void MoveSelection(int destination)
    {
        if (!CanReorder) return;
        ExecuteReorder(new ListReorderRequest([.. Rows.SelectedItems.Cast<object>()], destination));
    }

    private void ExecuteReorder(object? parameter)
    {
        if (!CanReorder || parameter is not ListReorderRequest request || !_sourceReorder.CanExecute(request)) return;
        if (!_displayed.Select(row => row.Item).SequenceEqual(SourceItems.Cast<object>(), ReferenceEqualityComparer.Instance)) return;
        _sourceReorder.Execute(new ListReorderRequest([.. request.Items.Cast<ListManagerRow>().Select(row => row.Item)], request.InsertionIndex));
    }
}

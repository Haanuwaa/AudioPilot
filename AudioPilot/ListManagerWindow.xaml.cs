using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
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
    private bool _checkingRoutine;
    private readonly Func<AudioRoutine, CancellationToken, Task<RoutineReadinessResult>> _checkRoutine;
    private (AudioRoutine Routine, string Name, bool Enabled, string Configuration, string Triggers)? _readinessState;
    private bool IsRoutineList => _mode == ListManagerMode.Routines;
    private bool IsOutput => _mode == ListManagerMode.Output;
    private IReadOnlyList<CycleDevice> Cycle => IsOutput ? _app.OutputCycleDevices : _app.InputCycleDevices;
    private IList SourceItems => IsRoutineList ? _app.Routines : IsOutput ? _app.OutputCycleDevices : _app.InputCycleDevices;
    private bool CanReorder => !_closed && !_token.IsCancellationRequested && _app.CanEditManagedList
        && SortBox.SelectedIndex == 0 && FilterBox.SelectedIndex == 0 && string.IsNullOrWhiteSpace(SearchBox.Text);

    internal ListManagerWindow(AppViewModel app, AudioDeviceService audio, ListManagerMode mode, CancellationToken lifetime,
        string? initialDeviceId = null, Func<CancellationToken, Task<ListManagerDeviceInventory>>? readDevices = null,
        Func<AudioRoutine, CancellationToken, Task<RoutineReadinessResult>>? checkRoutine = null)
    {
        _app = app;
        _checkRoutine = checkRoutine ?? app.CheckRoutineDraftAsync;
        _audio = audio;
        _mode = mode;
        _initialDeviceId = initialDeviceId;
        _readDevices = readDevices ?? (token => app.ReadListManagerDevicesAsync(IsOutput, token));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _lifetime.Token;
        _source = (INotifyCollectionChanged)SourceItems;
        _sourceReorder = IsRoutineList ? app.ReorderRoutinesCommand : IsOutput ? app.ReorderOutputCycleDevicesCommand : app.ReorderInputCycleDevicesCommand;
        _reorder = new RelayCommand(ExecuteReorder, request => CanReorder && _sourceReorder.CanExecute(request));
        LocalizationContext.SetService(this, _app.Localization);
        InitializeComponent();
        PropertyChangedEventManager.AddHandler(_app.Localization, LanguageChanged, nameof(LocalizationService.Revision));

        LocalizedText heading = new(mode switch
        {
            ListManagerMode.Output => "Manage_Output",
            ListManagerMode.Input => "Manage_Input",
            _ => "Manage_Routines",
        });
        LocalizationContext.SetText(this, TitleProperty, heading);
        LocalizationContext.SetText(IntroText, TextBlock.TextProperty, new(IsRoutineList ? "Manage_RoutineIntro" : "Manage_DeviceIntro"));
        LocalizationContext.SetText(SearchBox, AutomationProperties.NameProperty, new(IsRoutineList ? "Manage_SearchRoutine" : "Manage_SearchDevice"));
        LocalizationContext.SetText(SearchBox, TextBoxPlaceholderBehavior.TextProperty, new(IsRoutineList ? "Manage_RoutineHint" : "Manage_DeviceHint"));
        LocalizationContext.SetText(Rows, AutomationProperties.NameProperty, heading);
        LocalizedText[] filters = IsRoutineList
            ? [new("Manage_AllRoutines"), new("Manage_Enabled"), new("Manage_Disabled"), new("Manage_Attention")]
            : [new("Manage_SwitchOrder"), new("Manage_Connected"), new("Manage_Disconnected"), new("Manage_AvailableToAdd")];
        FilterBox.ItemsSource = filters;
        FilterBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
        RunButton.Command = app.RunSelectedRoutineCommand;
        SaveButton.Command = IsRoutineList ? app.SaveRoutinesCommand : app.SaveSettingsCommand;
        LocalizationContext.SetText(SaveButton, ContentControl.ContentProperty, new(IsRoutineList ? "Manage_SaveRoutines" : "Manage_SaveSettings"));
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
        PropertyChangedEventManager.RemoveHandler(_app.Localization, LanguageChanged, nameof(LocalizationService.Revision));
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

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => QueueProjection();

    private void Source_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueProjection();

    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.IsAutoSaveActive) or nameof(AppViewModel.IsSavingRoutines)
            or nameof(AppViewModel.IsRuntimeShutdownRequested)
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
                    _app.ReportListManagerFailure(ex);
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
        string itemLabel = IsRoutineList ? (total == 1 ? "Manage_OneRoutine" : "Manage_ManyRoutines") : (total == 1 ? "Manage_OneDevice" : "Manage_ManyDevices");
        CountText.Text = _app.Localization.FormatKey("Manage_ItemCount", visible.Length, total, _app.Localization[itemLabel]);
        if (!IsRoutineList && _inventory == null)
            CountText.Text += _reading ? _app.Localization["Manage_CheckingDeviceStatus"] : _app.Localization["Manage_DeviceStatusUnavailable"];
        else if (!IsRoutineList && _inventory is { IsComplete: false })
            CountText.Text += _app.Localization["Manage_SomeDeviceStatusIsUnavailable"];
        RefreshStatusButton.Visibility = !IsRoutineList && !_reading && _inventory is not { IsComplete: true } ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = !IsRoutineList && _inventory == null && FilterBox.SelectedIndex > 0
            ? (_reading ? _app.Localization["Manage_CheckingForDevices"] : _app.Localization["Manage_DeviceStatusIsUnavailableSwitchBackTo"])
            : !IsRoutineList && _inventory is { IsComplete: false } && FilterBox.SelectedIndex > 0
            ? _app.Localization["Manage_SomeDevicesCouldNotBeCheckedRefresh"]
            : total == 0 ? (IsRoutineList ? _app.Localization["Ui_NoRoutinesYetClickAddToCreateOne"]
                : availableToAdd ? _app.Localization["Manage_NoDevicesAvailableToAdd"]
                : _app.Localization["Manage_NoDevicesInTheSwitchOrderChoose"])
            : _app.Localization["Manage_NoMatchesTryADifferentSearchOr"];
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
        AddButton.Content = IsRoutineList ? _app.Localization["Ui_Add"] : FilterBox.SelectedIndex == 3 ? _app.Localization["Manage_AddSelected"] : _app.Localization["Manage_AddDevices"];
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
        DeviceActions.Visibility = !IsRoutineList && row != null ? Visibility.Visible : Visibility.Collapsed;
        TestButton.IsEnabled = SetDefaultButton.IsEnabled = ResolveActionDevice() != null;
        RunButton.CommandParameter = routine;
        RunButton.IsEnabled = routine != null && _app.CanRunRoutineManually(routine);
        CheckButton.IsEnabled = editable && routine != null && !_checkingRoutine;
        if (_readinessState.HasValue && (routine == null || _readinessState.Value != CaptureReadinessState(routine)))
        {
            ReadinessText.Visibility = Visibility.Collapsed;
            _readinessState = null;
        }
        EditButton.IsEnabled = editable && routine != null;
        EnabledButton.IsEnabled = editable && routine != null;
        EnabledButton.Content = routine?.Enabled == true ? _app.Localization["Manage_Disable"] : _app.Localization["Ui_Enable"];
        DetailTitle.Text = row?.Name ?? (selected.Length == 0 ? _app.Localization["Manage_SelectAnItem"] : _app.Localization.FormatKey("Manage_SelectionCount", selected.Length));
        DetailStatus.Text = row?.FormatStatus(_app.Localization) ?? string.Empty;
        DetailStatus.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
        ListManagerDetail[] details = row == null ? [] : routine == null ? BuildDeviceDetails(row) : BuildRoutineDetails(routine);
        if (!_details.SequenceEqual(details)) Details.ItemsSource = _details = details;
        FooterText.Text = (IsRoutineList ? _app.RoutinesTabFeedbackPresentation
            : IsOutput ? _app.OutputTabFeedbackPresentation : _app.InputTabFeedbackPresentation)(_app.Localization);
    }

    private ListManagerDetail[] BuildDeviceDetails(ListManagerRow row)
    {
        if (row.DuplicateOfPosition is int position)
        {
            return [new(_app.Localization["Manage_MatchingEntry"], _app.Localization.FormatKey("Manage_MatchingPosition", position))];
        }

        string defaults = _inventory == null || row.Connected == null ? _app.Localization["Manage_StatusUnavailable"] : string.IsNullOrEmpty(row.DefaultRoles)
            ? _app.Localization["Manage_NoConfirmedDefaultRole"] : ListManagerRow.FormatRoles(row.DefaultRoles, _app.Localization);
        if (_inventory?.UnreadableRoles is { Count: > 0 } unreadable) defaults += Environment.NewLine + _app.Localization.FormatKey("Manage_UnreadableRoles", ListManagerRow.FormatRoles(string.Join(", ", unreadable), _app.Localization));
        return [new(_app.Localization["Manage_WindowsDefaultRoles"], defaults)];
    }

    private ListManagerDetail[] BuildRoutineDetails(AudioRoutine routine)
    {
        List<ListManagerDetail> details = [new(_app.Localization["Ui_Actions"], routine.TargetPresentation(_app.Localization)), new(_app.Localization["Ui_Triggers"], routine.TriggerDetailsPresentation(_app.Localization))];
        if (routine.HasConditions) details.Add(new(_app.Localization["Manage_Requirements"], routine.ConditionsPresentation(_app.Localization)));
        string next = RoutineScheduleCalculator.BuildPreview(routine, DateTime.UtcNow, _app.AutomaticRoutinesPaused);
        if (!string.IsNullOrWhiteSpace(next)) details.Add(new(_app.Localization["Manage_NextScheduledTrigger"], AppMessages.Translate(next, _app.Localization)));
        string lastRun = routine.LastRunUtc is { } timestamp
            ? $"{timestamp.ToLocalTime():g}\n{routine.LastRunPresentation(_app.Localization)}"
            : _app.Localization["Manage_Never"];
        details.Add(new(_app.Localization["Manage_LastRunThisSession"], lastRun));
        if (routine.HasConflict) details.Add(new(_app.Localization["Manage_Conflict"], AppMessages.Translate(routine.ConflictSummary, _app.Localization)));
        if (routine.HasHotkeyWarning) details.Add(new(_app.Localization["Manage_Shortcut"], AppMessages.Translate(routine.HotkeyWarningSummary, _app.Localization)));
        if (routine.HasRoutineDetailsOptions) details.Add(new(_app.Localization["Ui_Options"], routine.OptionsPresentation(_app.Localization)));
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

    private CycleDevice? ResolveActionDevice()
    {
        if (_closed || _token.IsCancellationRequested || !_app.CanEditManagedList || IsRoutineList || _reading
            || Volatile.Read(ref _refreshRequested) != 0 || Rows.SelectedItems.Count != 1
            || Rows.SelectedItem is not ListManagerRow { Connected: true, DuplicateOfPosition: null, ResolvedDeviceId: { } id }
            || _inventory?.Active.FirstOrDefault(device => string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } target) return null;
        return target.Clone();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveActionDevice() is not { } target) return;
        var command = IsOutput ? _app.AudioTesting.TestOutputCommand : _app.AudioTesting.TestInputCommand;
        if (!command.CanExecute(target)) return;
        _app.SelectedSettingsTabIndex = IsOutput ? 0 : 1;
        Close();
        command.Execute(target);
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveActionDevice() is not { } target) return;
        ICommand command = IsOutput ? _app.SetDefaultOutputDeviceCommand : _app.SetDefaultInputDeviceCommand;
        if (command.CanExecute(target)) command.Execute(target);
    }

    private static (AudioRoutine, string, bool, string, string) CaptureReadinessState(AudioRoutine routine) =>
        (routine, routine.Name, routine.Enabled, AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine), routine.TriggerFingerprint);

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _token.IsCancellationRequested || !_app.CanEditManagedList || _checkingRoutine || Rows.SelectedItems.Count != 1
            || Rows.SelectedItem is not ListManagerRow { Item: AudioRoutine routine } || !_app.Routines.Contains(routine)) return;
        var state = CaptureReadinessState(routine);
        _readinessState = state;
        _checkingRoutine = true;
        UpdateSelection();
        try
        {
            Task<RoutineReadinessResult> read = _checkRoutine(routine.Clone(), _token);
            _ = read.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            RoutineReadinessResult result = await read.WaitAsync(_token);
            if (_closed || _token.IsCancellationRequested || _readinessState != state || Rows.SelectedItems.Count != 1
                || Rows.SelectedItem is not ListManagerRow { Item: AudioRoutine current }
                || state != CaptureReadinessState(current)) return;
            LocalizationContext.SetPresentation(ReadinessText, TextBlock.TextProperty, localization =>
                result.FormatForUi(localization) + Environment.NewLine + localization.FormatKey("Routine_ReadinessChecked", result.CheckedUtc.ToLocalTime()));
            ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, result.Ready ? "DialogSuccessBrush" : "DialogWarningBrush");
            ReadinessText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _app.ReportListManagerFailure(ex, "routine-check");
            if (!_closed && _readinessState == state && Rows.SelectedItems.Count == 1
                && Rows.SelectedItem is ListManagerRow { Item: AudioRoutine current } && state == CaptureReadinessState(current))
            {
                LocalizationContext.SetText(ReadinessText, TextBlock.TextProperty, new("Routine_ReadinessUnavailable"));
                ReadinessText.SetResourceReference(TextBlock.ForegroundProperty, "DialogWarningBrush");
                ReadinessText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _checkingRoutine = false;
            if (!_closed) UpdateSelection();
        }
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

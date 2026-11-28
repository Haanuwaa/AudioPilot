using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioPilot.Controls;

public partial class RoutineTriggerEditorPanel : UserControl
{
    public static readonly DependencyProperty ShowActionButtonsProperty = DependencyProperty.Register(
        nameof(ShowActionButtons),
        typeof(bool),
        typeof(RoutineTriggerEditorPanel),
        new PropertyMetadata(true));

    public static readonly RoutedEvent ConfirmRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ConfirmRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RoutineTriggerEditorPanel));

    public static readonly RoutedEvent CancelRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CancelRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RoutineTriggerEditorPanel));

    public static readonly RoutedEvent BrowseApplicationRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(BrowseApplicationRequested), RoutingStrategy.Bubble, typeof(EventHandler<AppTargetPickerActionEventArgs>), typeof(RoutineTriggerEditorPanel));

    public static readonly RoutedEvent RunningApplicationRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(RunningApplicationRequested), RoutingStrategy.Bubble, typeof(EventHandler<AppTargetPickerActionEventArgs>), typeof(RoutineTriggerEditorPanel));

    public static readonly RoutedEvent PackagedApplicationRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(PackagedApplicationRequested), RoutingStrategy.Bubble, typeof(EventHandler<AppTargetPickerActionEventArgs>), typeof(RoutineTriggerEditorPanel));

    public RoutineTriggerEditorPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPanelPreviewKeyDown;
    }

    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ShowActionButtons)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (FindAncestorComboBox(e.OriginalSource as DependencyObject) is { IsDropDownOpen: true })
            {
                return;
            }

            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(CancelRequestedEvent, this));
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (e.OriginalSource is Button || FindAncestorComboBox(e.OriginalSource as DependencyObject) is { IsDropDownOpen: true })
            {
                return;
            }

            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(ConfirmRequestedEvent, this));
        }
    }

    private static ComboBox? FindAncestorComboBox(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current != null)
        {
            if (current is ComboBox comboBox)
            {
                return comboBox;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public bool ShowActionButtons
    {
        get => (bool)GetValue(ShowActionButtonsProperty);
        set => SetValue(ShowActionButtonsProperty, value);
    }

    public event RoutedEventHandler ConfirmRequested
    {
        add => AddHandler(ConfirmRequestedEvent, value);
        remove => RemoveHandler(ConfirmRequestedEvent, value);
    }

    public event RoutedEventHandler CancelRequested
    {
        add => AddHandler(CancelRequestedEvent, value);
        remove => RemoveHandler(CancelRequestedEvent, value);
    }

    public event EventHandler<AppTargetPickerActionEventArgs> BrowseApplicationRequested
    {
        add => AddHandler(BrowseApplicationRequestedEvent, value);
        remove => RemoveHandler(BrowseApplicationRequestedEvent, value);
    }

    public event EventHandler<AppTargetPickerActionEventArgs> RunningApplicationRequested
    {
        add => AddHandler(RunningApplicationRequestedEvent, value);
        remove => RemoveHandler(RunningApplicationRequestedEvent, value);
    }

    public event EventHandler<AppTargetPickerActionEventArgs> PackagedApplicationRequested
    {
        add => AddHandler(PackagedApplicationRequestedEvent, value);
        remove => RemoveHandler(PackagedApplicationRequestedEvent, value);
    }

    public event EventHandler? NetworkDropDownOpened;

    public void FocusTriggerType() => TriggerTypeBox.Focus();

    private void ConfirmTriggerButton_Click(object sender, RoutedEventArgs e) =>
        RaiseEvent(new RoutedEventArgs(ConfirmRequestedEvent, this));

    private void CancelTriggerButton_Click(object sender, RoutedEventArgs e) =>
        RaiseEvent(new RoutedEventArgs(CancelRequestedEvent, this));

    private void TriggerApp_BrowseRequested(object sender, AppTargetPickerActionEventArgs e) =>
        RaiseEvent(new AppTargetPickerActionEventArgs(BrowseApplicationRequestedEvent, e.SourceButton));

    private void TriggerApp_RunningAppRequested(object sender, AppTargetPickerActionEventArgs e) =>
        RaiseEvent(new AppTargetPickerActionEventArgs(RunningApplicationRequestedEvent, e.SourceButton));

    private void TriggerApp_PackagedAppRequested(object sender, AppTargetPickerActionEventArgs e) =>
        RaiseEvent(new AppTargetPickerActionEventArgs(PackagedApplicationRequestedEvent, e.SourceButton));

    private void NetworkPicker_DropDownOpened(object sender, EventArgs e) =>
        NetworkDropDownOpened?.Invoke(this, e);

    private void ScheduleDayOfWeekPicker_PresetSelected(object? _, ViewModels.DaySelectionPreset preset)
    {
        if (DataContext is ViewModels.RoutineTriggerDraftViewModel draft)
        {
            draft.ApplyScheduleDayPreset(preset);
        }
    }
}

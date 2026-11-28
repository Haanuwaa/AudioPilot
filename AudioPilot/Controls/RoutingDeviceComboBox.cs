using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.Controls;

/// <summary>Searches the visible routing labels without applying a route while typing.</summary>
public sealed class RoutingDeviceComboBox : ComboBox
{
    public static readonly DependencyProperty DevicesProperty = DependencyProperty.Register(
        nameof(Devices), typeof(IReadOnlyList<ApplicationRoutingDevice>), typeof(RoutingDeviceComboBox),
        new PropertyMetadata(null, OnDevicesChanged));

    private readonly Queue<TextCompositionEventArgs> _pendingText = new();
    private DispatcherOperation? _replay;

    public RoutingDeviceComboBox()
    {
        TextSearch.SetTextPath(this, nameof(DeviceChoice.SearchText));
        SelectedValuePath = nameof(DeviceChoice.Device);
    }

    public IReadOnlyList<ApplicationRoutingDevice>? Devices
    {
        get => (IReadOnlyList<ApplicationRoutingDevice>?)GetValue(DevicesProperty);
        set => SetValue(DevicesProperty, value);
    }

    private static void OnDevicesChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var combo = (RoutingDeviceComboBox)target;
        combo.ItemsSource = args.NewValue is IReadOnlyList<ApplicationRoutingDevice> devices
            ? devices.Select(device => new DeviceChoice(combo, device)).ToArray()
            : null;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (!IsLoaded || !IsEnabled) return;
        if (IsDropDownOpen && _replay is null)
        {
            base.OnTextInput(e);
            return;
        }
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;

        e.Handled = true;
        _pendingText.Enqueue(e);
        if (_replay is not null) return;

        // WPF commits text search when closed. Let the popup realize and focus its initial item first.
        SetCurrentValue(IsDropDownOpenProperty, true);
        if (IsDropDownOpen) _replay = Dispatcher.InvokeAsync(ReplayText, DispatcherPriority.Loaded);
        else _pendingText.Clear();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool alt = (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) != 0;
        if (IsDropDownOpen && (key == Key.F4 && !alt || alt && key is Key.Up or Key.Down))
        {
            SetCurrentValue(IsDropDownOpenProperty, false);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != IsDropDownOpenProperty || IsDropDownOpen) return;
        _replay?.Abort();
        _replay = null;
        _pendingText.Clear();
    }

    private void ReplayText()
    {
        _replay = null;
        while (IsDropDownOpen && IsLoaded && IsEnabled && _pendingText.TryDequeue(out var input))
        {
            base.OnTextInput(new TextCompositionEventArgs(input.Device, input.TextComposition)
            {
                RoutedEvent = TextCompositionManager.TextInputEvent,
                Source = this,
            });
        }
        _pendingText.Clear();
    }

    private sealed class DeviceChoice(RoutingDeviceComboBox owner, ApplicationRoutingDevice device)
    {
        public ApplicationRoutingDevice Device { get; } = device;
        public bool IsSelectable => Device.IsSelectable;
        public string SearchText => IsSelectable ? ToString() : string.Empty;

        public override string ToString() => LocalizationContext.GetService(owner).FormatValue(Device.LabelText);
    }
}

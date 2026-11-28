using System.Windows;
using System.Windows.Controls;
using AudioPilot.ViewModels;

namespace AudioPilot.Controls;

public partial class SettingsHotkeyRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(SettingsHotkeyRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FieldNameProperty = DependencyProperty.Register(
        nameof(FieldName),
        typeof(string),
        typeof(SettingsHotkeyRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey),
        typeof(HotkeyViewModel),
        typeof(SettingsHotkeyRow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsFieldEnabledProperty = DependencyProperty.Register(
        nameof(IsFieldEnabled),
        typeof(bool),
        typeof(SettingsHotkeyRow),
        new PropertyMetadata(true));

    public SettingsHotkeyRow()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string FieldName
    {
        get => (string)GetValue(FieldNameProperty);
        set => SetValue(FieldNameProperty, value);
    }

    public HotkeyViewModel? Hotkey
    {
        get => (HotkeyViewModel?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool IsFieldEnabled
    {
        get => (bool)GetValue(IsFieldEnabledProperty);
        set => SetValue(IsFieldEnabledProperty, value);
    }
}

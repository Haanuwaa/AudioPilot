using System.Windows;
using System.Windows.Controls;

namespace AudioPilot.Controls;

public partial class TimeOfDayPicker : UserControl
{
    public static readonly DependencyProperty HourOptionsProperty = DependencyProperty.Register(
        nameof(HourOptions),
        typeof(IReadOnlyList<string>),
        typeof(TimeOfDayPicker),
        new PropertyMetadata(Array.Empty<string>()));

    public static readonly DependencyProperty MinuteOptionsProperty = DependencyProperty.Register(
        nameof(MinuteOptions),
        typeof(IReadOnlyList<string>),
        typeof(TimeOfDayPicker),
        new PropertyMetadata(Array.Empty<string>()));

    public static readonly DependencyProperty AmPmOptionsProperty = DependencyProperty.Register(
        nameof(AmPmOptions),
        typeof(IReadOnlyList<string>),
        typeof(TimeOfDayPicker),
        new PropertyMetadata(Array.Empty<string>()));

    public static readonly DependencyProperty Is24HourFormatProperty = DependencyProperty.Register(
        nameof(Is24HourFormat),
        typeof(bool),
        typeof(TimeOfDayPicker),
        new PropertyMetadata(false));

    public static readonly DependencyProperty HourIndexProperty = DependencyProperty.Register(
        nameof(HourIndex),
        typeof(int),
        typeof(TimeOfDayPicker),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MinuteIndexProperty = DependencyProperty.Register(
        nameof(MinuteIndex),
        typeof(int),
        typeof(TimeOfDayPicker),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty AmPmIndexProperty = DependencyProperty.Register(
        nameof(AmPmIndex),
        typeof(int),
        typeof(TimeOfDayPicker),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HourAutomationNameProperty = DependencyProperty.Register(
        nameof(HourAutomationName),
        typeof(string),
        typeof(TimeOfDayPicker),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MinuteAutomationNameProperty = DependencyProperty.Register(
        nameof(MinuteAutomationName),
        typeof(string),
        typeof(TimeOfDayPicker),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty AmPmAutomationNameProperty = DependencyProperty.Register(
        nameof(AmPmAutomationName),
        typeof(string),
        typeof(TimeOfDayPicker),
        new PropertyMetadata(string.Empty));

    public TimeOfDayPicker()
    {
        InitializeComponent();
    }

    public IReadOnlyList<string> HourOptions
    {
        get => (IReadOnlyList<string>)GetValue(HourOptionsProperty);
        set => SetValue(HourOptionsProperty, value);
    }

    public IReadOnlyList<string> MinuteOptions
    {
        get => (IReadOnlyList<string>)GetValue(MinuteOptionsProperty);
        set => SetValue(MinuteOptionsProperty, value);
    }

    public IReadOnlyList<string> AmPmOptions
    {
        get => (IReadOnlyList<string>)GetValue(AmPmOptionsProperty);
        set => SetValue(AmPmOptionsProperty, value);
    }

    public bool Is24HourFormat
    {
        get => (bool)GetValue(Is24HourFormatProperty);
        set => SetValue(Is24HourFormatProperty, value);
    }

    public int HourIndex
    {
        get => (int)GetValue(HourIndexProperty);
        set => SetValue(HourIndexProperty, value);
    }

    public int MinuteIndex
    {
        get => (int)GetValue(MinuteIndexProperty);
        set => SetValue(MinuteIndexProperty, value);
    }

    public int AmPmIndex
    {
        get => (int)GetValue(AmPmIndexProperty);
        set => SetValue(AmPmIndexProperty, value);
    }

    public string HourAutomationName
    {
        get => (string)GetValue(HourAutomationNameProperty);
        set => SetValue(HourAutomationNameProperty, value);
    }

    public string MinuteAutomationName
    {
        get => (string)GetValue(MinuteAutomationNameProperty);
        set => SetValue(MinuteAutomationNameProperty, value);
    }

    public string AmPmAutomationName
    {
        get => (string)GetValue(AmPmAutomationNameProperty);
        set => SetValue(AmPmAutomationNameProperty, value);
    }
}

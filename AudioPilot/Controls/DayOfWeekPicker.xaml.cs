using System.Windows;
using System.Windows.Controls;
using AudioPilot.ViewModels;

namespace AudioPilot.Controls;

public partial class DayOfWeekPicker : UserControl
{
    public static readonly DependencyProperty SundayProperty = DependencyProperty.Register(
        nameof(Sunday), typeof(bool), typeof(DayOfWeekPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MondayProperty = DependencyProperty.Register(
        nameof(Monday), typeof(bool), typeof(DayOfWeekPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty TuesdayProperty = DependencyProperty.Register(
        nameof(Tuesday), typeof(bool), typeof(DayOfWeekPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty WednesdayProperty = DependencyProperty.Register(
        nameof(Wednesday), typeof(bool), typeof(DayOfWeekPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ThursdayProperty = DependencyProperty.Register(
        nameof(Thursday), typeof(bool), typeof(DayOfWeekPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty FridayProperty = DependencyProperty.Register(
        nameof(Friday), typeof(bool), typeof(DayOfWeekPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SaturdayProperty = DependencyProperty.Register(
        nameof(Saturday), typeof(bool), typeof(DayOfWeekPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ShowAllDayProperty = DependencyProperty.Register(
        nameof(ShowAllDay), typeof(bool), typeof(DayOfWeekPicker),
        new PropertyMetadata(false));

    public static readonly DependencyProperty AllDayProperty = DependencyProperty.Register(
        nameof(AllDay), typeof(bool), typeof(DayOfWeekPicker),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ShowPresetsProperty = DependencyProperty.Register(
        nameof(ShowPresets), typeof(bool), typeof(DayOfWeekPicker),
        new PropertyMetadata(true));

    public DayOfWeekPicker()
    {
        InitializeComponent();
    }

    public bool Sunday { get => (bool)GetValue(SundayProperty); set => SetValue(SundayProperty, value); }
    public bool Monday { get => (bool)GetValue(MondayProperty); set => SetValue(MondayProperty, value); }
    public bool Tuesday { get => (bool)GetValue(TuesdayProperty); set => SetValue(TuesdayProperty, value); }
    public bool Wednesday { get => (bool)GetValue(WednesdayProperty); set => SetValue(WednesdayProperty, value); }
    public bool Thursday { get => (bool)GetValue(ThursdayProperty); set => SetValue(ThursdayProperty, value); }
    public bool Friday { get => (bool)GetValue(FridayProperty); set => SetValue(FridayProperty, value); }
    public bool Saturday { get => (bool)GetValue(SaturdayProperty); set => SetValue(SaturdayProperty, value); }
    public bool ShowAllDay { get => (bool)GetValue(ShowAllDayProperty); set => SetValue(ShowAllDayProperty, value); }
    public bool AllDay { get => (bool)GetValue(AllDayProperty); set => SetValue(AllDayProperty, value); }
    public bool ShowPresets { get => (bool)GetValue(ShowPresetsProperty); set => SetValue(ShowPresetsProperty, value); }

    public event EventHandler<DaySelectionPreset>? PresetSelected;

    private void EveryDayPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetSelected != null) PresetSelected.Invoke(this, DaySelectionPreset.EveryDay);
        else SetDays(false, false, false, false, false, false, false);
    }

    private void WeekdaysPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetSelected != null) PresetSelected.Invoke(this, DaySelectionPreset.Weekdays);
        else SetDays(false, true, true, true, true, true, false);
    }

    private void WeekendsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetSelected != null) PresetSelected.Invoke(this, DaySelectionPreset.Weekends);
        else SetDays(true, false, false, false, false, false, true);
    }

    private void SetDays(bool sun, bool mon, bool tue, bool wed, bool thu, bool fri, bool sat)
    {
        Sunday = sun;
        Monday = mon;
        Tuesday = tue;
        Wednesday = wed;
        Thursday = thu;
        Friday = fri;
        Saturday = sat;
    }
}

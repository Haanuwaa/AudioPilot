using System.Windows;
using System.Windows.Controls;

namespace AudioPilot.Controls;

public sealed class AppTargetPickerActionEventArgs(RoutedEvent routedEvent, Button sourceButton) : RoutedEventArgs(routedEvent)
{
    public Button SourceButton { get; } = sourceButton;
}

public partial class AppTargetPickerControl : UserControl
{
    public static readonly DependencyProperty TargetPathProperty = DependencyProperty.Register(
        nameof(TargetPath),
        typeof(string),
        typeof(AppTargetPickerControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ResolvedAppNameProperty = DependencyProperty.Register(
        nameof(ResolvedAppName),
        typeof(string),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HasResolvedAppProperty = DependencyProperty.Register(
        nameof(HasResolvedApp),
        typeof(bool),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(false));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty InputAutomationNameProperty = DependencyProperty.Register(
        nameof(InputAutomationName),
        typeof(string),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty InputHelpTextProperty = DependencyProperty.Register(
        nameof(InputHelpText),
        typeof(string),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BrowseAutomationNameProperty = DependencyProperty.Register(
        nameof(BrowseAutomationName),
        typeof(string),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RunningAppsAutomationNameProperty = DependencyProperty.Register(
        nameof(RunningAppsAutomationName),
        typeof(string),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PickAppAutomationNameProperty = DependencyProperty.Register(
        nameof(PickAppAutomationName),
        typeof(string),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ButtonRowTopMarginProperty = DependencyProperty.Register(
        nameof(ButtonRowTopMargin),
        typeof(Thickness),
        typeof(AppTargetPickerControl),
        new PropertyMetadata(new Thickness(0, 8, 0, 0)));

    public static readonly RoutedEvent BrowseRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(BrowseRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<AppTargetPickerActionEventArgs>),
        typeof(AppTargetPickerControl));

    public static readonly RoutedEvent RunningAppRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(RunningAppRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<AppTargetPickerActionEventArgs>),
        typeof(AppTargetPickerControl));

    public static readonly RoutedEvent PackagedAppRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(PackagedAppRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<AppTargetPickerActionEventArgs>),
        typeof(AppTargetPickerControl));

    public AppTargetPickerControl()
    {
        InitializeComponent();
    }

    public string TargetPath
    {
        get => (string)GetValue(TargetPathProperty);
        set => SetValue(TargetPathProperty, value);
    }

    public string ResolvedAppName
    {
        get => (string)GetValue(ResolvedAppNameProperty);
        set => SetValue(ResolvedAppNameProperty, value);
    }

    public bool HasResolvedApp
    {
        get => (bool)GetValue(HasResolvedAppProperty);
        set => SetValue(HasResolvedAppProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public string InputAutomationName
    {
        get => (string)GetValue(InputAutomationNameProperty);
        set => SetValue(InputAutomationNameProperty, value);
    }

    public string InputHelpText
    {
        get => (string)GetValue(InputHelpTextProperty);
        set => SetValue(InputHelpTextProperty, value);
    }

    public string BrowseAutomationName
    {
        get => (string)GetValue(BrowseAutomationNameProperty);
        set => SetValue(BrowseAutomationNameProperty, value);
    }

    public string RunningAppsAutomationName
    {
        get => (string)GetValue(RunningAppsAutomationNameProperty);
        set => SetValue(RunningAppsAutomationNameProperty, value);
    }

    public string PickAppAutomationName
    {
        get => (string)GetValue(PickAppAutomationNameProperty);
        set => SetValue(PickAppAutomationNameProperty, value);
    }

    public Thickness ButtonRowTopMargin
    {
        get => (Thickness)GetValue(ButtonRowTopMarginProperty);
        set => SetValue(ButtonRowTopMarginProperty, value);
    }

    public event EventHandler<AppTargetPickerActionEventArgs> BrowseRequested
    {
        add => AddHandler(BrowseRequestedEvent, value);
        remove => RemoveHandler(BrowseRequestedEvent, value);
    }

    public event EventHandler<AppTargetPickerActionEventArgs> RunningAppRequested
    {
        add => AddHandler(RunningAppRequestedEvent, value);
        remove => RemoveHandler(RunningAppRequestedEvent, value);
    }

    public event EventHandler<AppTargetPickerActionEventArgs> PackagedAppRequested
    {
        add => AddHandler(PackagedAppRequestedEvent, value);
        remove => RemoveHandler(PackagedAppRequestedEvent, value);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) RaiseEvent(new AppTargetPickerActionEventArgs(BrowseRequestedEvent, button));
    }

    private void RunningAppsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) RaiseEvent(new AppTargetPickerActionEventArgs(RunningAppRequestedEvent, button));
    }

    private void PickAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) RaiseEvent(new AppTargetPickerActionEventArgs(PackagedAppRequestedEvent, button));
    }

    private void ClearTargetButton_Click(object sender, RoutedEventArgs e)
    {
        TargetPath = string.Empty;
        TargetTextBox.Focus();
        e.Handled = true;
    }
}

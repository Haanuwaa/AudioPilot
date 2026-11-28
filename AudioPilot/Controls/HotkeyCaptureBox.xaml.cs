using System.Windows;
using System.Windows.Controls;
using AudioPilot.Localization;
using AudioPilot.ViewModels;

namespace AudioPilot.Controls;

public partial class HotkeyCaptureBox : UserControl
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey),
        typeof(HotkeyViewModel),
        typeof(HotkeyCaptureBox),
        new PropertyMetadata(null));

    public static readonly DependencyProperty FieldNameProperty = DependencyProperty.Register(
        nameof(FieldName),
        typeof(string),
        typeof(HotkeyCaptureBox),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FieldHeightProperty = DependencyProperty.Register(
        nameof(FieldHeight),
        typeof(double),
        typeof(HotkeyCaptureBox),
        new PropertyMetadata(double.NaN));

    public HotkeyCaptureBox()
    {
        InitializeComponent();
        // Local null suppresses WPF's default TextBox editor menu while still letting ContextMenuOpening lazily build our Delete menu.
        CaptureTextBox.ContextMenu = null;
    }

    public HotkeyViewModel? Hotkey
    {
        get => (HotkeyViewModel?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public string FieldName
    {
        get => (string)GetValue(FieldNameProperty);
        set => SetValue(FieldNameProperty, value);
    }

    public double FieldHeight
    {
        get => (double)GetValue(FieldHeightProperty);
        set => SetValue(FieldHeightProperty, value);
    }

    private void CaptureTextBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (CaptureTextBox.ContextMenu != null) return;
        var menuItem = new MenuItem { InputGestureText = "Del" };
        menuItem.Click += ClearHotkeyButton_Click;
        LocalizationContext.SetText(menuItem, HeaderedItemsControl.HeaderProperty, new("Ui_Delete"));
        menuItem.SetResourceReference(MenuItem.IconProperty, "AppMenuDeleteIcon");
        menuItem.SetResourceReference(StyleProperty, "AppContextMenuItemStyle");
        var contextMenu = new ContextMenu();
        contextMenu.SetResourceReference(StyleProperty, "AppContextMenuStyle");
        contextMenu.Items.Add(menuItem);
        CaptureTextBox.ContextMenu = contextMenu;
    }

    private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        Hotkey?.Reset();
        CaptureTextBox.SelectionLength = 0;
        CaptureTextBox.SelectionStart = CaptureTextBox.Text.Length;
        e.Handled = true;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class HoverInfoPopupBehavior : Behavior<FrameworkElement>
    {
        private static readonly Logger _logger = Logger.Instance;
        private static InfoPopupService PopupService => InfoPopupService.Instance;
        private CancellationTokenSource? _hoverDelayCts;
        private DisabledToolTipSupport? _disabledToolTip;
        private bool _keyboardHelp;

        public string? Text
        {
            get => (string?)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(HoverInfoPopupBehavior), new PropertyMetadata(string.Empty,
                static (sender, _) => ((HoverInfoPopupBehavior)sender).OnTextChanged()));

        public HotkeyWarningKind WarningKind
        {
            get => (HotkeyWarningKind)GetValue(WarningKindProperty);
            set => SetValue(WarningKindProperty, value);
        }

        public static readonly DependencyProperty WarningKindProperty =
            DependencyProperty.Register(nameof(WarningKind), typeof(HotkeyWarningKind), typeof(HoverInfoPopupBehavior), new PropertyMetadata(HotkeyWarningKind.None));

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.MouseEnter += OnMouseEnter;
            AssociatedObject.MouseLeave += OnMouseLeave;
            AssociatedObject.MouseMove += OnMouseMove;
            AssociatedObject.LostMouseCapture += OnLostMouseCapture;
            AssociatedObject.LostKeyboardFocus += OnLostKeyboardFocus;
            AssociatedObject.GotKeyboardFocus += OnGotKeyboardFocus;
            AssociatedObject.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
            AssociatedObject.IsEnabledChanged += OnIsEnabledChanged;

            if (AssociatedObject is ComboBox comboBox)
            {
                comboBox.DropDownOpened += OnComboBoxDropDownOpened;
                comboBox.DropDownClosed += OnComboBoxDropDownClosed;
            }

            AssociatedObject.Unloaded += OnUnloaded;
            if (AssociatedObject.ReadLocalValue(AutomationProperties.HelpTextProperty) == DependencyProperty.UnsetValue
                && string.IsNullOrEmpty(AutomationProperties.GetHelpText(AssociatedObject)))
                BindingOperations.SetBinding(AssociatedObject, AutomationProperties.HelpTextProperty, new Binding(nameof(Text)) { Source = this });
            UpdateDisabledToolTip();
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);

            AssociatedObject.MouseEnter -= OnMouseEnter;
            AssociatedObject.MouseLeave -= OnMouseLeave;
            AssociatedObject.MouseMove -= OnMouseMove;
            AssociatedObject.LostMouseCapture -= OnLostMouseCapture;
            AssociatedObject.LostKeyboardFocus -= OnLostKeyboardFocus;
            AssociatedObject.GotKeyboardFocus -= OnGotKeyboardFocus;
            AssociatedObject.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
            AssociatedObject.IsEnabledChanged -= OnIsEnabledChanged;

            if (AssociatedObject is ComboBox comboBox)
            {
                comboBox.DropDownOpened -= OnComboBoxDropDownOpened;
                comboBox.DropDownClosed -= OnComboBoxDropDownClosed;
            }

            AssociatedObject.Unloaded -= OnUnloaded;

            _disabledToolTip?.Dispose();
            _disabledToolTip = null;
            PopupService.Hide(AssociatedObject);
            if (ReferenceEquals(BindingOperations.GetBinding(AssociatedObject, AutomationProperties.HelpTextProperty)?.Source, this))
                BindingOperations.ClearBinding(AssociatedObject, AutomationProperties.HelpTextProperty);
        }

        private async void OnMouseEnter(object sender, MouseEventArgs e)
        {
            _keyboardHelp = false;
            if (!AssociatedObject.IsEnabled || string.IsNullOrWhiteSpace(Text))
            {
                return;
            }

            CancellationToken hoverDelayToken = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);

            await HoverDelayCoordinator.ExecuteAfterDelayAsync(
                AppConstants.Timing.TooltipHoverDelayMs,
                () => AssociatedObject.IsEnabled && AssociatedObject.IsMouseOver,
                ShowOrUpdatePopup,
                ex => _logger.Warning("HoverInfoPopupBehavior", "Failed to process hover info popup", nameof(OnMouseEnter), ex),
                hoverDelayToken);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!PopupService.IsActiveFor(AssociatedObject) || string.IsNullOrWhiteSpace(Text))
            {
                return;
            }

            _keyboardHelp = false;
            ShowOrUpdatePopup();
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            HidePopup();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            HidePopup();
        }

        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            HidePopup();
        }

        private void UpdateDisabledToolTip()
        {
            if (AssociatedObject == null) return;
            if (string.IsNullOrWhiteSpace(Text))
            {
                _disabledToolTip?.Dispose();
                _disabledToolTip = null;
                return;
            }
            _disabledToolTip ??= new DisabledToolTipSupport(AssociatedObject, () => Text);
            _disabledToolTip.Refresh();
        }

        private void OnTextChanged()
        {
            if (AssociatedObject == null) return;
            UpdateDisabledToolTip();
            if (string.IsNullOrWhiteSpace(Text)) HidePopup();
            else if (PopupService.IsActiveFor(AssociatedObject)) PopupService.UpdateText(Text, WarningKind);
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            HidePopup();
        }

        private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            HidePopup();
        }

        private async void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice || string.IsNullOrWhiteSpace(Text)) return;
            _keyboardHelp = true;
            CancellationToken token = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);
            await HoverDelayCoordinator.ExecuteAfterDelayAsync(AppConstants.Timing.TooltipHoverDelayMs,
                () => AssociatedObject.IsEnabled && AssociatedObject.IsKeyboardFocusWithin && _keyboardHelp
                    && InputManager.Current.MostRecentInputDevice is KeyboardDevice,
                ShowOrUpdatePopup,
                ex => _logger.Warning("HoverInfoPopupBehavior", "Keyboard help could not be shown", nameof(OnGotKeyboardFocus), ex), token);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool dismiss = e.Key == Key.Escape && _keyboardHelp && PopupService.IsActiveFor(AssociatedObject);
            HidePopup();
            if (dismiss && !Interaction.GetBehaviors(AssociatedObject).OfType<HotkeyCaptureBehavior>().Any()) e.Handled = true;
        }

        private void OnComboBoxDropDownOpened(object? sender, EventArgs e)
        {
            HidePopup();
        }

        private void OnComboBoxDropDownClosed(object? sender, EventArgs e)
        {
            HidePopup();
        }

        private void HidePopup()
        {
            _keyboardHelp = false;
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            PopupService.Hide(AssociatedObject);
        }

        private void ShowOrUpdatePopup()
        {
            if (!PopupService.IsActiveFor(AssociatedObject))
            {
                PopupService.ShowText(AssociatedObject, Text!, WarningKind);
            }
            else
            {
                PopupService.UpdateText(Text!, WarningKind);
            }

            if (!PopupService.IsActiveFor(AssociatedObject)) return;

            var position = _keyboardHelp ? new Point(0, AssociatedObject.ActualHeight + 6) : Mouse.GetPosition(AssociatedObject) + new Vector(12, 12);
            PopupService.UpdatePosition(position.X, position.Y);
        }
    }
}

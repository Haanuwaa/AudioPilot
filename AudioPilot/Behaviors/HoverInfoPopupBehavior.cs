using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Localization;
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

        public bool ShowOnlyWhenClipped
        {
            get => (bool)GetValue(ShowOnlyWhenClippedProperty);
            set => SetValue(ShowOnlyWhenClippedProperty, value);
        }

        public static readonly DependencyProperty ShowOnlyWhenClippedProperty =
            DependencyProperty.Register(nameof(ShowOnlyWhenClipped), typeof(bool), typeof(HoverInfoPopupBehavior), new PropertyMetadata(false,
                static (sender, _) => ((HoverInfoPopupBehavior)sender).OnTextChanged()));

        private bool HasHelpText => !string.IsNullOrWhiteSpace(Text)
            && (!ShowOnlyWhenClipped || TrimmedTextPopupBehavior.IsTextTrimmed(AssociatedObject));

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
            // Behaviors are outside the logical tree, so forward the owner's localization scope.
            if (LocalizedBinding.IsLocalizedBinding(this, TextProperty)
                && ReadLocalValue(LocalizationContext.ServiceProperty) == DependencyProperty.UnsetValue)
                BindingOperations.SetBinding(this, LocalizationContext.ServiceProperty,
                    new Binding { Source = AssociatedObject, Path = new PropertyPath("(0)", LocalizationContext.ServiceProperty) });
            AssociatedObject.MouseEnter += OnMouseEnter;
            AssociatedObject.MouseLeave += OnMouseLeave;
            AssociatedObject.MouseMove += OnMouseMove;
            AssociatedObject.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), handledEventsToo: true);
            AssociatedObject.LostMouseCapture += OnLostMouseCapture;
            AssociatedObject.LostKeyboardFocus += OnLostKeyboardFocus;
            AssociatedObject.GotKeyboardFocus += OnGotKeyboardFocus;
            AssociatedObject.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
            AssociatedObject.IsEnabledChanged += OnIsEnabledChanged;
            AssociatedObject.SizeChanged += OnSizeChanged;

            if (AssociatedObject is ComboBox comboBox)
            {
                comboBox.DropDownOpened += OnComboBoxDropDownOpened;
                comboBox.DropDownClosed += OnComboBoxDropDownClosed;
                comboBox.SelectionChanged += OnComboBoxSelectionChanged;
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
            if (ReferenceEquals(BindingOperations.GetBinding(this, LocalizationContext.ServiceProperty)?.Source, AssociatedObject))
                BindingOperations.ClearBinding(this, LocalizationContext.ServiceProperty);
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);

            AssociatedObject.MouseEnter -= OnMouseEnter;
            AssociatedObject.MouseLeave -= OnMouseLeave;
            AssociatedObject.MouseMove -= OnMouseMove;
            AssociatedObject.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown));
            AssociatedObject.LostMouseCapture -= OnLostMouseCapture;
            AssociatedObject.LostKeyboardFocus -= OnLostKeyboardFocus;
            AssociatedObject.GotKeyboardFocus -= OnGotKeyboardFocus;
            AssociatedObject.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
            AssociatedObject.IsEnabledChanged -= OnIsEnabledChanged;
            AssociatedObject.SizeChanged -= OnSizeChanged;

            if (AssociatedObject is ComboBox comboBox)
            {
                comboBox.DropDownOpened -= OnComboBoxDropDownOpened;
                comboBox.DropDownClosed -= OnComboBoxDropDownClosed;
                comboBox.SelectionChanged -= OnComboBoxSelectionChanged;
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
            if (!AssociatedObject.IsEnabled || !HasHelpText)
            {
                return;
            }

            CancellationToken hoverDelayToken = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);

            await HoverDelayCoordinator.ExecuteAfterDelayAsync(
                AssociatedObject, AppConstants.Timing.TooltipHoverDelayMs,
                () => AssociatedObject.IsEnabled && AssociatedObject.IsMouseOver && HasHelpText,
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

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => HidePopup();

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
            if (!HasHelpText)
            {
                _disabledToolTip?.Dispose();
                _disabledToolTip = null;
                return;
            }
            _disabledToolTip ??= new DisabledToolTipSupport(AssociatedObject, () => HasHelpText ? Text : null);
            _disabledToolTip.Refresh();
        }

        private void OnTextChanged()
        {
            if (AssociatedObject == null) return;
            UpdateDisabledToolTip();
            if (!HasHelpText) HidePopup();
            else if (PopupService.IsActiveFor(AssociatedObject)) PopupService.UpdateText(Text!, WarningKind);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ShowOnlyWhenClipped) OnTextChanged();
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
            if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice || !HasHelpText) return;
            _keyboardHelp = true;
            CancellationToken token = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);
            await HoverDelayCoordinator.ExecuteAfterDelayAsync(AssociatedObject, AppConstants.Timing.TooltipHoverDelayMs,
                () => AssociatedObject.IsEnabled && AssociatedObject.IsKeyboardFocusWithin && _keyboardHelp
                    && InputManager.Current.MostRecentInputDevice is KeyboardDevice && HasHelpText,
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

        private void OnComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ShowOnlyWhenClipped) return;
            HidePopup();
            _disabledToolTip?.Dispose();
            _disabledToolTip = null;
            UpdateDisabledToolTip();
        }

        private void HidePopup()
        {
            _keyboardHelp = false;
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            PopupService.Hide(AssociatedObject);
        }

        private void ShowOrUpdatePopup()
        {
            if (!HasHelpText || Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed)
            {
                HidePopup();
                return;
            }

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

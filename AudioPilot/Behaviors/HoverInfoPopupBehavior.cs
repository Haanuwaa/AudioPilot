using System.Windows;
using System.Windows.Controls;
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

        public string? Text
        {
            get => (string?)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(HoverInfoPopupBehavior), new PropertyMetadata(string.Empty));

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

            if (AssociatedObject is ComboBox comboBox)
            {
                comboBox.DropDownOpened += OnComboBoxDropDownOpened;
                comboBox.DropDownClosed += OnComboBoxDropDownClosed;
            }

            AssociatedObject.Unloaded += OnUnloaded;
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

            if (AssociatedObject is ComboBox comboBox)
            {
                comboBox.DropDownOpened -= OnComboBoxDropDownOpened;
                comboBox.DropDownClosed -= OnComboBoxDropDownClosed;
            }

            AssociatedObject.Unloaded -= OnUnloaded;

            PopupService.Hide(AssociatedObject);
        }

        private async void OnMouseEnter(object sender, MouseEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Text))
            {
                return;
            }

            CancellationToken hoverDelayToken = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);

            await HoverDelayCoordinator.ExecuteAfterDelayAsync(
                AppConstants.Timing.TooltipHoverDelayMs,
                () => AssociatedObject.IsMouseOver,
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

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            HidePopup();
        }

        private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (AssociatedObject is ComboBox)
            {
                HidePopup();
            }
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

            var position = Mouse.GetPosition(AssociatedObject);
            PopupService.UpdatePosition(position.X + 12, position.Y + 12);
        }
    }
}

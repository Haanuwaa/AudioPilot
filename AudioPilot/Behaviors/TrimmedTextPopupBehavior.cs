using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class TrimmedTextPopupBehavior : Behavior<FrameworkElement>
    {
        private static readonly Logger _logger = Logger.Instance;
        private static InfoPopupService PopupService => InfoPopupService.Instance;
        private CancellationTokenSource? _hoverDelayCts;
        private DisabledToolTipSupport? _disabledToolTip;

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.MouseEnter += OnMouseEnter;
            AssociatedObject.MouseLeave += OnMouseLeave;
            AssociatedObject.MouseMove += OnMouseMove;
            AssociatedObject.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), handledEventsToo: true);
            AssociatedObject.LostMouseCapture += OnLostMouseCapture;
            AssociatedObject.LostKeyboardFocus += OnLostKeyboardFocus;
            AssociatedObject.Unloaded += OnUnloaded;
            AssociatedObject.IsEnabledChanged += OnIsEnabledChanged;
            _disabledToolTip = new DisabledToolTipSupport(AssociatedObject, () => TryGetTrimmedText(out string text) ? text : null);
            _disabledToolTip.Refresh();
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            HoverDelayCoordinator.CancelAndDispose(ref _hoverDelayCts);
            AssociatedObject.MouseEnter -= OnMouseEnter;
            AssociatedObject.MouseLeave -= OnMouseLeave;
            AssociatedObject.MouseMove -= OnMouseMove;
            AssociatedObject.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown));
            AssociatedObject.LostMouseCapture -= OnLostMouseCapture;
            AssociatedObject.LostKeyboardFocus -= OnLostKeyboardFocus;
            AssociatedObject.Unloaded -= OnUnloaded;
            AssociatedObject.IsEnabledChanged -= OnIsEnabledChanged;
            _disabledToolTip?.Dispose();
            _disabledToolTip = null;
            PopupService.Hide(AssociatedObject);
        }

        private async void OnMouseEnter(object sender, MouseEventArgs e)
        {
            CancellationToken hoverDelayToken = HoverDelayCoordinator.StartOrRestart(ref _hoverDelayCts);

            await HoverDelayCoordinator.ExecuteAfterDelayAsync(
                AppConstants.Timing.TooltipHoverDelayMs,
                () => AssociatedObject.IsEnabled && AssociatedObject.IsMouseOver && TryGetTrimmedText(out _),
                ShowOrUpdatePopup,
                ex => _logger.Warning("TrimmedTextPopupBehavior", "Failed to process trimmed-text hover popup", nameof(OnMouseEnter), ex),
                hoverDelayToken);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!PopupService.IsActiveFor(AssociatedObject))
            {
                return;
            }

            if (!TryGetTrimmedText(out _))
            {
                HidePopup();
                return;
            }

            ShowOrUpdatePopup();
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            HidePopup();
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => HidePopup();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            HidePopup();
        }

        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => HidePopup();

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            HidePopup();
        }

        private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
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
            if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed)
            {
                HidePopup();
                return;
            }

            if (!TryGetTrimmedText(out string text))
            {
                return;
            }

            if (!PopupService.IsActiveFor(AssociatedObject))
            {
                PopupService.ShowText(AssociatedObject, text);
            }
            else
            {
                PopupService.UpdateText(text, HotkeyWarningKind.None);
            }

            Point position = Mouse.GetPosition(AssociatedObject);
            PopupService.UpdatePosition(position.X + 12, position.Y + 12);
        }

        private bool TryGetTrimmedText(out string text)
        {
            text = string.Empty;
            TextBlock? textBlock = ResolveTextBlock(AssociatedObject);
            if (textBlock == null || !IsTextTrimmed(textBlock))
            {
                return false;
            }

            text = textBlock.Text.Trim();
            return !string.IsNullOrWhiteSpace(text);
        }

        private static TextBlock? ResolveTextBlock(FrameworkElement root)
        {
            if (root is TextBlock textBlock)
            {
                return textBlock;
            }

            if (root is ContentControl { Content: TextBlock contentTextBlock })
            {
                return contentTextBlock;
            }

            return FindVisualChild<TextBlock>(root);
        }

        private static T? FindVisualChild<T>(DependencyObject root)
            where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is T match)
                {
                    return match;
                }

                T? nestedMatch = FindVisualChild<T>(child);
                if (nestedMatch != null)
                {
                    return nestedMatch;
                }
            }

            return null;
        }

        internal static bool IsTextTrimmed(TextBlock textBlock)
        {
            ArgumentNullException.ThrowIfNull(textBlock);

            string text = textBlock.Text.TrimEnd();
            if (text.Length == 0
                || textBlock.TextTrimming == TextTrimming.None
                || textBlock.TextWrapping != TextWrapping.NoWrap
                || textBlock.ActualWidth <= 0)
            {
                return false;
            }

            double availableWidth = textBlock.ActualWidth - textBlock.Padding.Left - textBlock.Padding.Right;
            if (availableWidth <= 0)
            {
                return true;
            }

            Typeface typeface = new(
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch);

            DpiScale dpi = VisualTreeHelper.GetDpi(textBlock);
            System.Globalization.CultureInfo culture;
            try
            {
                culture = textBlock.Language.GetSpecificCulture();
            }
            catch (InvalidOperationException)
            {
                culture = System.Globalization.CultureInfo.CurrentCulture;
            }

            FormattedText formattedText = new(
                text,
                culture,
                textBlock.FlowDirection,
                typeface,
                textBlock.FontSize,
                textBlock.Foreground,
                dpi.PixelsPerDip);

            return ExceedsAvailableWidth(formattedText.WidthIncludingTrailingWhitespace, availableWidth, dpi.DpiScaleX);
        }

        internal static bool ExceedsAvailableWidth(double textWidth, double availableWidth, double dpiScaleX)
        {
            if (!double.IsFinite(textWidth)
                || !double.IsFinite(availableWidth)
                || textWidth <= 0
                || availableWidth < 0)
            {
                return false;
            }

            double effectiveDpiScale = double.IsFinite(dpiScaleX) && dpiScaleX > 0 ? dpiScaleX : 1d;
            double layoutTolerance = 1d / effectiveDpiScale;
            return textWidth - availableWidth > layoutTolerance;
        }
    }
}

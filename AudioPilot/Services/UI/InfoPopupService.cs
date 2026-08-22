using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot.Services.UI
{
    public sealed class InfoPopupService
    {
        private static readonly Lazy<InfoPopupService> _instance = new(() => new InfoPopupService());
        public static InfoPopupService Instance => _instance.Value;

        private readonly Popup _popup;
        private readonly TextBlock _valueText;
        private readonly TextBlock _labelText;
        private readonly Border _container;
        private readonly ILogger _logger;
        private FrameworkElement? _currentTarget;
        private Window? _currentWindow;
        private HotkeyWarningKind _currentWarningKind;

        private InfoPopupService() : this(Logger.Instance)
        {
        }

        internal InfoPopupService(ILogger logger)
        {
            _logger = logger;
            _valueText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300
            };

            _labelText = new TextBlock
            {
                Foreground = Brushes.White,
                Text = "Volume: "
            };

            _container = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                MaxWidth = 320,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 6,
                    ShadowDepth = 0,
                    Opacity = 0.4,
                    Color = Colors.Black
                },
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        _labelText,
                        _valueText
                    }
                }
            };

            _container.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            _container.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            _valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            _popup = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.Relative,
                StaysOpen = true,
                IsHitTestVisible = false,
                Child = _container
            };
        }

        public void Show(FrameworkElement target, double value, double horizontalOffset, double verticalOffset)
        {
            AttachTarget(target);
            _popup.PlacementTarget = target;
            _popup.Placement = PlacementMode.Relative;
            _popup.HorizontalOffset = horizontalOffset;
            _popup.VerticalOffset = verticalOffset;

            _labelText.Visibility = Visibility.Visible;
            _valueText.Text = $"{value:F1}";

            _container.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            _container.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            _valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _currentWarningKind = HotkeyWarningKind.None;

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("InfoPopupService", () => $"info-popup-show | mode=value target={target.GetType().Name}");
            }
            _popup.IsOpen = true;
        }

        public void ShowText(FrameworkElement target, string text)
        {
            ShowText(target, text, HotkeyWarningKind.None);
        }

        public void ShowText(FrameworkElement target, string text, HotkeyWarningKind warningKind)
        {
            AttachTarget(target);
            _popup.PlacementTarget = target;
            _popup.Placement = PlacementMode.Relative;

            var mousePosition = Mouse.GetPosition(target);
            _popup.HorizontalOffset = mousePosition.X + 10;
            _popup.VerticalOffset = mousePosition.Y + 10;

            _labelText.Visibility = Visibility.Collapsed;
            _valueText.Text = text;

            ApplyTextAppearance(target, warningKind);
            _currentWarningKind = warningKind;

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("InfoPopupService", () => $"info-popup-show | mode=text warning={warningKind} target={target.GetType().Name}");
            }
            _popup.IsOpen = true;
        }

        public void UpdateText(string text, HotkeyWarningKind warningKind)
        {
            if (_currentTarget is null)
            {
                return;
            }

            if (!string.Equals(_valueText.Text, text, StringComparison.Ordinal))
            {
                _valueText.Text = text;
            }

            if (_currentWarningKind != warningKind)
            {
                ApplyTextAppearance(_currentTarget, warningKind);
                _currentWarningKind = warningKind;
            }
        }

        public void UpdatePosition(double horizontalOffset, double verticalOffset)
        {
            if (Math.Abs(_popup.HorizontalOffset - horizontalOffset) >= 0.25)
            {
                _popup.HorizontalOffset = horizontalOffset;
            }

            if (Math.Abs(_popup.VerticalOffset - verticalOffset) >= 0.25)
            {
                _popup.VerticalOffset = verticalOffset;
            }
        }

        public void UpdateValue(double value)
        {
            string formattedValue = $"{value:F1}";
            if (!string.Equals(_valueText.Text, formattedValue, StringComparison.Ordinal))
            {
                _valueText.Text = formattedValue;
            }
        }

        public void Hide(FrameworkElement target)
        {
            if (_currentTarget == target)
            {
                HideCurrentPopup("explicit-hide");
            }
        }

        public bool IsActiveFor(FrameworkElement target)
        {
            if (_currentTarget != target || !_popup.IsOpen)
            {
                return false;
            }

            if (!target.IsVisible ||
                !target.IsEnabled ||
                _currentWindow?.IsVisible == false ||
                _currentWindow?.WindowState == WindowState.Minimized)
            {
                HideCurrentPopup("inactive-target-state");
                return false;
            }

            return true;
        }

        private void AttachTarget(FrameworkElement target)
        {
            if (!ReferenceEquals(_currentTarget, target))
            {
                DetachTarget();
                _currentTarget = target;

                _currentTarget.Unloaded += OnCurrentTargetUnloaded;
                _currentTarget.IsVisibleChanged += OnCurrentTargetIsVisibleChanged;
                _currentTarget.IsEnabledChanged += OnCurrentTargetIsEnabledChanged;

                _currentWindow = Window.GetWindow(target);
                if (_currentWindow != null)
                {
                    _currentWindow.Deactivated += OnCurrentWindowDeactivated;
                    _currentWindow.IsVisibleChanged += OnCurrentWindowIsVisibleChanged;
                    _currentWindow.StateChanged += OnCurrentWindowStateChanged;
                    _currentWindow.Closed += OnCurrentWindowClosed;
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("InfoPopupService", () => $"info-popup-target-attached | target={target.GetType().Name} window={_currentWindow?.GetType().Name ?? "none"}");
                }
            }
        }

        private void DetachTarget()
        {
            FrameworkElement? target = _currentTarget;
            Window? window = _currentWindow;
            if (_currentTarget != null)
            {
                _currentTarget.Unloaded -= OnCurrentTargetUnloaded;
                _currentTarget.IsVisibleChanged -= OnCurrentTargetIsVisibleChanged;
                _currentTarget.IsEnabledChanged -= OnCurrentTargetIsEnabledChanged;
            }

            if (_currentWindow != null)
            {
                _currentWindow.Deactivated -= OnCurrentWindowDeactivated;
                _currentWindow.IsVisibleChanged -= OnCurrentWindowIsVisibleChanged;
                _currentWindow.StateChanged -= OnCurrentWindowStateChanged;
                _currentWindow.Closed -= OnCurrentWindowClosed;
            }

            _currentWindow = null;
            _currentTarget = null;

            if (target != null && _logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("InfoPopupService", () => $"info-popup-target-detached | target={target.GetType().Name} window={window?.GetType().Name ?? "none"}");
            }
        }

        private void HideCurrentPopup(string reason)
        {
            bool hadActivePopup = _popup.IsOpen || _popup.PlacementTarget != null || _currentTarget != null || _currentWindow != null;
            if (!hadActivePopup)
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("InfoPopupService", () => $"info-popup-hide | reason={reason}");
            }

            _popup.IsOpen = false;
            _popup.PlacementTarget = null;
            _currentWarningKind = HotkeyWarningKind.None;
            DetachTarget();
        }

        private void OnCurrentTargetUnloaded(object sender, RoutedEventArgs e)
        {
            HideCurrentPopup("target-unloaded");
        }

        private void OnCurrentTargetIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true)
            {
                HideCurrentPopup("target-hidden");
            }
        }

        private void OnCurrentTargetIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true)
            {
                HideCurrentPopup("target-disabled");
            }
        }

        private void OnCurrentWindowDeactivated(object? sender, EventArgs e)
        {
            HideCurrentPopup("window-deactivated");
        }

        private void OnCurrentWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true)
            {
                HideCurrentPopup("window-hidden");
            }
        }

        private void OnCurrentWindowStateChanged(object? sender, EventArgs e)
        {
            if (_currentWindow?.WindowState == WindowState.Minimized)
            {
                HideCurrentPopup("window-minimized");
            }
        }

        private void OnCurrentWindowClosed(object? sender, EventArgs e)
        {
            HideCurrentPopup("window-closed");
        }

        private void ApplyTextAppearance(FrameworkElement target, HotkeyWarningKind warningKind)
        {
            _container.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            _container.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            _valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            string? brushKey = warningKind switch
            {
                HotkeyWarningKind.Duplicate or HotkeyWarningKind.ExternalConflict => "HotkeyConflictBrush",
                HotkeyWarningKind.Reserved => "HotkeyReservedBrush",
                HotkeyWarningKind.Fallback => "HotkeyFallbackBrush",
                _ => null,
            };

            if (brushKey is null)
            {
                return;
            }

            if (target.TryFindResource(brushKey) is Brush warningBrush)
            {
                _valueText.Foreground = warningBrush;
            }
        }
    }
}

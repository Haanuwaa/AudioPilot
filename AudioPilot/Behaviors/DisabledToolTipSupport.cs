using System.Windows;
using System.Windows.Controls;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Behaviors
{
    /// <summary>Provides themed, read-only hover content through WPF's disabled-control hit testing.</summary>
    internal sealed class DisabledToolTipSupport : IDisposable
    {
        private readonly FrameworkElement _target;
        private readonly Func<string?> _getText;
        private ToolTip? _toolTip;
        private List<(DependencyProperty Property, object Value)>? _overrides;
        private bool _unloaded;
        private bool _disposed;

        internal DisabledToolTipSupport(FrameworkElement target, Func<string?> getText)
        {
            _target = target;
            _getText = getText;
            target.IsEnabledChanged += OnIsEnabledChanged;
            target.IsVisibleChanged += OnIsVisibleChanged;
            target.Loaded += OnLoaded;
            target.Unloaded += OnUnloaded;
            target.ToolTipOpening += OnToolTipOpening;
        }

        internal bool Refresh()
        {
            if (_disposed || _unloaded || _target.IsEnabled)
            {
                RemoveToolTip();
                return false;
            }

            if (_toolTip == null)
            {
                if (_target.ReadLocalValue(FrameworkElement.ToolTipProperty) != DependencyProperty.UnsetValue || _target.ToolTip != null) return false;
                _toolTip = new ToolTip
                {
                    Focusable = false,
                    IsHitTestVisible = false,
                    Content = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 },
                };
                SetDefault(ToolTipService.ShowOnDisabledProperty, true);
                SetDefault(ToolTipService.InitialShowDelayProperty, AppConstants.Timing.TooltipHoverDelayMs);
                SetDefault(ToolTipService.ShowDurationProperty, int.MaxValue);
                _target.ToolTip = _toolTip;
            }

            if (!ReferenceEquals(_target.ToolTip, _toolTip)) return false;
            string? text = null;
            try
            {
                text = _getText();
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning("DisabledToolTipSupport", "disabled-tooltip-content-failed", nameof(Refresh), ex);
            }
            ((TextBlock)_toolTip.Content).Text = text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text)) return true;
            _toolTip.IsOpen = false;
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _target.IsEnabledChanged -= OnIsEnabledChanged;
            _target.IsVisibleChanged -= OnIsVisibleChanged;
            _target.Loaded -= OnLoaded;
            _target.Unloaded -= OnUnloaded;
            _target.ToolTipOpening -= OnToolTipOpening;
            RemoveToolTip();
        }

        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true) Refresh();
            else RemoveToolTip();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _unloaded = false;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _unloaded = true;
            RemoveToolTip();
        }

        private void OnToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (_toolTip != null && ReferenceEquals(e.OriginalSource, _target) && ReferenceEquals(_target.ToolTip, _toolTip) && !Refresh())
            {
                e.Handled = true;
            }
        }

        private void SetDefault(DependencyProperty property, object value)
        {
            if (_target.ReadLocalValue(property) != DependencyProperty.UnsetValue) return;
            _target.SetValue(property, value);
            (_overrides ??= []).Add((property, value));
        }

        private void RemoveToolTip()
        {
            if (_toolTip == null) return;
            _toolTip.IsOpen = false;
            if (ReferenceEquals(_target.ToolTip, _toolTip)) _target.ClearValue(FrameworkElement.ToolTipProperty);
            _toolTip = null;
            if (_overrides == null) return;
            foreach (var (property, value) in _overrides)
            {
                if (Equals(_target.ReadLocalValue(property), value)) _target.ClearValue(property);
            }
            _overrides.Clear();
        }
    }
}

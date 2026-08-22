using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class HotkeyWarningBorderBehavior : Behavior<TextBox>
    {
        public bool HasWarning
        {
            get => (bool)GetValue(HasWarningProperty);
            set => SetValue(HasWarningProperty, value);
        }

        public static readonly DependencyProperty HasWarningProperty =
            DependencyProperty.Register(
                nameof(HasWarning),
                typeof(bool),
                typeof(HotkeyWarningBorderBehavior),
                new PropertyMetadata(false, OnWarningStateChanged));

        public HotkeyWarningKind WarningKind
        {
            get => (HotkeyWarningKind)GetValue(WarningKindProperty);
            set => SetValue(WarningKindProperty, value);
        }

        public static readonly DependencyProperty WarningKindProperty =
            DependencyProperty.Register(
                nameof(WarningKind),
                typeof(HotkeyWarningKind),
                typeof(HotkeyWarningBorderBehavior),
                new PropertyMetadata(HotkeyWarningKind.None, OnWarningStateChanged));

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.GotKeyboardFocus += OnFocusChanged;
            AssociatedObject.LostKeyboardFocus += OnFocusChanged;
            ApplyBorderState();
        }

        protected override void OnDetaching()
        {
            ClearLocalBorderOverride();
            AssociatedObject.GotKeyboardFocus -= OnFocusChanged;
            AssociatedObject.LostKeyboardFocus -= OnFocusChanged;
            base.OnDetaching();
        }

        private static void OnWarningStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HotkeyWarningBorderBehavior behavior)
            {
                behavior.ApplyBorderState();
            }
        }

        private void OnFocusChanged(object sender, RoutedEventArgs e)
        {
            ApplyBorderState();
        }

        private void ApplyBorderState()
        {
            if (AssociatedObject == null)
            {
                return;
            }

            if (HasWarning)
            {
                if (TryResolveWarningBrush(out Brush? warningBrush))
                {
                    AssociatedObject.BorderBrush = warningBrush;
                }
                else
                {
                    AssociatedObject.BorderBrush = Brushes.Transparent;
                }

                return;
            }

            ClearLocalBorderOverride();
        }

        private void ClearLocalBorderOverride()
        {
            AssociatedObject?.ClearValue(Control.BorderBrushProperty);
        }

        private bool TryResolveWarningBrush(out Brush? brush)
        {
            brush = null;
            if (AssociatedObject == null)
            {
                return false;
            }

            string? resourceKey = WarningKind switch
            {
                HotkeyWarningKind.Duplicate or HotkeyWarningKind.ExternalConflict => "HotkeyConflictBrush",
                HotkeyWarningKind.Reserved => "HotkeyReservedBrush",
                HotkeyWarningKind.Fallback => "HotkeyFallbackBrush",
                _ => null,
            };

            if (resourceKey is null)
            {
                return false;
            }

            brush = AssociatedObject.TryFindResource(resourceKey) as Brush;
            return brush != null;
        }
    }
}

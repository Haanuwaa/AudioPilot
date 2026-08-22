using System.Windows;
using System.Windows.Controls;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public static class TextBoxPlaceholderBehavior
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(TextBoxPlaceholderBehavior),
            new FrameworkPropertyMetadata(string.Empty));

        public static readonly DependencyProperty IsContextMenuOpenProperty = DependencyProperty.RegisterAttached(
            "IsContextMenuOpen",
            typeof(bool),
            typeof(TextBoxPlaceholderBehavior),
            new FrameworkPropertyMetadata(false));

        private static readonly DependencyProperty StableTextProperty = DependencyProperty.RegisterAttached(
            "StableText",
            typeof(string),
            typeof(TextBoxPlaceholderBehavior),
            new FrameworkPropertyMetadata(string.Empty));

        public static string GetText(DependencyObject obj)
        {
            return (string)obj.GetValue(TextProperty);
        }

        public static void SetText(DependencyObject obj, string value)
        {
            obj.SetValue(TextProperty, value);
        }

        public static bool GetIsContextMenuOpen(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsContextMenuOpenProperty);
        }

        public static void SetIsContextMenuOpen(DependencyObject obj, bool value)
        {
            obj.SetValue(IsContextMenuOpenProperty, value);
        }

        internal static string EnsureStableText(
            DependencyObject obj,
            string? options,
            string? identity = null,
            Func<string?, string>? pickDefaultOption = null,
            string? preferredFamily = null)
        {
            ArgumentNullException.ThrowIfNull(obj);

            string stableText = GetStableText(obj);
            if (string.IsNullOrWhiteSpace(stableText))
            {
                string selectedOption = PlaceholderExampleBehavior.PickDeterministicOption(
                    options,
                    identity,
                    pickDefaultOption,
                    preferredFamily);
                stableText = HotkeyDisplayFormatter.FormatSpaced(selectedOption);
                SetStableText(obj, stableText);
            }

            if (!GetIsContextMenuOpen(obj))
            {
                SetText(obj, stableText);
            }
            return stableText;
        }

        private static string GetStableText(DependencyObject obj)
        {
            return (string)obj.GetValue(StableTextProperty);
        }

        private static void SetStableText(DependencyObject obj, string value)
        {
            obj.SetValue(StableTextProperty, value);
        }
    }

    public sealed class PlaceholderExampleBehavior : Behavior<TextBox>
    {
        public string? Options { get; set; }

        public string? Identity { get; set; }

        public string? PreferredFamily { get; set; }

        protected override void OnAttached()
        {
            base.OnAttached();

            if (AssociatedObject == null)
            {
                return;
            }

            AssociatedObject.ContextMenuOpening += OnContextMenuOpening;
            AssociatedObject.ContextMenuClosing += OnContextMenuClosing;
            AssociatedObject.LostKeyboardFocus += OnLostKeyboardFocus;
            AssociatedObject.IsVisibleChanged += OnIsVisibleChanged;
            AssociatedObject.Unloaded += OnUnloaded;
            AssociatedObject.Loaded += OnLoaded;

            TextBoxPlaceholderBehavior.EnsureStableText(
                AssociatedObject,
                Options,
                Identity,
                preferredFamily: PreferredFamily);
        }

        protected override void OnDetaching()
        {
            if (AssociatedObject != null)
            {
                AssociatedObject.ContextMenuOpening -= OnContextMenuOpening;
                AssociatedObject.ContextMenuClosing -= OnContextMenuClosing;
                AssociatedObject.LostKeyboardFocus -= OnLostKeyboardFocus;
                AssociatedObject.IsVisibleChanged -= OnIsVisibleChanged;
                AssociatedObject.Unloaded -= OnUnloaded;
                AssociatedObject.Loaded -= OnLoaded;
                TextBoxPlaceholderBehavior.SetIsContextMenuOpen(AssociatedObject, false);
            }

            base.OnDetaching();
        }

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (AssociatedObject != null)
            {
                TextBoxPlaceholderBehavior.SetIsContextMenuOpen(AssociatedObject, true);
            }
        }

        private void OnContextMenuClosing(object sender, ContextMenuEventArgs e)
        {
            if (AssociatedObject != null)
            {
                RestorePlaceholderState();
            }
        }

        private void OnLostKeyboardFocus(object sender, RoutedEventArgs e)
        {
            if (AssociatedObject?.ContextMenu?.IsOpen == true)
            {
                return;
            }

            RestorePlaceholderState();
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (AssociatedObject == null)
            {
                return;
            }

            if (e.NewValue is false)
            {
                TextBoxPlaceholderBehavior.SetIsContextMenuOpen(AssociatedObject, false);
                return;
            }

            RestorePlaceholderState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (AssociatedObject == null)
            {
                return;
            }

            TextBoxPlaceholderBehavior.SetIsContextMenuOpen(AssociatedObject, false);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RestorePlaceholderState();
        }

        private void RestorePlaceholderState()
        {
            if (AssociatedObject == null)
            {
                return;
            }

            TextBoxPlaceholderBehavior.SetIsContextMenuOpen(AssociatedObject, false);
            TextBoxPlaceholderBehavior.EnsureStableText(
                AssociatedObject,
                Options,
                Identity,
                preferredFamily: PreferredFamily);
        }

        internal static string PickDeterministicOption(
            string? options,
            string? identity = null,
            Func<string?, string>? pickDefaultOption = null,
            string? preferredFamily = null)
        {
            string[] candidates = GetCandidates(options);
            if (candidates.Length == 0)
            {
                return string.Empty;
            }

            string[] preferredCandidates = GetPreferredCandidates(candidates, preferredFamily);
            if (preferredCandidates.Length != 0)
            {
                candidates = preferredCandidates;
            }

            if (string.IsNullOrWhiteSpace(identity))
            {
                return (pickDefaultOption ?? PickDefaultOptionFromOptions)(options);
            }

            int index = ComputeStableIndex(identity, candidates.Length);
            return candidates[index];
        }

        internal static string PickDefaultOptionFromOptions(string? options)
        {
            string[] candidates = GetCandidates(options);

            if (candidates.Length == 0)
            {
                return string.Empty;
            }

            return candidates[Random.Shared.Next(candidates.Length)];
        }

        private static string[] GetCandidates(string? options)
        {
            if (string.IsNullOrWhiteSpace(options))
            {
                return [];
            }

            return [.. options
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))];
        }

        private static string[] GetPreferredCandidates(string[] candidates, string? preferredFamily)
        {
            if (string.IsNullOrWhiteSpace(preferredFamily))
            {
                return [];
            }

            return [.. candidates.Where(candidate => MatchesPreferredFamily(candidate, preferredFamily))];
        }

        private static bool MatchesPreferredFamily(string candidate, string preferredFamily)
        {
            string normalizedFamily = preferredFamily.Trim().ToLowerInvariant();
            return normalizedFamily switch
            {
                "letter" => IsLetterBased(candidate),
                "upward" => IsUpward(candidate),
                "downward" => IsDownward(candidate),
                "directional-next" => IsDirectionalNext(candidate),
                "directional-previous" => IsDirectionalPrevious(candidate),
                "mouse-left" => IsMouseLeft(candidate),
                "mouse-right" => IsMouseRight(candidate),
                "mouse-middle" => IsMouseMiddle(candidate),
                "mouse-x1" => IsMouseX1(candidate),
                "mouse-x2" => IsMouseX2(candidate),
                "wheel-up" => IsWheelUp(candidate),
                "wheel-down" => IsWheelDown(candidate),
                _ => false,
            };
        }

        private static bool IsLetterBased(string candidate)
        {
            string token = GetMainToken(candidate);
            return token.Length == 1 && char.IsLetter(token[0]);
        }

        private static bool IsUpward(string candidate)
        {
            string token = GetMainToken(candidate);
            return token is "Up" or "PageUp" or "Home" or "WheelUp";
        }

        private static bool IsDownward(string candidate)
        {
            string token = GetMainToken(candidate);
            return token is "Down" or "PageDown" or "End" or "WheelDown";
        }

        private static bool IsDirectionalNext(string candidate)
        {
            string token = GetMainToken(candidate);
            return token is "Right" or "N" or "MouseRight" or "MouseX2";
        }

        private static bool IsDirectionalPrevious(string candidate)
        {
            string token = GetMainToken(candidate);
            return token is "Left" or "MouseLeft" or "MouseX1";
        }

        private static bool IsMouseLeft(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseLeft";
        }

        private static bool IsMouseRight(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseRight";
        }

        private static bool IsMouseMiddle(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseMiddle";
        }

        private static bool IsMouseX1(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseX1";
        }

        private static bool IsMouseX2(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseX2";
        }

        private static bool IsWheelUp(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "WheelUp";
        }

        private static bool IsWheelDown(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "WheelDown";
        }

        private static string GetMainToken(string candidate)
        {
            string[] parts = candidate.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? string.Empty : parts[^1];
        }

        private static int ComputeStableIndex(string identity, int length)
        {
            unchecked
            {
                const uint offsetBasis = 2166136261;
                const uint prime = 16777619;

                uint hash = offsetBasis;
                foreach (char character in identity)
                {
                    hash ^= character;
                    hash *= prime;
                }

                return (int)(hash % length);
            }
        }
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public interface IHotkeySink
    {
        void Reset();
        void SetMain(HotkeyMainInput input);
        void AddModifier(Key key);
        void RemoveModifier(Key key);
        bool IsSupportedHotkey(HotkeyMainInput input, IReadOnlyList<Key> modifiers);
        void SetRejectedHotkey(HotkeyMainInput input, IReadOnlyList<Key> modifiers);
        string DisplayText { get; }
        uint MainVirtual { get; }
        bool HasMainInput { get; }
        List<Key> Modifiers { get; }
    }

    public sealed class HotkeyCaptureBehavior : Behavior<TextBox>
    {
        private const int WM_MOUSEHWHEEL = 0x020E;

        private INotifyPropertyChanged? _targetNotifier;
        private HwndSource? _horizontalWheelSource;
        private bool _startNewSequenceOnNextKey;
        private bool _suppressNextContextMenuOpen;
        private bool _caretMoveQueued;

        public HotkeyCaptureBehavior() { }

        public IHotkeySink? Target
        {
            get => (IHotkeySink?)GetValue(TargetProperty);
            set => SetValue(TargetProperty, value);
        }

        public static readonly DependencyProperty TargetProperty =
            System.Windows.DependencyProperty.Register(
                nameof(Target),
                typeof(IHotkeySink),
                typeof(HotkeyCaptureBehavior),
                new PropertyMetadata(null, OnTargetChanged));

        private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not HotkeyCaptureBehavior behavior)
                return;
            behavior._targetNotifier?.PropertyChanged -= behavior.OnTargetPropertyChanged;
            behavior._targetNotifier = null;

            if (e.NewValue is INotifyPropertyChanged notifier)
            {
                behavior._targetNotifier = notifier;
                behavior._targetNotifier.PropertyChanged += behavior.OnTargetPropertyChanged;
            }

            behavior.RefreshTextbox();
        }

        private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IHotkeySink.DisplayText) || string.IsNullOrEmpty(e.PropertyName))
            {
                RefreshTextbox();
            }
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject == null) return;

            _startNewSequenceOnNextKey = true;

            AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
            AssociatedObject.PreviewKeyUp += OnPreviewKeyUp;
            AssociatedObject.PreviewTextInput += OnPreviewTextInput;
            AssociatedObject.PreviewMouseDown += OnPreviewMouseDown;
            AssociatedObject.GotKeyboardFocus += OnGotKeyboardFocus;
            AssociatedObject.LostKeyboardFocus += OnLostKeyboardFocus;
            AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            AssociatedObject.PreviewMouseMove += OnPreviewMouseMove;
            AssociatedObject.PreviewMouseUp += OnPreviewMouseUp;
            AssociatedObject.PreviewMouseWheel += OnPreviewMouseWheel;
            AssociatedObject.MouseEnter += OnMouseEnter;
            AssociatedObject.MouseLeave += OnMouseLeave;
            AssociatedObject.ContextMenuOpening += OnContextMenuOpening;
        }

        protected override void OnDetaching()
        {
            DetachHorizontalWheelHook();
            _targetNotifier?.PropertyChanged -= OnTargetPropertyChanged;
            _targetNotifier = null;

            if (AssociatedObject != null)
            {
                AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
                AssociatedObject.PreviewKeyUp -= OnPreviewKeyUp;
                AssociatedObject.PreviewTextInput -= OnPreviewTextInput;

                AssociatedObject.PreviewMouseDown -= OnPreviewMouseDown;
                AssociatedObject.GotKeyboardFocus -= OnGotKeyboardFocus;
                AssociatedObject.LostKeyboardFocus -= OnLostKeyboardFocus;
                AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
                AssociatedObject.PreviewMouseMove -= OnPreviewMouseMove;
                AssociatedObject.PreviewMouseUp -= OnPreviewMouseUp;
                AssociatedObject.PreviewMouseWheel -= OnPreviewMouseWheel;
                AssociatedObject.MouseEnter -= OnMouseEnter;
                AssociatedObject.MouseLeave -= OnMouseLeave;
                AssociatedObject.ContextMenuOpening -= OnContextMenuOpening;
            }
            base.OnDetaching();
        }

        internal static bool ShouldSuppressContextMenuAfterMouseCapture(MouseButton button, ModifierKeys modifiers)
        {
            return button == MouseButton.Right && modifiers != ModifierKeys.None;
        }

        internal static bool ShouldMoveCaretToEnd(int selectionStart, int selectionLength, int textLength)
        {
            return selectionLength != 0 || selectionStart != textLength;
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.System || key == Key.LWin || key == Key.RWin;
        }

        private static bool TryNormalizeModifier(Key key, out Key norm)
        {
            switch (key)
            {
                case Key.LeftCtrl:
                case Key.RightCtrl:
                    norm = Key.LeftCtrl;
                    return true;

                case Key.LeftShift:
                case Key.RightShift:
                    norm = Key.LeftShift;
                    return true;

                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.System:
                    norm = Key.LeftAlt;
                    return true;

                case Key.LWin:
                case Key.RWin:
                    norm = Key.LWin;
                    return true;

                default:
                    norm = Key.None;
                    return false;
            }
        }

        private static bool HasAnyModifierAtTime()
        {
            return Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ||
                   Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ||
                   Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) ||
                   Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        }

        private static List<Key> GetActiveModifiers()
        {
            var modifiers = new List<Key>(4);
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) modifiers.Add(Key.LeftCtrl);
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) modifiers.Add(Key.LeftShift);
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) modifiers.Add(Key.LeftAlt);
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers.Add(Key.LWin);
            return modifiers;
        }

        private static bool IsDisallowedMainKey(Key key)
        {
            return key is
                Key.Tab or
                Key.Enter or
                Key.Escape or
                Key.Space or
                Key.Back or
                Key.Apps or
                Key.CapsLock or
                Key.NumLock or
                Key.Scroll;
        }

        internal static bool ShouldResetOnDelete(Key key, ModifierKeys modifiers)
        {
            return key == Key.Delete && modifiers == ModifierKeys.None;
        }

        internal static bool ShouldLeaveKeyDownForTextboxNavigation(Key key, ModifierKeys modifiers)
        {
            return (IsCaretNavKey(key) && modifiers == ModifierKeys.None)
                || (key == Key.A && modifiers == ModifierKeys.Control);
        }

        private static bool IsCaretNavKey(Key key)
        {
            return key is
                Key.Left or
                Key.Right or
                Key.Up or
                Key.Down or
                Key.Home or
                Key.End or
                Key.PageDown or
                Key.PageUp;
        }

        private static bool IsMediaArtifactKey(Key key)
        {
            return key is
                Key.MediaPlayPause or
                Key.MediaNextTrack or
                Key.MediaPreviousTrack or
                Key.MediaStop or
                Key.VolumeUp or
                Key.VolumeDown or
                Key.VolumeMute;
        }

        private static readonly Key[] PhysicalMainKeyCandidates =
        [
            Key.A, Key.B, Key.C, Key.D, Key.E, Key.F, Key.G, Key.H, Key.I, Key.J, Key.K, Key.L, Key.M,
            Key.N, Key.O, Key.P, Key.Q, Key.R, Key.S, Key.T, Key.U, Key.V, Key.W, Key.X, Key.Y, Key.Z,
            Key.D0, Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9,
            Key.NumPad0, Key.NumPad1, Key.NumPad2, Key.NumPad3, Key.NumPad4,
            Key.NumPad5, Key.NumPad6, Key.NumPad7, Key.NumPad8, Key.NumPad9,
            Key.OemPeriod, Key.OemComma, Key.OemPlus, Key.OemMinus, Key.Oem2,
            Key.OemSemicolon, Key.OemQuotes, Key.OemOpenBrackets, Key.OemCloseBrackets,
            Key.OemBackslash, Key.OemTilde,
            Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12
        ];

        private static bool TryResolvePhysicalMainKeyFromState(out Key resolved)
        {
            resolved = Key.None;

            var pressed = new List<Key>();
            foreach (var candidate in PhysicalMainKeyCandidates)
            {
                if (Keyboard.IsKeyDown(candidate))
                {
                    pressed.Add(candidate);
                }
            }

            if (pressed.Count == 1)
            {
                resolved = pressed[0];
                return true;
            }

            return false;
        }

        private static Key ResolveRawKey(KeyEventArgs e)
        {
            if (e.SystemKey != Key.None && e.SystemKey != Key.System)
            {
                return e.SystemKey;
            }

            if (e.Key != Key.System)
            {
                return e.Key;
            }

            if (e.SystemKey != Key.None)
            {
                return e.SystemKey;
            }

            if (e.ImeProcessedKey != Key.None)
            {
                return e.ImeProcessedKey;
            }

            if (e.DeadCharProcessedKey != Key.None)
            {
                return e.DeadCharProcessedKey;
            }

            return Key.None;
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (Target is null) return;
            if (e.IsRepeat) return;

            var rawKey = ResolveRawKey(e);
            if (IsMediaArtifactKey(rawKey) && TryResolvePhysicalMainKeyFromState(out var resolvedPhysical))
            {
                rawKey = resolvedPhysical;
            }

            if (rawKey == Key.Tab &&
                (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
            {
                ClearModifierOnlyState();
                return;
            }

            if (ShouldLeaveKeyDownForTextboxNavigation(rawKey, Keyboard.Modifiers))
            {
                e.Handled = true;
                MoveCaretToEnd();
                return;
            }

            e.Handled = true;
            if (ShouldResetOnDelete(e.Key, Keyboard.Modifiers))
            {
                Target.Reset();
                _startNewSequenceOnNextKey = true;
                RefreshTextbox();
                return;
            }

            if (rawKey == Key.None) return;

            if (_startNewSequenceOnNextKey)
            {
                Target.Reset();
                _startNewSequenceOnNextKey = false;
            }

            if (IsModifierKey(rawKey))
            {
                if (TryNormalizeModifier(rawKey, out var norm))
                {
                    Target.AddModifier(norm);
                    RefreshTextbox();
                }
                return;
            }

            HotkeyMainInput mainInput = HotkeyMainInput.FromKeyboard(rawKey);
            List<Key> activeModifiers = GetActiveModifiers();
            if (!Target.IsSupportedHotkey(mainInput, activeModifiers))
            {
                Target.SetRejectedHotkey(mainInput, activeModifiers);
                RefreshTextbox();
                return;
            }

            if (activeModifiers.Count == 0 && IsDisallowedMainKey(rawKey))
            {
                return;
            }

            SyncModifiersFromKeyboard();
            Target.SetMain(mainInput);
            RefreshTextbox();
            _startNewSequenceOnNextKey = true;
        }

        private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
        {
            if (Target is null) return;

            var rawKey = ResolveRawKey(e);
            if (rawKey == Key.Tab &&
                (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
            {
                return;
            }

            e.Handled = true;

            if (!IsModifierKey(rawKey)) return;

            if (!Target.HasMainInput)
            {
                SyncModifiersFromKeyboard();
                RefreshTextbox();

                if (!HasAnyModifierAtTime())
                {
                    _startNewSequenceOnNextKey = true;
                }
            }
        }

        private void OnPreviewTextInput(object? sender, TextCompositionEventArgs e)
        {
            e.Handled = true;
            MoveCaretToEnd();
        }

        private void OnPreviewMouseDown(object? sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                TryCaptureMouseInput(HotkeyMainInput.FromMouseButton(e.ChangedButton), e);
                return;
            }

            QueueMoveCaretToEnd();
        }

        private void OnGotKeyboardFocus(object? sender, KeyboardFocusChangedEventArgs e)
        {
            _startNewSequenceOnNextKey = true;
            AttachHorizontalWheelHook();
            QueueMoveCaretToEnd();
        }

        private void OnLostKeyboardFocus(object? sender, KeyboardFocusChangedEventArgs e)
        {
            DetachHorizontalWheelHookIfInactive();
            ClearModifierOnlyState();
        }

        private void OnMouseEnter(object sender, MouseEventArgs e) => AttachHorizontalWheelHook();

        private void OnMouseLeave(object sender, MouseEventArgs e) => DetachHorizontalWheelHookIfInactive();

        private void AttachHorizontalWheelHook()
        {
            if (AssociatedObject == null)
            {
                return;
            }

            HwndSource? source = PresentationSource.FromVisual(AssociatedObject) as HwndSource;
            if (ReferenceEquals(source, _horizontalWheelSource))
            {
                return;
            }

            DetachHorizontalWheelHook();
            _horizontalWheelSource = source;
            _horizontalWheelSource?.AddHook(OnWindowMessage);
        }

        private void DetachHorizontalWheelHook()
        {
            _horizontalWheelSource?.RemoveHook(OnWindowMessage);
            _horizontalWheelSource = null;
        }

        private void DetachHorizontalWheelHookIfInactive()
        {
            if (AssociatedObject?.IsKeyboardFocusWithin != true && AssociatedObject?.IsMouseOver != true)
            {
                DetachHorizontalWheelHook();
            }
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WM_MOUSEHWHEEL || AssociatedObject?.IsMouseOver != true)
            {
                return IntPtr.Zero;
            }

            HotkeyMainInput input = ResolveHorizontalWheelInput(wParam);
            if (input.HasValue)
            {
                handled = TryCaptureMouseInput(input, null);
            }

            return IntPtr.Zero;
        }

        internal static HotkeyMainInput ResolveHorizontalWheelInput(IntPtr wParam)
        {
            short delta = unchecked((short)((wParam.ToInt64() >> 16) & ushort.MaxValue));
            return delta switch
            {
                > 0 => HotkeyMainInput.WheelRight,
                < 0 => HotkeyMainInput.WheelLeft,
                _ => HotkeyMainInput.None,
            };
        }

        private void OnPreviewMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            AssociatedObject?.Focus();

            if (AssociatedObject?.IsKeyboardFocused == true && Keyboard.Modifiers != ModifierKeys.None)
            {
                TryCaptureMouseInput(HotkeyMainInput.FromMouseButton(MouseButton.Left), e);
                return;
            }

            _startNewSequenceOnNextKey = true;
            QueueMoveCaretToEnd();
        }

        private void OnPreviewMouseMove(object? sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                QueueMoveCaretToEnd();
            }
        }

        private void OnPreviewMouseUp(object? sender, MouseButtonEventArgs e)
        {
            QueueMoveCaretToEnd();
        }

        private void OnPreviewMouseWheel(object? sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0)
            {
                TryCaptureMouseInput(e.Delta > 0 ? HotkeyMainInput.WheelUp : HotkeyMainInput.WheelDown, e);
            }
        }

        private void OnContextMenuOpening(object? sender, ContextMenuEventArgs e)
        {
            QueueMoveCaretToEnd();

            if (!_suppressNextContextMenuOpen)
            {
                return;
            }

            _suppressNextContextMenuOpen = false;
            e.Handled = true;
        }

        private void RefreshTextbox()
        {
            if (AssociatedObject == null) return;

            var text = Target?.DisplayText ?? string.Empty;
            AssociatedObject.Text = text;
            MoveCaretToEnd();
        }

        private void SyncModifiersFromKeyboard()
        {
            if (Target is null)
            {
                return;
            }

            var expected = new List<Key>();
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) expected.Add(Key.LeftCtrl);
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) expected.Add(Key.LeftShift);
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) expected.Add(Key.LeftAlt);
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) expected.Add(Key.LWin);

            foreach (var existing in Target.Modifiers.ToArray())
            {
                Target.RemoveModifier(existing);
            }

            foreach (var modifier in expected)
            {
                Target.AddModifier(modifier);
            }
        }

        private void MoveCaretToEnd()
        {
            if (AssociatedObject == null) return;

            var len = AssociatedObject.Text?.Length ?? 0;
            if (!ShouldMoveCaretToEnd(AssociatedObject.SelectionStart, AssociatedObject.SelectionLength, len))
            {
                return;
            }

            AssociatedObject.SelectionLength = 0;
            AssociatedObject.SelectionStart = len;
            AssociatedObject.CaretIndex = len;
        }

        private void QueueMoveCaretToEnd()
        {
            if (AssociatedObject == null || _caretMoveQueued)
            {
                return;
            }

            _caretMoveQueued = true;
            _ = AssociatedObject.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    _caretMoveQueued = false;
                    MoveCaretToEnd();
                }));
        }

        private void ClearModifierOnlyState()
        {
            if (Target is null || Target.HasMainInput || Target.Modifiers.Count == 0)
            {
                return;
            }

            var modifiers = Target.Modifiers.ToArray();
            foreach (var modifier in modifiers)
            {
                Target.RemoveModifier(modifier);
            }

            RefreshTextbox();
        }

        private bool TryCaptureMouseInput(HotkeyMainInput input, InputEventArgs? e)
        {
            if (Target is null)
            {
                return false;
            }

            AssociatedObject?.Focus();
            e?.Handled = true;

            List<Key> activeModifiers = GetActiveModifiers();
            if (!Target.IsSupportedHotkey(input, activeModifiers))
            {
                Target.SetRejectedHotkey(input, activeModifiers);
                RefreshTextbox();
                MoveCaretToEnd();
                return true;
            }

            if (_startNewSequenceOnNextKey)
            {
                Target.Reset();
                _startNewSequenceOnNextKey = false;
            }

            SyncModifiersFromKeyboard();
            Target.SetMain(input);
            if (e is MouseButtonEventArgs mouseButtonEventArgs &&
                ShouldSuppressContextMenuAfterMouseCapture(mouseButtonEventArgs.ChangedButton, Keyboard.Modifiers))
            {
                _suppressNextContextMenuOpen = true;
            }
            RefreshTextbox();
            _startNewSequenceOnNextKey = true;
            return true;
        }
    }
}

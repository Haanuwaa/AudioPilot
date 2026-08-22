using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Constants;
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
        void SetEditing(bool isEditing);
        string DisplayText { get; }
        uint MainVirtual { get; }
        bool HasMainInput { get; }
        List<Key> Modifiers { get; }
    }

    public sealed partial class HotkeyCaptureBehavior : Behavior<TextBox>
    {
        private const int WM_MOUSEHWHEEL = 0x020E;

        private INotifyPropertyChanged? _targetNotifier;
        private HwndSource? _horizontalWheelSource;
        private bool _startNewSequenceOnNextKey;
        private bool _suppressNextContextMenuOpen;
        private bool _caretMoveQueued;
        private int _capturedSideButtonUps;

        private readonly HotkeyCaptureSession _captureSession;
        private readonly Func<nint> _getMessageExtraInfo;
        private IDisposable? _captureLease;

        public HotkeyCaptureBehavior() : this(HotkeyCaptureSession.Shared, GetMessageExtraInfo) { }

        internal HotkeyCaptureBehavior(HotkeyCaptureSession captureSession, Func<nint> getMessageExtraInfo)
        {
            _captureSession = captureSession;
            _getMessageExtraInfo = getMessageExtraInfo;
        }

        [LibraryImport("user32.dll")]
        private static partial nint GetMessageExtraInfo();

        internal void ReleaseCapture()
        {
            _captureLease?.Dispose();
            _captureLease = null;
            Target?.SetEditing(false);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => ReleaseCapture();

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (AssociatedObject.IsVisible && AssociatedObject.IsKeyboardFocusWithin)
            {
                _captureLease ??= _captureSession.Acquire();
                Target?.SetEditing(true);
            }
            else if (!AssociatedObject.IsVisible)
                ReleaseCapture();
        }

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
            (e.OldValue as IHotkeySink)?.SetEditing(false);
            (e.NewValue as IHotkeySink)?.SetEditing(behavior._captureLease != null);
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
            Target?.SetEditing(AssociatedObject.IsKeyboardFocusWithin);
            if (AssociatedObject.IsKeyboardFocusWithin) _captureLease ??= _captureSession.Acquire();

            AssociatedObject.Unloaded += OnUnloaded;
            AssociatedObject.IsVisibleChanged += OnIsVisibleChanged;
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
            ReleaseCapture();
            _capturedSideButtonUps = 0;
            DetachHorizontalWheelHook();
            _targetNotifier?.PropertyChanged -= OnTargetPropertyChanged;
            _targetNotifier = null;

            if (AssociatedObject != null)
            {
                AssociatedObject.Unloaded -= OnUnloaded;
                AssociatedObject.IsVisibleChanged -= OnIsVisibleChanged;
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

        private void TrackPressedModifiers()
        {
            ReadOnlySpan<Key> keys = [Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin];
            foreach (Key key in keys)
            {
                if (Keyboard.IsKeyDown(key)) _captureSession.TrackKeyDown(KeyInterop.VirtualKeyFromKey(key));
            }
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
            if (_getMessageExtraInfo() == (nint)AppConstants.Hotkeys.SyntheticMediaInputMarker)
            {
                e.Handled = true;
                return;
            }
            if (Target is null) return;
            if (e.IsRepeat) return;

            var rawKey = ResolveRawKey(e);
            _captureSession.TrackKeyDown(KeyInterop.VirtualKeyFromKey(rawKey));
            TrackPressedModifiers();

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

            SyncModifiersFromKeyboard();
            Target.SetMain(mainInput);
            RefreshTextbox();
            _startNewSequenceOnNextKey = true;
        }

        private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
        {
            if (_getMessageExtraInfo() == (nint)AppConstants.Hotkeys.SyntheticMediaInputMarker)
            {
                e.Handled = true;
                return;
            }
            if (Target is null) return;

            var rawKey = ResolveRawKey(e);
            _captureSession.TrackKeyUp(KeyInterop.VirtualKeyFromKey(rawKey));
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
                if (TryCaptureMouseInput(HotkeyMainInput.FromMouseButton(e.ChangedButton), e)
                    && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
                    _capturedSideButtonUps |= 1 << (int)e.ChangedButton;
                return;
            }

            QueueMoveCaretToEnd();
        }

        private void OnGotKeyboardFocus(object? sender, KeyboardFocusChangedEventArgs e)
        {
            _captureLease ??= _captureSession.Acquire();
            Target?.SetEditing(true);
            _startNewSequenceOnNextKey = true;
            AttachHorizontalWheelHook();
            QueueMoveCaretToEnd();
        }

        private void OnLostKeyboardFocus(object? sender, KeyboardFocusChangedEventArgs e)
        {
            ReleaseCapture();
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
            _captureSession.TrackKeyUp(MouseButtonVirtualKey(e.ChangedButton));
            int bit = 1 << (int)e.ChangedButton;
            if ((_capturedSideButtonUps & bit) != 0)
            {
                _capturedSideButtonUps &= ~bit;
                e.Handled = true;
            }
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

        private static int MouseButtonVirtualKey(MouseButton button) => button switch
        {
            MouseButton.Left => 0x01,
            MouseButton.Right => 0x02,
            MouseButton.Middle => 0x04,
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            _ => 0,
        };

        private bool TryCaptureMouseInput(HotkeyMainInput input, InputEventArgs? e)
        {
            if (Target is null)
            {
                return false;
            }

            List<Key> activeModifiers = GetActiveModifiers();
            if (activeModifiers.Count == 0 && !input.AllowsStandaloneWithoutModifier)
            {
                return false;
            }

            AssociatedObject?.Focus();
            TrackPressedModifiers();
            if (input.Kind == HotkeyMainInputKind.MouseButton)
                _captureSession.TrackKeyDown(MouseButtonVirtualKey(input.MouseButton));
            e?.Handled = true;

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

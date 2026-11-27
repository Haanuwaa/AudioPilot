using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Logging;

namespace AudioPilot.Helpers
{
    internal static partial class WindowFirstPresentationHelper
    {
        private static readonly nint WindowTopmost = new(-1);
        private static readonly DependencyProperty ShortcutRetryProperty = DependencyProperty.RegisterAttached(
            "ShortcutRetry", typeof(ShortcutRaise), typeof(WindowFirstPresentationHelper));

        internal static bool TryRaiseForShortcut(Window window, Logger logger, TimeSpan duration, Func<nint>? getForeground = null,
            nint? expectedForeground = null)
        {
            window.Dispatcher.VerifyAccess();
            if (!window.IsVisible || window.Topmost || window.Owner != null) return false;
            if (PresentationSource.FromVisual(window) is not HwndSource { IsDisposed: false } source) return false;
            getForeground ??= GetForegroundWindow;
            nint handle = source.Handle;
            nint foreground = getForeground();
            if (handle == 0 || handle == foreground || (expectedForeground is nint expected && foreground != expected)
                || IsIconic(handle) || IsNativeTopmost(handle)) return false;

            var raise = new ShortcutRaise(window, source, handle, foreground, logger, duration, getForeground);
            if (!MoveWithoutActivation(handle, WindowTopmost) || !IsNativeTopmost(handle)) return false;
            try
            {
                ClearShortcutRetry(window);
                raise.Start();
            }
            catch
            {
                raise.End("start-failed");
                throw;
            }
            logger.Debug("WindowFirstPresentationHelper", "main-window-shortcut-raise | event=started");
            return true;
        }

        internal static bool CanRetryShortcutShow(Window window)
        {
            if (window.GetValue(ShortcutRetryProperty) is not ShortcutRaise retry) return false;
            if (retry.IsInBackground()) return true;
            retry.Detach();
            return false;
        }

        private static void ClearShortcutRetry(Window window)
        {
            if (window.GetValue(ShortcutRetryProperty) is ShortcutRaise retry) retry.Detach();
        }

        private static bool IsNativeTopmost(nint handle) => (GetWindowLong(handle, ExtendedWindowStyleIndex) & ExtendedWindowStyleTopmost) != 0;

        private static bool MoveWithoutActivation(nint handle, nint insertAfter) => SetWindowPos(handle, insertAfter, 0, 0, 0, 0,
            SetWindowPosNoSize | SetWindowPosNoMove | SetWindowPosNoActivate | SetWindowPosNoOwnerZOrder);

        private sealed class ShortcutRaise(Window window, HwndSource source, nint handle, nint originalForeground,
            Logger logger, TimeSpan duration, Func<nint> getForeground)
        {
            private const int WmSetFocus = 0x0007;
            private const int WmMouseActivate = 0x0021;
            private const int WmActivateApp = 0x001C;
            private const int WmNcLeftButtonDown = 0x00A1;
            private static readonly TimeSpan ForegroundSampleInterval = TimeSpan.FromMilliseconds(50);
            private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal, window.Dispatcher)
            { Interval = duration < ForegroundSampleInterval ? duration : ForegroundSampleInterval };
            private long _started;
            private bool _ended;
            private bool _detached;

            internal bool IsInBackground() => window.IsVisible && !source.IsDisposed && source.Handle == handle && getForeground() != handle;

            internal void Start()
            {
                source.AddHook(OnWindowMessage);
                window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown), handledEventsToo: true);
                window.Activated += OnActivated;
                window.IsVisibleChanged += OnVisibilityChanged;
                window.Closed += OnClosed;
                _started = Stopwatch.GetTimestamp();
                _timer.Tick += OnTimer;
                _timer.Start();
            }

            private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
            {
                // Let Windows finish processing the click, including any non-client activation.
                if (message is WmMouseActivate or WmNcLeftButtonDown)
                    _ = window.Dispatcher.InvokeAsync(() => Dismiss("click"), DispatcherPriority.Input);
                else if (_ended && (message == WmSetFocus || (message == WmActivateApp && wParam != 0)))
                    _ = window.Dispatcher.InvokeAsync(ObserveActivation, DispatcherPriority.Input);
                return 0;
            }

            private void OnMouseDown(object sender, MouseButtonEventArgs e) => Dismiss("click");

            private void OnActivated(object? sender, EventArgs e) => ObserveActivation();

            private void ObserveActivation()
            {
                if (!_detached && getForeground() == handle) Dismiss("activated");
            }

            private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (!window.IsVisible) Dismiss("hidden");
            }

            private void OnClosed(object? sender, EventArgs e) => Dismiss("closed");

            private void OnTimer(object? sender, EventArgs e)
            {
                // WPF can already report IsActive after a denied activation. A later native
                // foreground change can then arrive without another managed Activated event.
                nint foreground = getForeground();
                if (foreground == handle) End("activated");
                else if (foreground != 0 && foreground != originalForeground) End("foreground-changed");
                else if (Stopwatch.GetElapsedTime(_started) >= duration) End("timeout");
            }

            private void Dismiss(string reason)
            {
                if (_ended) Detach();
                else End(reason);
            }

            internal void End(string reason)
            {
                if (_ended) return;
                _ended = true;
                _timer.Stop();
                _timer.Tick -= OnTimer;

                bool stillTopmost = false;
                bool endedInBackground = reason is "timeout" or "foreground-changed";
                if (!source.IsDisposed && source.Handle == handle)
                {
                    if (IsNativeTopmost(handle))
                    {
                        nint foreground = GetForegroundWindow();
                        if (endedInBackground && foreground != 0 && foreground != handle && !IsNativeTopmost(foreground))
                        {
                            _ = MoveWithoutActivation(handle, foreground);
                        }
                        if (IsNativeTopmost(handle)) _ = MoveWithoutActivation(handle, WindowNotTopmost);
                        stillTopmost = IsNativeTopmost(handle);
                    }
                }
                if (!_detached && endedInBackground && !stillTopmost && IsInBackground())
                {
                    // Keep the window listeners until reactivation, a new raise, or explicit interaction.
                    window.SetValue(ShortcutRetryProperty, this);
                }
                else Detach();

                if (stillTopmost)
                    logger.Warning("WindowFirstPresentationHelper", () => $"main-window-shortcut-raise-clear-failed | reason={reason}");
                else
                    logger.Debug("WindowFirstPresentationHelper", () => $"main-window-shortcut-raise | event=ended reason={reason}");
            }

            internal void Detach()
            {
                if (_detached) return;
                _detached = true;
                window.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown));
                window.Activated -= OnActivated;
                window.IsVisibleChanged -= OnVisibilityChanged;
                window.Closed -= OnClosed;
                if (!source.IsDisposed && source.Handle == handle) source.RemoveHook(OnWindowMessage);
                if (ReferenceEquals(window.GetValue(ShortcutRetryProperty), this)) window.ClearValue(ShortcutRetryProperty);
            }
        }
    }
}

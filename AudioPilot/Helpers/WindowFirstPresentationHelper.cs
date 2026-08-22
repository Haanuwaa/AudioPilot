using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Helpers
{
    /// <summary>
    /// Coordinates the first presentation of a WPF window so its themed, rendered native surface is ready before it appears on-screen.
    /// </summary>
    internal static partial class WindowFirstPresentationHelper
    {
        private static readonly DependencyProperty IsOffscreenFirstRenderStagedProperty =
            DependencyProperty.RegisterAttached(
                "IsOffscreenFirstRenderStaged",
                typeof(bool),
                typeof(WindowFirstPresentationHelper),
                new PropertyMetadata(false));

        private static readonly DependencyProperty FinalFirstPresentationLeftProperty =
            DependencyProperty.RegisterAttached(
                "FinalFirstPresentationLeft",
                typeof(double),
                typeof(WindowFirstPresentationHelper),
                new PropertyMetadata(0d));

        private static readonly DependencyProperty FinalFirstPresentationTopProperty =
            DependencyProperty.RegisterAttached(
                "FinalFirstPresentationTop",
                typeof(double),
                typeof(WindowFirstPresentationHelper),
                new PropertyMetadata(0d));

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(nint windowHandle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(nint windowHandle);

        [LibraryImport("user32.dll")]
        private static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        private const uint SetWindowPosNoSize = 0x0001;
        private const uint SetWindowPosNoMove = 0x0002;
        private const uint SetWindowPosNoZOrder = 0x0004;
        private const uint SetWindowPosNoActivate = 0x0010;
        private const uint SetWindowPosShowWindow = 0x0040;
        private const uint SetWindowPosNoOwnerZOrder = 0x0200;
        private static readonly nint WindowTopmost = new(-1);
        private static readonly nint WindowNotTopmost = new(-2);

        internal static void Prepare(Window window, bool hideFromTaskbar = true)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Opacity = 0d;
            window.ShowActivated = false;
            if (hideFromTaskbar)
            {
                window.ShowInTaskbar = false;
            }
        }

        internal static bool TryApplyNativeClientBackground(Window window, bool ensureHandle = false)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Dispatcher.VerifyAccess();
            var interopHelper = new WindowInteropHelper(window);
            nint windowHandle = ensureHandle ? interopHelper.EnsureHandle() : interopHelper.Handle;
            return TryApplyNativeClientBackground(window, windowHandle);
        }

        internal static void StageOffscreenFirstRender(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Dispatcher.VerifyAccess();

            if ((bool)window.GetValue(IsOffscreenFirstRenderStagedProperty))
            {
                return;
            }

            double width = ResolvePositiveDimension(window.Width, window.ActualWidth, window.MinWidth, 480d);
            double height = ResolvePositiveDimension(window.Height, window.ActualHeight, window.MinHeight, 420d);
            Rect workArea = SystemParameters.WorkArea;
            double finalLeft = double.IsFinite(window.Left)
                ? window.Left
                : workArea.Left + Math.Max(0d, (workArea.Width - width) / 2d);
            double finalTop = double.IsFinite(window.Top)
                ? window.Top
                : workArea.Top + Math.Max(0d, (workArea.Height - height) / 2d);
            window.SetValue(FinalFirstPresentationLeftProperty, finalLeft);
            window.SetValue(FinalFirstPresentationTopProperty, finalTop);
            window.SetValue(IsOffscreenFirstRenderStagedProperty, true);
            window.Left = SystemParameters.VirtualScreenLeft - width - 256d;
            window.Top = SystemParameters.VirtualScreenTop - height - 256d;
        }

        internal static void BeginOffscreenFirstRender(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Dispatcher.VerifyAccess();

            if (!(bool)window.GetValue(IsOffscreenFirstRenderStagedProperty))
            {
                return;
            }

            window.Opacity = 1d;
        }

        internal static void WithdrawFirstPresentation(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Dispatcher.VerifyAccess();

            window.Opacity = 0d;
            RestoreStagedPosition(window);
        }

        internal static async Task<bool> RevealAsync(
            Window window,
            bool activate,
            Func<bool>? canReveal = null,
            bool waitForFirstRender = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(window);

            Task<bool> firstRender = waitForFirstRender
                ? WaitForFirstContentRenderAsync(window, cancellationToken)
                : Task.FromResult(true);

            await window.Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Loaded,
                cancellationToken);
            if (!await firstRender)
            {
                return false;
            }
            await window.Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Render,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!window.IsLoaded || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
            {
                return false;
            }

            if (canReveal != null && !canReveal())
            {
                return false;
            }

            bool isStaged = (bool)window.GetValue(IsOffscreenFirstRenderStagedProperty);
            nint windowHandle = new WindowInteropHelper(window).Handle;
            if (isStaged)
            {
                RestoreStagedPosition(window, windowHandle);
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Loaded,
                    cancellationToken);
                await window.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Render,
                    cancellationToken);

                if (canReveal != null && !canReveal())
                {
                    return false;
                }

            }

            if (activate)
            {
                Activate(window);
            }

            windowHandle = new WindowInteropHelper(window).Handle;
            return windowHandle != nint.Zero
                && IsWindowVisible(windowHandle)
                && window.IsVisible
                && window.Visibility == Visibility.Visible
                && window.Opacity > 0d;
        }

        internal static void Activate(Window window)
        {
            try
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                _ = window.Activate();
                nint windowHandle = new WindowInteropHelper(window).Handle;
                if (windowHandle != nint.Zero && GetForegroundWindow() != windowHandle)
                {
                    _ = SetForegroundWindow(windowHandle);
                    if (GetForegroundWindow() != windowHandle)
                    {
                        uint promotionFlags = SetWindowPosNoSize |
                            SetWindowPosNoMove |
                            SetWindowPosShowWindow |
                            SetWindowPosNoOwnerZOrder;
                        _ = SetWindowPos(windowHandle, WindowTopmost, 0, 0, 0, 0, promotionFlags);
                        _ = SetWindowPos(windowHandle, WindowNotTopmost, 0, 0, 0, 0, promotionFlags);
                        _ = SetForegroundWindow(windowHandle);
                    }

                    _ = window.Activate();
                }

                _ = window.Focus();
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning(
                    "WindowFirstPresentationHelper",
                    () => $"window-activation-failed | windowType={window.GetType().Name} error={ex.GetType().Name}",
                    nameof(Activate),
                    ex);
            }
        }

        internal static bool HasHandle(Window window)
        {
            return new WindowInteropHelper(window).Handle != nint.Zero;
        }

        private static bool TryApplyNativeClientBackground(Window window, nint windowHandle)
        {
            try
            {
                if (windowHandle == nint.Zero
                    || HwndSource.FromHwnd(windowHandle)?.CompositionTarget is not HwndTarget compositionTarget)
                {
                    return false;
                }

                Color backgroundColor = window.Background is SolidColorBrush backgroundBrush
                    ? backgroundBrush.Color
                    : SystemColors.WindowColor;
                compositionTarget.BackgroundColor = Color.FromRgb(
                    backgroundColor.R,
                    backgroundColor.G,
                    backgroundColor.B);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning(
                    "WindowFirstPresentationHelper",
                    () => $"native-client-background-apply-failed | windowType={window.GetType().Name} error={ex.GetType().Name}",
                    nameof(TryApplyNativeClientBackground),
                    ex);
                return false;
            }
        }

        private static async Task<bool> WaitForFirstContentRenderAsync(Window window, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnContentRendered(object? sender, EventArgs args)
            {
                completion.TrySetResult(true);
            }

            window.ContentRendered += OnContentRendered;

            try
            {
                try
                {
                    return await completion.Task.WaitAsync(
                        TimeSpan.FromMilliseconds(AppConstants.Timing.FirstPresentationRenderTimeoutMs),
                        cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    Logger.Instance.Warning(
                        "WindowFirstPresentationHelper",
                        () => $"first-presentation-render-timeout | error={ex.GetType().Name}",
                        nameof(WaitForFirstContentRenderAsync));
                    return false;
                }
            }
            finally
            {
                window.ContentRendered -= OnContentRendered;
            }
        }

        private static void RestoreStagedPosition(Window window, nint windowHandle = default)
        {
            if (!(bool)window.GetValue(IsOffscreenFirstRenderStagedProperty))
            {
                return;
            }

            double finalLeft = (double)window.GetValue(FinalFirstPresentationLeftProperty);
            double finalTop = (double)window.GetValue(FinalFirstPresentationTopProperty);
            bool positioned = false;
            if (windowHandle != nint.Zero
                && HwndSource.FromHwnd(windowHandle)?.CompositionTarget is HwndTarget compositionTarget)
            {
                Point devicePosition = compositionTarget.TransformToDevice.Transform(new Point(finalLeft, finalTop));
                positioned = SetWindowPos(
                    windowHandle,
                    nint.Zero,
                    (int)Math.Round(devicePosition.X),
                    (int)Math.Round(devicePosition.Y),
                    0,
                    0,
                    SetWindowPosNoSize |
                    SetWindowPosNoZOrder |
                    SetWindowPosNoActivate |
                    SetWindowPosNoOwnerZOrder);
            }

            if (!positioned)
            {
                window.Left = finalLeft;
                window.Top = finalTop;
            }

            window.ClearValue(FinalFirstPresentationLeftProperty);
            window.ClearValue(FinalFirstPresentationTopProperty);
            window.ClearValue(IsOffscreenFirstRenderStagedProperty);
        }

        private static double ResolvePositiveDimension(params double[] candidates)
        {
            foreach (double candidate in candidates)
            {
                if (double.IsFinite(candidate) && candidate > 0d)
                {
                    return candidate;
                }
            }

            return 1d;
        }
    }
}

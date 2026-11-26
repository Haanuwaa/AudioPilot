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

        private static readonly DependencyProperty CenterFirstPresentationProperty =
            DependencyProperty.RegisterAttached(
                "CenterFirstPresentation",
                typeof(bool),
                typeof(WindowFirstPresentationHelper),
                new PropertyMetadata(false));

        private static readonly DependencyProperty HasRenderedContentProperty =
            DependencyProperty.RegisterAttached(
                "HasRenderedContent",
                typeof(bool),
                typeof(WindowFirstPresentationHelper),
                new PropertyMetadata(false));

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(nint windowHandle, int attribute, in int value, int size);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static partial int GetWindowLong(nint windowHandle, int index);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(nint windowHandle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(nint windowHandle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(nint windowHandle, int command);

        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

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

        private const int ShowWindowRestore = 9;
        private const uint SetWindowPosNoSize = 0x0001;
        private const uint SetWindowPosNoMove = 0x0002;
        private const uint SetWindowPosNoZOrder = 0x0004;
        private const uint SetWindowPosNoActivate = 0x0010;
        private const uint SetWindowPosNoOwnerZOrder = 0x0200;
        private const int ExtendedWindowStyleIndex = -20;
        private const int ExtendedWindowStyleTopmost = 0x0008;
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
            window.SetValue(CenterFirstPresentationProperty, !double.IsFinite(window.Left) && !double.IsFinite(window.Top));
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
            Func<bool>? positionBeforeReveal = null,
            Func<nint, bool, int>? setCloak = null,
            double? revealOpacity = null,
            Action? onActivationDenied = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(window);

            window.Dispatcher.VerifyAccess();
            nint stagedHandle = (bool)window.GetValue(IsOffscreenFirstRenderStagedProperty)
                ? new WindowInteropHelper(window).EnsureHandle()
                : nint.Zero;
            setCloak ??= SetPresentationCloak;
            bool cloaked = false;
            bool revealed = false;
            double? opacityBeforePositioning = null;
            try
            {
                if (stagedHandle != nint.Zero)
                {
                    cloaked = TrySetPresentationCloak(setCloak, stagedHandle, cloak: true);
                }

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
                    if (stagedHandle != nint.Zero && !cloaked)
                    {
                        // The first render ran offscreen. Conceal relocation even if DWM cannot cloak.
                        opacityBeforePositioning = window.Opacity;
                        window.Opacity = 0d;
                        await window.Dispatcher.InvokeAsync(
                            static () => { },
                            DispatcherPriority.Render,
                            cancellationToken);
                        if (new WindowInteropHelper(window).Handle != windowHandle || (canReveal != null && !canReveal()))
                        {
                            return false;
                        }
                    }
                    RestoreStagedPosition(window, windowHandle, positionBeforeReveal);
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

                    if (positionBeforeReveal != null)
                    {
                        _ = positionBeforeReveal();
                        window.UpdateLayout();
                        await window.Dispatcher.InvokeAsync(
                            static () => { },
                            DispatcherPriority.Render,
                            cancellationToken);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (canReveal != null && !canReveal())
                {
                    return false;
                }
                if (cloaked)
                {
                    if (!TrySetPresentationCloak(setCloak, stagedHandle, cloak: false))
                    {
                        return false;
                    }
                    cloaked = false;
                }
                if ((revealOpacity ?? opacityBeforePositioning) is { } opacity)
                {
                    window.Opacity = opacity;
                }

                if (activate)
                {
                    Activate(window, onActivationDenied);
                }

                windowHandle = new WindowInteropHelper(window).Handle;
                revealed = windowHandle != nint.Zero
                    && IsWindowVisible(windowHandle)
                    && !IsIconic(windowHandle)
                    && window.IsVisible
                    && window.Visibility == Visibility.Visible
                    && window.Opacity > 0d;
                return revealed;
            }
            finally
            {
                if (!revealed && stagedHandle != nint.Zero && new WindowInteropHelper(window).Handle == stagedHandle)
                {
                    try { window.Hide(); }
                    finally
                    {
                        if (cloaked)
                        {
                            _ = TrySetPresentationCloak(setCloak, stagedHandle, cloak: false);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Restores and activates the native window even when hiding from StateChanged left its HWND minimized
        /// while WPF reports a normal WindowState. Reports a foreground denial after restoring normal stacking order.
        /// </summary>
        internal static void Activate(Window window, Action? onActivationDenied = null)
        {
            try
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                nint windowHandle = new WindowInteropHelper(window).Handle;
                if (windowHandle != nint.Zero && IsIconic(windowHandle))
                {
                    _ = ShowWindow(windowHandle, ShowWindowRestore);
                }

                WindowPlacementHelper.EnsureAccessible(windowHandle);
                _ = window.Activate();
                nint foregroundWindow = GetForegroundWindow();
                bool isIconic = windowHandle != nint.Zero && IsIconic(windowHandle);
                Logger.Instance.Trace("WindowFirstPresentationHelper", () =>
                    $"window-activation-result | windowType={window.GetType().Name} hwnd=0x{windowHandle:X} foreground=0x{foregroundWindow:X} visible={window.IsVisible} active={window.IsActive} iconic={isIconic}");
                if (windowHandle != nint.Zero && foregroundWindow == windowHandle)
                {
                    ClearShortcutRetry(window);
                    _ = window.Focus();
                    return;
                }

                if (windowHandle != nint.Zero && foregroundWindow != nint.Zero && !window.Topmost && window.Owner == null)
                {
                    _ = MoveWithoutActivation(windowHandle, IsNativeTopmost(foregroundWindow) ? WindowNotTopmost : foregroundWindow);
                }

                Logger.Instance.Debug(
                    "WindowFirstPresentationHelper",
                    () => $"window-activation-deferred | windowType={window.GetType().Name} reason=foreground-denied");
                if (windowHandle != nint.Zero && foregroundWindow != nint.Zero && !isIconic) onActivationDenied?.Invoke();
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

        /// <summary>Conceals first-render DPI relocation while DWM continues composing the window.</summary>
        private static int SetPresentationCloak(nint windowHandle, bool cloak)
        {
            const int DwmWindowAttributeCloak = 13;
            int value = cloak ? 1 : 0;
            return DwmSetWindowAttribute(windowHandle, DwmWindowAttributeCloak, in value, sizeof(int));
        }

        private static bool TrySetPresentationCloak(Func<nint, bool, int> setCloak, nint windowHandle, bool cloak)
        {
            int result;
            try { result = setCloak(windowHandle, cloak); }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { result = ex.HResult; }
            if (result < 0)
            {
                Logger.Instance.Warning("WindowFirstPresentationHelper", () =>
                    $"window-first-presentation-cloak-failed | hwnd=0x{windowHandle:X} enabled={cloak} hresult=0x{result:X8}");
                return false;
            }

            Logger.Instance.Trace("WindowFirstPresentationHelper", () =>
                $"window-first-presentation-cloak | hwnd=0x{windowHandle:X} enabled={cloak} hresult=0x{result:X8}");
            return true;
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
            if ((bool)window.GetValue(HasRenderedContentProperty)) return true;

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnContentRendered(object? sender, EventArgs args)
            {
                window.SetValue(HasRenderedContentProperty, true);
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

        private static void RestoreStagedPosition(Window window, nint windowHandle = default, Func<bool>? positionBeforeReveal = null)
        {
            if (!(bool)window.GetValue(IsOffscreenFirstRenderStagedProperty))
            {
                return;
            }

            double finalLeft = (double)window.GetValue(FinalFirstPresentationLeftProperty);
            double finalTop = (double)window.GetValue(FinalFirstPresentationTopProperty);
            bool positioned = positionBeforeReveal?.Invoke() ?? false;
            if (!positioned && (bool)window.GetValue(CenterFirstPresentationProperty))
            {
                positioned = WindowPlacementHelper.TryCenterOnPrimaryWorkArea(windowHandle);
            }
            if (!positioned && windowHandle != nint.Zero
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
            window.ClearValue(CenterFirstPresentationProperty);
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

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioPilot.Helpers
{
    internal readonly record struct DialogConfirmationDecision(bool HasExpectedViewModel, bool CanConfirm)
    {
        public bool ShouldConfirm => HasExpectedViewModel && CanConfirm;
    }

    internal readonly record struct DialogScreenRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Math.Max(0, Right - Left);

        public int Height => Math.Max(0, Bottom - Top);
    }

    internal readonly record struct DialogScreenPosition(int Left, int Top);
    internal readonly record struct DialogScreenSize(int Width, int Height);

    internal static partial class DialogWindowHelper
    {
        public static void Initialize(Window window, object viewModel)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(viewModel);

            window.DataContext = viewModel;
        }

        public static void ApplyOwnerOrMainWindowTheme(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            WindowThemeResolver.ApplyOwnerOrMainWindowTheme(window);
        }

        public static bool? ShowOwnedDialog(Window dialog, Window? owner = null, Action? onPresented = null)
        {
            ArgumentNullException.ThrowIfNull(dialog);

            if (owner != null)
            {
                dialog.Owner = owner;
            }

            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            WindowFirstPresentationHelper.Prepare(dialog, hideFromTaskbar: false);
            WindowThemeResolver.ApplyOwnerOrMainWindowTheme(dialog);
            WindowFirstPresentationHelper.StageOffscreenFirstRender(dialog);
            WindowFirstPresentationHelper.BeginOffscreenFirstRender(dialog);
            using var workAreaLifetime = new DialogWorkAreaLifetime(dialog);
            using var presentationCancellation = new CancellationTokenSource();
            bool closed = false;
            ExceptionDispatchInfo? presentationFailure = null;

            void sourceInitializedHandler(object? _1, EventArgs _2)
            {
                dialog.SourceInitialized -= sourceInitializedHandler;
                WindowThemeResolver.ApplyOwnerOrMainWindowTheme(dialog);
            }

            void closedHandler(object? _1, EventArgs _2)
            {
                closed = true;
                presentationCancellation.Cancel();
            }

            async Task RevealDialogAsync()
            {
                try
                {
                    bool revealed = await WindowFirstPresentationHelper.RevealAsync(
                        dialog,
                        activate: true,
                        canReveal: () => !closed && dialog.IsVisible,
                        waitForFirstRender: true,
                        cancellationToken: presentationCancellation.Token,
                        positionBeforeReveal: () => TryCenterBeforeReveal(dialog, owner));
                    if (revealed)
                    {
                        dialog.ShowActivated = true;
                        onPresented?.Invoke();
                    }
                    else if (!closed)
                    {
                        throw new InvalidOperationException("The native dialog did not complete presentation.");
                    }
                }
                catch (OperationCanceledException) when (presentationCancellation.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    presentationFailure = ExceptionDispatchInfo.Capture(ex);
                    if (!closed)
                    {
                        dialog.Close();
                    }
                }
            }

            dialog.SourceInitialized += sourceInitializedHandler;
            dialog.Closed += closedHandler;
            try
            {
                _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(dialog, ensureHandle: true);
                _ = RevealDialogAsync();
                presentationFailure?.Throw();
                bool? result = dialog.ShowDialog();
                presentationFailure?.Throw();
                return result;
            }
            finally
            {
                presentationCancellation.Cancel();
                dialog.SourceInitialized -= sourceInitializedHandler;
                dialog.Closed -= closedHandler;
            }
        }

        public static bool? ShowAppOwnedDialog(Window dialog)
        {
            ArgumentNullException.ThrowIfNull(dialog);

            return ShowOwnedDialog(dialog, Application.Current?.MainWindow);
        }

        public static bool TryGetViewModel<TViewModel>(
            Window window,
            [NotNullWhen(true)] out TViewModel? viewModel,
            bool setDialogResultOnFailure = false)
            where TViewModel : class
        {
            ArgumentNullException.ThrowIfNull(window);

            viewModel = window.DataContext as TViewModel;
            if (viewModel != null)
            {
                return true;
            }

            if (setDialogResultOnFailure)
            {
                window.DialogResult = false;
            }

            return false;
        }

        internal static DialogConfirmationDecision ResolveConfirmationDecision<TViewModel>(
            object? dataContext,
            Func<TViewModel, bool> canConfirm)
            where TViewModel : class
        {
            ArgumentNullException.ThrowIfNull(canConfirm);

            if (dataContext is not TViewModel viewModel)
            {
                return new DialogConfirmationDecision(HasExpectedViewModel: false, CanConfirm: false);
            }

            return new DialogConfirmationDecision(HasExpectedViewModel: true, CanConfirm: canConfirm(viewModel));
        }

        internal static DialogScreenPosition CalculateCenteredPosition(
            DialogScreenRect anchorBounds,
            DialogScreenRect workArea,
            int dialogWidth,
            int dialogHeight)
        {
            int safeDialogWidth = Math.Max(0, dialogWidth);
            int safeDialogHeight = Math.Max(0, dialogHeight);
            int centeredLeft = anchorBounds.Left + ((anchorBounds.Width - safeDialogWidth) / 2);
            int centeredTop = anchorBounds.Top + ((anchorBounds.Height - safeDialogHeight) / 2);
            int maximumLeft = Math.Max(workArea.Left, workArea.Right - safeDialogWidth);
            int maximumTop = Math.Max(workArea.Top, workArea.Bottom - safeDialogHeight);

            return new DialogScreenPosition(
                Math.Clamp(centeredLeft, workArea.Left, maximumLeft),
                Math.Clamp(centeredTop, workArea.Top, maximumTop));
        }

        internal static DialogScreenSize CalculateBoundedSize(
            DialogScreenRect workArea,
            int dialogWidth,
            int dialogHeight,
            int margin = 16)
        {
            int totalMargin = Math.Max(0, margin) * 2;
            int availableWidth = Math.Max(1, workArea.Width - totalMargin);
            int availableHeight = Math.Max(1, workArea.Height - totalMargin);
            return new DialogScreenSize(
                Math.Min(Math.Max(1, dialogWidth), availableWidth),
                Math.Min(Math.Max(1, dialogHeight), availableHeight));
        }

        internal static bool TryCenterBeforeReveal(Window dialog, Window? requestedOwner) =>
            TryFitToWorkArea(dialog, requestedOwner, center: true);

        private static readonly ConditionalWeakTable<Window, DialogSizeConstraints> SizeConstraints = [];

        private sealed record DialogSizeConstraints(double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

        internal static void ApplyWorkAreaConstraints(Window dialog, DialogScreenSize availablePixels, double dpiScale)
        {
            DialogSizeConstraints original = SizeConstraints.GetValue(dialog,
                static window => new(window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight));
            double width = availablePixels.Width / dpiScale;
            double height = availablePixels.Height / dpiScale;
            dialog.SetCurrentValue(Window.MinWidthProperty, Math.Min(original.MinWidth, width));
            dialog.SetCurrentValue(Window.MinHeightProperty, Math.Min(original.MinHeight, height));
            dialog.SetCurrentValue(Window.MaxWidthProperty, Math.Min(original.MaxWidth, width));
            dialog.SetCurrentValue(Window.MaxHeightProperty, Math.Min(original.MaxHeight, height));
        }

        internal static bool TryFitToWorkArea(Window dialog, Window? requestedOwner, bool center)
        {
            try
            {
                nint dialogHandle = new WindowInteropHelper(dialog).Handle;
                if (dialogHandle == 0 || !NativeMethods.GetWindowRect(dialogHandle, out NativeMethods.Rect dialogBounds))
                {
                    return false;
                }

                Window? effectiveOwner = center ? requestedOwner ?? dialog.Owner : null;
                nint ownerHandle = effectiveOwner == null ? 0 : new WindowInteropHelper(effectiveOwner).Handle;
                nint monitor = NativeMethods.MonitorFromWindow(center ? ownerHandle : dialogHandle,
                    center && ownerHandle == 0 ? NativeMethods.MonitorDefaultToPrimary : NativeMethods.MonitorDefaultToNearest);
                var monitorInfo = new NativeMethods.MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>(),
                };

                if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    return false;
                }

                DialogScreenRect workArea = monitorInfo.WorkArea.ToScreenRect();
                DialogScreenRect anchorBounds = ownerHandle != 0
                    && !NativeMethods.IsIconic(ownerHandle)
                    && NativeMethods.GetWindowRect(ownerHandle, out NativeMethods.Rect ownerBounds)
                        ? ownerBounds.ToScreenRect()
                        : workArea;
                DialogScreenSize boundedSize = CalculateBoundedSize(
                    workArea,
                    dialogBounds.Width,
                    dialogBounds.Height);
                double dpiScale = NativeMethods.GetDpiForWindow(dialogHandle) / 96d;
                if (dpiScale <= 0) return false;
                ApplyWorkAreaConstraints(dialog, CalculateBoundedSize(workArea, int.MaxValue, int.MaxValue), dpiScale);
                bool resizeToWorkArea = boundedSize.Width != dialogBounds.Width || boundedSize.Height != dialogBounds.Height;
                if (resizeToWorkArea)
                {
                    dialog.SizeToContent = SizeToContent.Manual;
                    dialog.SetCurrentValue(Window.WidthProperty, boundedSize.Width / dpiScale);
                    dialog.SetCurrentValue(Window.HeightProperty, boundedSize.Height / dpiScale);
                }

                DialogScreenPosition position = center
                    ? CalculateCenteredPosition(anchorBounds, workArea, boundedSize.Width, boundedSize.Height)
                    : new DialogScreenPosition(
                        Math.Clamp(dialogBounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - boundedSize.Width)),
                        Math.Clamp(dialogBounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - boundedSize.Height)));

                bool positioned = NativeMethods.SetWindowPos(
                    dialogHandle,
                    0,
                    position.Left,
                    position.Top,
                    boundedSize.Width,
                    boundedSize.Height,
                    (resizeToWorkArea ? 0u : NativeMethods.SetWindowPosNoSize) |
                    NativeMethods.SetWindowPosNoZOrder |
                    NativeMethods.SetWindowPosNoActivate);
                return positioned && NativeMethods.GetWindowRect(dialogHandle, out NativeMethods.Rect actual)
                    && actual.Left >= workArea.Left && actual.Top >= workArea.Top
                    && actual.Right <= workArea.Right && actual.Bottom <= workArea.Bottom;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static partial class NativeMethods
        {
            internal const uint MonitorDefaultToPrimary = 1;
            internal const uint MonitorDefaultToNearest = 2;
            internal const uint SetWindowPosNoSize = 0x0001;
            internal const uint SetWindowPosNoZOrder = 0x0004;
            internal const uint SetWindowPosNoActivate = 0x0010;

            [StructLayout(LayoutKind.Sequential)]
            internal struct Rect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;

                public readonly int Width => Math.Max(0, Right - Left);

                public readonly int Height => Math.Max(0, Bottom - Top);

                public readonly DialogScreenRect ToScreenRect() => new(Left, Top, Right, Bottom);
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct MonitorInfo
            {
                public uint Size;
                public Rect Monitor;
                public Rect WorkArea;
                public uint Flags;
            }

            [LibraryImport("user32.dll")]
            internal static partial uint GetDpiForWindow(nint windowHandle);

            [LibraryImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static partial bool GetWindowRect(nint windowHandle, out Rect bounds);

            [LibraryImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static partial bool IsIconic(nint windowHandle);

            [LibraryImport("user32.dll")]
            internal static partial nint MonitorFromWindow(nint windowHandle, uint flags);

            [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

            [LibraryImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static partial bool SetWindowPos(
                nint windowHandle,
                nint insertAfter,
                int x,
                int y,
                int width,
                int height,
                uint flags);
        }
    }
}

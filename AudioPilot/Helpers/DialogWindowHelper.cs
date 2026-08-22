using System.Diagnostics.CodeAnalysis;
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

        public static bool? ShowOwnedDialog(Window dialog, Window? owner = null)
        {
            ArgumentNullException.ThrowIfNull(dialog);

            if (owner != null)
            {
                dialog.Owner = owner;
            }

            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            WindowFirstPresentationHelper.Prepare(dialog, hideFromTaskbar: false);
            WindowThemeResolver.ApplyOwnerOrMainWindowTheme(dialog);

            void sourceInitializedHandler(object? _1, EventArgs _2)
            {
                dialog.SourceInitialized -= sourceInitializedHandler;
                WindowThemeResolver.ApplyOwnerOrMainWindowTheme(dialog);
                TryCenterBeforeFirstRender(dialog, owner);
            }

            void contentRenderedHandler(object? _1, EventArgs _2)
            {
                dialog.ContentRendered -= contentRenderedHandler;
                dialog.Opacity = 1d;
                dialog.ShowActivated = true;
                WindowFirstPresentationHelper.Activate(dialog);
            }

            dialog.SourceInitialized += sourceInitializedHandler;
            dialog.ContentRendered += contentRenderedHandler;
            try
            {
                return dialog.ShowDialog();
            }
            finally
            {
                dialog.SourceInitialized -= sourceInitializedHandler;
                dialog.ContentRendered -= contentRenderedHandler;
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

        public static bool TryConfirm<TViewModel>(
            Window window,
            Func<TViewModel, bool> canConfirm,
            bool setDialogResultOnFailure)
            where TViewModel : class
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(canConfirm);

            DialogConfirmationDecision decision = ResolveConfirmationDecision(window.DataContext, canConfirm);
            if (!decision.HasExpectedViewModel)
            {
                if (setDialogResultOnFailure)
                {
                    window.DialogResult = false;
                }

                return false;
            }

            if (!decision.CanConfirm)
            {
                if (setDialogResultOnFailure)
                {
                    window.DialogResult = false;
                }

                return false;
            }

            window.DialogResult = true;
            return true;
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

        internal static void TryCenterBeforeFirstRender(Window dialog, Window? requestedOwner)
        {
            try
            {
                nint dialogHandle = new WindowInteropHelper(dialog).Handle;
                if (dialogHandle == 0 || !NativeMethods.GetWindowRect(dialogHandle, out NativeMethods.Rect dialogBounds))
                {
                    return;
                }

                Window? effectiveOwner = requestedOwner ?? dialog.Owner;
                nint ownerHandle = effectiveOwner == null ? 0 : new WindowInteropHelper(effectiveOwner).Handle;
                nint monitorAnchor = ownerHandle != 0 ? ownerHandle : dialogHandle;
                nint monitor = NativeMethods.MonitorFromWindow(monitorAnchor, NativeMethods.MonitorDefaultToNearest);
                var monitorInfo = new NativeMethods.MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>(),
                };

                if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    return;
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
                bool resizeToWorkArea = boundedSize.Width != dialogBounds.Width || boundedSize.Height != dialogBounds.Height;
                if (resizeToWorkArea)
                {
                    dialog.SizeToContent = SizeToContent.Manual;
                }

                DialogScreenPosition position = CalculateCenteredPosition(
                    anchorBounds,
                    workArea,
                    boundedSize.Width,
                    boundedSize.Height);

                _ = NativeMethods.SetWindowPos(
                    dialogHandle,
                    0,
                    position.Left,
                    position.Top,
                    boundedSize.Width,
                    boundedSize.Height,
                    (resizeToWorkArea ? 0u : NativeMethods.SetWindowPosNoSize) |
                    NativeMethods.SetWindowPosNoZOrder |
                    NativeMethods.SetWindowPosNoActivate);
            }
            catch (Exception)
            {
                // Dialog positioning is best effort; showing the dialog is more important than centering it.
            }
        }

        private static partial class NativeMethods
        {
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

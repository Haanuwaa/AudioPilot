using System.Windows;

namespace AudioPilot.Services.UI
{
    internal interface IAppDialogPresenter
    {
        Task<AppDialogResult> PresentAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken,
            Action<AppDialogKind>? onPresented = null);
        bool TryUpdateAcknowledgement(AppDialogRequest request, int repetitionCount);
        void CloseActive(AppDialogResult result);
    }

    internal sealed class AppDialogWindowPresenter : IAppDialogPresenter
    {
        private AppDialogWindow? _activeWindow;

        public Task<AppDialogResult> PresentAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken,
            Action<AppDialogKind>? onPresented = null)
        {
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                return Application.Current.Dispatcher.InvokeAsync(
                    () => PresentAsync(request, cancellationToken, onPresented)).Task.Unwrap();
            }

            var completion = new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new AppDialogWindow(request);
            _activeWindow = window;

            Window? owner = ResolveOwner(request.Owner, window);
            window.Completed += result => completion.TrySetResult(result);
            if (onPresented != null)
            {
                window.FirstPresented += () => onPresented(window.Request.Kind);
            }
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state =>
                {
                    var dialog = (AppDialogWindow)state!;
                    dialog.Complete(AppDialogResult.Cancelled);
                },
                window);

            try
            {
                _ = Helpers.DialogWindowHelper.ShowOwnedDialog(window, owner);
                completion.TrySetResult(window.Result);
            }
            finally
            {
                if (ReferenceEquals(_activeWindow, window))
                {
                    _activeWindow = null;
                }
            }

            return completion.Task;
        }

        public bool TryUpdateAcknowledgement(AppDialogRequest request, int repetitionCount)
        {
            if (_activeWindow == null || !_activeWindow.Request.IsAcknowledgement)
            {
                return false;
            }

            _activeWindow.UpdateAcknowledgement(request, repetitionCount);
            return true;
        }

        public void CloseActive(AppDialogResult result)
        {
            _activeWindow?.Complete(result);
        }

        private static Window? ResolveOwner(Window? explicitOwner, Window dialog)
        {
            Window? owner = ResolveOwnerForCurrentApplication(explicitOwner);
            dialog.ShowInTaskbar = owner == null;
            return owner;
        }

        internal static Window? ResolveOwnerForCurrentApplication(Window? explicitOwner)
        {
            Application? application = Application.Current;
            if (application?.Dispatcher is not { HasShutdownStarted: false, HasShutdownFinished: false } dispatcher
                || !dispatcher.CheckAccess())
            {
                return null;
            }

            return ResolveOwnerCandidate(
                explicitOwner,
                application.Windows.OfType<Window>(),
                application.MainWindow,
                IsValidOwner,
                static window => window.IsActive);
        }

        internal static Window? ResolveOwnerCandidate(
            Window? explicitOwner,
            IEnumerable<Window> windows,
            Window? mainWindow,
            Func<Window?, bool> isValidOwner,
            Func<Window, bool> isActive)
        {
            ArgumentNullException.ThrowIfNull(windows);
            ArgumentNullException.ThrowIfNull(isValidOwner);
            ArgumentNullException.ThrowIfNull(isActive);

            if (isValidOwner(explicitOwner))
            {
                return explicitOwner;
            }

            Window? active = windows.FirstOrDefault(window => isActive(window) && isValidOwner(window));
            if (active != null)
            {
                return active;
            }

            return isValidOwner(mainWindow) && mainWindow!.WindowState != WindowState.Minimized
                ? mainWindow
                : null;
        }

        private static bool IsValidOwner(Window? window)
        {
            return window is { IsVisible: true, WindowState: not WindowState.Minimized }
                && window.Dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false };
        }
    }
}

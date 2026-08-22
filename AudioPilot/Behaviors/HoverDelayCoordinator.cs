using System.Windows;

namespace AudioPilot.Behaviors
{
    internal static class HoverDelayCoordinator
    {
        public static async Task ExecuteAfterDelayAsync(
            FrameworkElement target,
            int delayMs,
            Func<bool> shouldContinue,
            Action action,
            Action<Exception> onError,
            CancellationToken hoverDelayToken)
        {
            Window? owner = Window.GetWindow(target);
            if (owner == null || !target.IsVisible || !owner.IsVisible) return;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(hoverDelayToken);
            bool finished = false;
            owner.Deactivated += OnOwnerInvalidated;
            owner.Closed += OnOwnerInvalidated;
            owner.IsVisibleChanged += OnOwnerVisibilityChanged;
            owner.StateChanged += OnOwnerStateChanged;
            try
            {
                await Task.Delay(delayMs, lifetime.Token);

                if (lifetime.IsCancellationRequested || !target.IsVisible || !target.IsEnabled || !owner.IsVisible
                    || owner.WindowState == WindowState.Minimized || !ReferenceEquals(Window.GetWindow(target), owner)
                    || owner.Dispatcher.HasShutdownStarted || owner.Dispatcher.HasShutdownFinished || !shouldContinue())
                {
                    return;
                }

                action();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                onError(ex);
            }
            finally
            {
                finished = true;
                owner.Deactivated -= OnOwnerInvalidated;
                owner.Closed -= OnOwnerInvalidated;
                owner.IsVisibleChanged -= OnOwnerVisibilityChanged;
                owner.StateChanged -= OnOwnerStateChanged;
            }

            void OnOwnerInvalidated(object? sender, EventArgs e)
            {
                if (!finished) lifetime.Cancel();
            }
            void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (e.NewValue is false) OnOwnerInvalidated(sender, EventArgs.Empty);
            }
            void OnOwnerStateChanged(object? sender, EventArgs e)
            {
                if (owner.WindowState == WindowState.Minimized) OnOwnerInvalidated(sender, EventArgs.Empty);
            }
        }

        public static CancellationToken StartOrRestart(ref CancellationTokenSource? hoverDelayCts)
        {
            CancellationTokenSource? previous = hoverDelayCts;
            if (previous != null)
            {
                previous.Cancel();
                previous.Dispose();
            }

            var replacement = new CancellationTokenSource();
            hoverDelayCts = replacement;
            return replacement.Token;
        }

        public static void CancelAndDispose(ref CancellationTokenSource? hoverDelayCts)
        {
            CancellationTokenSource? current = hoverDelayCts;
            hoverDelayCts = null;

            if (current == null)
            {
                return;
            }

            current.Cancel();
            current.Dispose();
        }
    }
}

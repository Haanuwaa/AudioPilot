namespace AudioPilot.Behaviors
{
    internal static class HoverDelayCoordinator
    {
        public static async Task ExecuteAfterDelayAsync(
            int delayMs,
            Func<bool> shouldContinue,
            Action action,
            Action<Exception> onError,
            CancellationToken hoverDelayToken)
        {
            try
            {
                await Task.Delay(delayMs, hoverDelayToken);

                if (hoverDelayToken.IsCancellationRequested || !shouldContinue())
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

        public static void Cancel(CancellationTokenSource? hoverDelayCts)
        {
            hoverDelayCts?.Cancel();
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

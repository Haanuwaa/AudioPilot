namespace AudioPilot.Coordinators
{
    internal static class AppDebouncedBackgroundWorkCoordinator
    {
        public static CancellationTokenSource BeginDebounce(ref CancellationTokenSource? current)
        {
            return BeginDebounce(ref current, out _);
        }

        public static CancellationTokenSource BeginDebounce(ref CancellationTokenSource? current, out bool replacedPrevious)
        {
            var nextDebounceCts = new CancellationTokenSource();
            CancellationTokenSource? previousDebounceCts = Interlocked.Exchange(ref current, nextDebounceCts);
            replacedPrevious = previousDebounceCts != null;
            CancelAndDispose(previousDebounceCts);
            return nextDebounceCts;
        }

        public static CancellationTokenSource BeginDebounce(Func<CancellationTokenSource, CancellationTokenSource?> swapDebounce)
        {
            var nextDebounceCts = new CancellationTokenSource();
            CancellationTokenSource? previousDebounceCts = swapDebounce(nextDebounceCts);
            CancelAndDispose(previousDebounceCts);

            return nextDebounceCts;
        }

        public static CancellationTokenSource? CancelAndDetach(ref CancellationTokenSource? current)
        {
            CancellationTokenSource? detached = Interlocked.Exchange(ref current, null);
            detached?.Cancel();
            return detached;
        }

        public static void CancelAndDispose(ref CancellationTokenSource? current)
        {
            CancellationTokenSource? detached = CancelAndDetach(ref current);
            detached?.Dispose();
        }

        public static void ReleaseOwned(ref CancellationTokenSource? current, CancellationTokenSource ownedDebounce)
        {
            Interlocked.CompareExchange(ref current, null, ownedDebounce);
            ownedDebounce.Dispose();
        }

        /// <summary>
        /// Executes debounced background work under a linked debounce and shutdown token.
        /// </summary>
        /// <remarks>
        /// Cancellation is treated as an expected outcome and is swallowed. The owned debounce token is always
        /// released in <c>finally</c> so later requests cannot get stuck behind abandoned debounce state.
        /// </remarks>
        public static async Task ExecuteAsync(
            CancellationTokenSource ownedDebounceCts,
            Action<CancellationTokenSource> releaseOwnedDebounce,
            Func<CancellationToken, Task> workAsync,
            CancellationToken shutdownToken)
        {
            try
            {
                using var linkedCts = CreateLinkedTokenSourceOrNull(ownedDebounceCts, shutdownToken);
                if (linkedCts == null)
                {
                    return;
                }

                await workAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                releaseOwnedDebounce(ownedDebounceCts);
            }
        }

        public static Task ExecuteDelayedAsync(
            CancellationTokenSource ownedDebounceCts,
            Action<CancellationTokenSource> releaseOwnedDebounce,
            int delayMs,
            Func<CancellationToken, Task> workAsync,
            CancellationToken shutdownToken)
        {
            return ExecuteAsync(
                ownedDebounceCts,
                releaseOwnedDebounce,
                async linkedToken =>
                {
                    await Task.Delay(delayMs, linkedToken);
                    await workAsync(linkedToken);
                },
                shutdownToken);
        }

        private static void CancelAndDispose(CancellationTokenSource? current)
        {
            if (current == null)
            {
                return;
            }

            current.Cancel();
            current.Dispose();
        }

        private static CancellationTokenSource? CreateLinkedTokenSourceOrNull(
            CancellationTokenSource ownedDebounceCts,
            CancellationToken shutdownToken)
        {
            try
            {
                return CancellationTokenSource.CreateLinkedTokenSource(ownedDebounceCts.Token, shutdownToken);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }
}

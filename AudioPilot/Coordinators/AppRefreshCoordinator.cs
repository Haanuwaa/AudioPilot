namespace AudioPilot.Coordinators
{
    internal sealed class AppRefreshCoordinator
    {
        private int _refreshCycleState;
        private int _pendingRefreshSignal;

        public bool IsRefreshing => Volatile.Read(ref _refreshCycleState) != 0;

        public bool TryBeginRefreshCycle()
        {
            return Interlocked.CompareExchange(ref _refreshCycleState, 1, 0) == 0;
        }

        public void MarkPendingRefresh()
        {
            Interlocked.Exchange(ref _pendingRefreshSignal, 1);
        }

        public bool TryConsumePendingRefresh()
        {
            return Interlocked.Exchange(ref _pendingRefreshSignal, 0) != 0;
        }

        public void EndRefreshCycle()
        {
            Interlocked.Exchange(ref _refreshCycleState, 0);
        }

        public bool EndRefreshCycleAndTryRestart()
        {
            EndRefreshCycle();

            if (!TryConsumePendingRefresh())
            {
                return false;
            }

            return TryBeginRefreshCycle();
        }

        public static bool HasSettingsTimestampChanged(DateTime currentWriteTime, DateTime lastWriteTime)
        {
            return currentWriteTime != lastWriteTime;
        }
    }
}

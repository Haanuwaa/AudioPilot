using AudioPilot.Constants;

namespace AudioPilot.Coordinators
{
    internal enum MinimizeAttemptResult
    {
        Started,
        Cooldown,
        AlreadyMinimizing,
    }

    internal sealed class AppWindowStateCoordinator
    {
        private volatile bool _isMinimizing;
        private DateTime _lastShowTime;
        private bool _startupVisibilityResolved;

        public bool ShowBalloonOnFirstMinimize { get; set; }
        public bool HasInteractiveShowRequest { get; private set; }
        public bool IsStartupVisibilityResolved => _startupVisibilityResolved;

        public void RequestInteractiveShow()
        {
            HasInteractiveShowRequest = true;
        }

        public void MarkStartupVisibilityResolved()
        {
            _startupVisibilityResolved = true;
        }

        public void MarkShown(DateTime now)
        {
            _isMinimizing = false;
            _lastShowTime = now;
            RequestInteractiveShow();
        }

        public MinimizeAttemptResult TryBeginMinimize(DateTime now)
        {
            if ((now - _lastShowTime).TotalMilliseconds < AppConstants.Timing.ShowCooldownMs)
            {
                return MinimizeAttemptResult.Cooldown;
            }

            if (_isMinimizing)
            {
                return MinimizeAttemptResult.AlreadyMinimizing;
            }

            _isMinimizing = true;
            return MinimizeAttemptResult.Started;
        }

        public void CompleteMinimize()
        {
            _isMinimizing = false;
        }

        public void AbortMinimize()
        {
            _isMinimizing = false;
        }
    }
}

using System.Windows;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal sealed class MainWindowVisibilityCoordinator(Logger logger)
    {
        private readonly Logger _logger = logger;
        private bool _autoScrollPending;

        public void HandleWindowStateChanged(
            WindowState state,
            bool isWindowVisible,
            Action minimizeAppVm)
        {
            if (state != WindowState.Minimized || !isWindowVisible)
            {
                return;
            }

            _logger.Debug("MainWindow", "main-window-minimize-to-tray-redirect");
            minimizeAppVm();
        }

        public void MarkPendingAutoScrollOnNextShow()
        {
            _autoScrollPending = true;
        }

        public void HandleVisibleChanged(
            bool isVisible,
            Func<bool> isEditorTabActive,
            Func<bool> isAutoScrollEnabled,
            Action scheduleScroll)
        {
            if (!_autoScrollPending || !isVisible)
            {
                return;
            }

            _autoScrollPending = false;
            if (isEditorTabActive() && isAutoScrollEnabled()) scheduleScroll();
        }
    }
}

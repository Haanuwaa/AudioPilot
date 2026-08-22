using System.Windows.Media;

namespace AudioPilot.Services.UI
{
    /// <summary>
    /// Small application-shell facade over independently owned tray and main-window services.
    /// It never owns or materializes either resource itself.
    /// </summary>
    public sealed class AppShellService : IDisposable
    {
        private readonly IAppMainWindowManager _windowManager;
        private readonly IAppTrayIconService _trayService;

        internal AppShellService(
            IAppMainWindowManager windowManager,
            IAppTrayIconService trayService)
        {
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        }

        public bool IsWindowVisible => _windowManager.IsVisible;

        internal Task<bool> ShowWindowFrontAndCenterAsync(
            MainWindowOpenTarget target = MainWindowOpenTarget.Default,
            CancellationToken cancellationToken = default) =>
            _windowManager.ShowAsync(target, cancellationToken);

        internal bool PrepareHiddenStartup() => _windowManager.Hide();

        public bool MinimizeToTray(bool showBalloon = false, string? appName = null)
        {
            if (!_trayService.EnsureVisible() || !_windowManager.Hide())
            {
                return false;
            }

            if (showBalloon && !string.IsNullOrWhiteSpace(appName))
            {
                _trayService.ShowBalloon(appName, "The application is still running in the background.");
            }

            return true;
        }

        public void RefreshIconsForCurrentDpi()
        {
            MainWindow? currentWindow = _windowManager.CurrentWindow;
            currentWindow?.Icon = AppIconImageProvider.GetSharedIconFrameForDpi(VisualTreeHelper.GetDpi(currentWindow).DpiScaleX);
        }

        public void Dispose()
        {
            // AppRuntimeHost owns both services and disposes them in lifecycle order.
        }
    }
}

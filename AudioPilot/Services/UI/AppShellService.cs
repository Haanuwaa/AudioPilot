using System.Windows.Media;

namespace AudioPilot.Services.UI
{
    internal enum BackgroundFailureKind
    {
        AutoSave,
        ResumeRecovery,
    }

    /// <summary>
    /// Small application-shell facade over independently owned tray and main-window services.
    /// It never owns or materializes either resource itself.
    /// </summary>
    public sealed class AppShellService : IDisposable
    {
        private readonly IAppMainWindowManager _windowManager;
        private readonly IAppTrayIconService _trayService;
        private readonly TimeProvider _timeProvider;
        private readonly Lock _notificationLock = new();
        private readonly Dictionary<BackgroundFailureKind, long> _lastNotification = [];

        internal AppShellService(
            IAppMainWindowManager windowManager,
            IAppTrayIconService trayService,
            TimeProvider? timeProvider = null)
        {
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public bool IsWindowVisible => _windowManager.IsVisible;

        /// <summary>Reports actionable background failures without opening a window or repeating each failed retry.</summary>
        internal void NotifyBackgroundFailure(BackgroundFailureKind kind)
        {
            (string title, string message, MainWindowOpenTarget target) = kind switch
            {
                BackgroundFailureKind.AutoSave => ("Changes could not be saved",
                    "AudioPilot could not automatically save your changes. Click to review settings and try saving again.",
                    MainWindowOpenTarget.Settings),
                BackgroundFailureKind.ResumeRecovery => ("AudioPilot needs attention after sleep",
                    "Audio or some hotkeys could not be restored. Click to open AudioPilot; restart it if the problem continues.",
                    MainWindowOpenTarget.Default),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

            lock (_notificationLock)
            {
                long now = _timeProvider.GetTimestamp();
                if (_lastNotification.TryGetValue(kind, out long previous)
                    && _timeProvider.GetElapsedTime(previous, now) < TimeSpan.FromMinutes(1))
                {
                    return;
                }

                _lastNotification[kind] = now;
            }

            _trayService.ShowBalloon(title, message, warning: true, target);
        }

        internal Task<bool> ShowWindowFrontAndCenterAsync(
            MainWindowOpenTarget target = MainWindowOpenTarget.Default,
            CancellationToken cancellationToken = default) =>
            _windowManager.ShowAsync(target, cancellationToken);

        internal void NotifyUpcomingScheduledRoutines(IReadOnlyList<string> routineNames)
        {
            if (routineNames.Count == 0)
            {
                return;
            }

            string title = routineNames.Count == 1 ? "Scheduled routine starts soon" : $"{routineNames.Count} scheduled routines start soon";
            string names = string.Join(", ", routineNames.Take(3).Select(FormatRoutineNotificationName));
            if (routineNames.Count > 3)
            {
                names += $" and {routineNames.Count - 3} more";
            }
            _trayService.ShowBalloon(title, $"{names}. Click to view your routines.", target: MainWindowOpenTarget.Routines);
        }

        /// <summary>Bounds notification labels without splitting emoji sequences or other text elements.</summary>
        private static string FormatRoutineNotificationName(string name)
        {
            string text = name.Trim();
            if (text.Length > 40)
            {
                int length = 0;
                while (length < text.Length)
                {
                    int next = length + System.Globalization.StringInfo.GetNextTextElementLength(text.AsSpan(length));
                    if (next > 40)
                    {
                        break;
                    }
                    length = next;
                }
                text = text[..length] + "…";
            }
            return text.ReplaceLineEndings(" ");
        }

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
        }
    }
}

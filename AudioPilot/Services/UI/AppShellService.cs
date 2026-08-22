using System.Windows.Media;

namespace AudioPilot.Services.UI
{
    internal enum BackgroundFailureKind
    {
        AutoSave,
        ResumeRecovery,
        Hotkeys,
        AutomaticRoutine,
    }

    /// <summary>
    /// Small application-shell facade over independently owned tray and main-window services.
    /// It never owns or materializes either resource itself.
    /// </summary>
    public sealed class AppShellService : IDisposable
    {
        private readonly IAppMainWindowManager _windowManager;
        private readonly IAppTrayIconService _trayService;
        private readonly Lock _notificationLock = new();
        private readonly Dictionary<BackgroundFailureKind, object> _notifiedFailures = [];
        private readonly HashSet<string> _failedRoutines = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        internal AppShellService(
            IAppMainWindowManager windowManager,
            IAppTrayIconService trayService)
        {
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        }

        public bool IsWindowVisible => _windowManager.IsVisible;

        /// <summary>Reports actionable background failures without opening a window or repeating each failed retry.</summary>
        internal Task NotifyBackgroundFailureAsync(BackgroundFailureKind kind)
        {
            (string title, string message) = kind switch
            {
                BackgroundFailureKind.AutoSave => ("Changes could not be saved",
                    "AudioPilot could not automatically save your changes. Open AudioPilot, then review Settings and try saving again."),
                BackgroundFailureKind.ResumeRecovery => ("AudioPilot needs attention after sleep",
                    "Audio or some hotkeys could not be restored. Click to open AudioPilot; restart it if the problem continues."),
                BackgroundFailureKind.Hotkeys => ("Some shortcuts are unavailable",
                    "One or more configured shortcuts could not be activated. Open AudioPilot, then review your hotkey settings."),
                BackgroundFailureKind.AutomaticRoutine => ("Automatic routines need attention",
                    "One or more automatic routines could not apply their audio actions. Open AudioPilot, then check Routines for last-run details."),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

            var attempt = new object();
            lock (_notificationLock)
            {
                if (_disposed || IsWindowVisible || !_notifiedFailures.TryAdd(kind, attempt)) return Task.CompletedTask;
            }

            return DeliverBackgroundFailureAsync(kind, attempt, title, message);
        }

        private async Task DeliverBackgroundFailureAsync(BackgroundFailureKind kind, object attempt, string title, string message)
        {
            bool accepted = false;
            try
            {
                accepted = await _trayService.ShowBalloonAsync(title, message, warning: true, canShow: CanShow);
            }
            catch (Exception ex)
            {
                Logging.Logger.Instance.Warning("AppShellService", "background-notification-failed", nameof(DeliverBackgroundFailureAsync), ex);
            }
            finally
            {
                lock (_notificationLock)
                    if (!accepted && _notifiedFailures.TryGetValue(kind, out object? current) && ReferenceEquals(current, attempt))
                        _notifiedFailures.Remove(kind);
            }

            bool CanShow()
            {
                lock (_notificationLock)
                    return !_disposed && !IsWindowVisible && _notifiedFailures.TryGetValue(kind, out object? current) && ReferenceEquals(current, attempt);
            }
        }

        internal void ClearBackgroundFailure(BackgroundFailureKind kind)
        {
            lock (_notificationLock) _notifiedFailures.Remove(kind);
        }

        internal void ReportHotkeyAvailability(Logging.HotkeyRuntimeSnapshot snapshot)
        {
            if (!snapshot.Initialized || snapshot.ShuttingDown || snapshot.CaptureSuspended) return;
            bool failed = snapshot.Registrations.Any(static item => item.LastOutcome is not ("None" or "Registered" or "Fallback"));
            if (failed) _ = NotifyBackgroundFailureAsync(BackgroundFailureKind.Hotkeys);
            else ClearBackgroundFailure(BackgroundFailureKind.Hotkeys);
        }

        internal void ReportRoutineResult(string routineId, bool enabled, bool success, bool skipped, bool automatic)
        {
            lock (_notificationLock)
            {
                if (_disposed) return;
                if (success || !enabled)
                {
                    _failedRoutines.Remove(routineId);
                    if (_failedRoutines.Count == 0) _notifiedFailures.Remove(BackgroundFailureKind.AutomaticRoutine);
                    return;
                }
                if (skipped || !automatic) return;
                _failedRoutines.Add(routineId);
            }
            _ = NotifyBackgroundFailureAsync(BackgroundFailureKind.AutomaticRoutine);
        }

        internal void RetainRoutineFailures(IEnumerable<string> configuredIds)
        {
            var retained = configuredIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (_notificationLock)
            {
                _failedRoutines.IntersectWith(retained);
                if (_failedRoutines.Count == 0) _notifiedFailures.Remove(BackgroundFailureKind.AutomaticRoutine);
            }
        }

        internal Task<bool> ShowWindowFrontAndCenterAsync(
            MainWindowOpenTarget target = MainWindowOpenTarget.Default,
            CancellationToken cancellationToken = default) =>
            _windowManager.ShowAsync(target, cancellationToken);

        internal void NotifyUpcomingScheduledRoutines(IReadOnlyList<string> routineNames)
        {
            lock (_notificationLock) { if (_disposed) return; }
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
            _ = _trayService.ShowBalloonAsync(title, $"{names}. Click to open AudioPilot.", canShow: IsNotificationLifetimeActive);
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
                _ = _trayService.ShowBalloonAsync(appName, "The application is still running in the background.", canShow: () => IsNotificationLifetimeActive() && !IsWindowVisible);
            }

            return true;
        }

        public void RefreshIconsForCurrentDpi()
        {
            MainWindow? currentWindow = _windowManager.CurrentWindow;
            currentWindow?.Icon = AppIconImageProvider.GetSharedIconFrameForDpi(VisualTreeHelper.GetDpi(currentWindow).DpiScaleX);
        }

        private bool IsNotificationLifetimeActive()
        {
            lock (_notificationLock) return !_disposed;
        }

        public void Dispose()
        {
            lock (_notificationLock)
            {
                _disposed = true;
                _notifiedFailures.Clear();
                _failedRoutines.Clear();
            }
        }
    }
}

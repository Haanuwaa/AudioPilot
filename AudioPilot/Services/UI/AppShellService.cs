using AudioPilot.Localization;

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
        private readonly HashSet<string> _failedRoutines = [with(StringComparer.OrdinalIgnoreCase)];
        private bool _disposed;

        internal AppShellService(
            IAppMainWindowManager windowManager,
            IAppTrayIconService trayService)
        {
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        }

        public bool IsWindowVisible => _windowManager.IsVisible;

        internal Task<Models.WindowLayoutSnapshot> ResetWindowLayoutAsync(CancellationToken cancellationToken = default)
            => _windowManager.ChangeLayoutAsync(new(Reset: true), cancellationToken);

        /// <summary>Reports actionable background failures without opening a window or repeating each failed retry.</summary>
        internal Task NotifyBackgroundFailureAsync(BackgroundFailureKind kind)
        {
            (string title, string message) = kind switch
            {
                BackgroundFailureKind.AutoSave => (LocalizationService.Default["Tray_ChangesCouldNotBeSaved"],
                    LocalizationService.Default["Tray_AudioPilotCouldNotAutomaticallySaveYourChanges"]),
                BackgroundFailureKind.ResumeRecovery => (LocalizationService.Default["Tray_AudioPilotNeedsAttentionAfterSleep"],
                    LocalizationService.Default["Tray_AudioOrSomeHotkeysCouldNotBe"]),
                BackgroundFailureKind.Hotkeys => (LocalizationService.Default["Tray_SomeShortcutsAreUnavailable"],
                    LocalizationService.Default["Tray_OneOrMoreConfiguredShortcutsCouldNot"]),
                BackgroundFailureKind.AutomaticRoutine => (LocalizationService.Default["Tray_AutomaticRoutinesNeedAttention"],
                    LocalizationService.Default["Tray_OneOrMoreAutomaticRoutinesCouldNot"]),
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

            string title = routineNames.Count == 1 ? LocalizationService.Default["Tray_ScheduledRoutineStartsSoon"] : LocalizationService.Default.FormatKey("Tray_UpcomingCount", routineNames.Count);
            string names = string.Join(", ", routineNames.Take(3).Select(FormatRoutineNotificationName));
            if (routineNames.Count > 3)
            {
                names += LocalizationService.Default.FormatKey("Tray_MoreRoutines", routineNames.Count - 3);
            }
            _ = _trayService.ShowBalloonAsync(title, LocalizationService.Default.FormatKey("Tray_OpenFromReminder", names), canShow: IsNotificationLifetimeActive);
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

        internal Task<bool> NotifyUpdateAvailableAsync(Version version, CancellationToken cancellationToken) =>
            _trayService.ShowBalloonAsync(LocalizationService.Default["Tray_AudioPilotUpdateAvailable"],
                LocalizationService.Default.FormatKey("Tray_UpdateNotification", version),
                canShow: () => IsNotificationLifetimeActive() && !cancellationToken.IsCancellationRequested);

        public bool MinimizeToTray(bool showBalloon = false, string? appName = null)
        {
            if (!_trayService.EnsureVisible() || !_windowManager.Hide())
            {
                return false;
            }

            if (showBalloon && !string.IsNullOrWhiteSpace(appName))
            {
                _ = _trayService.ShowBalloonAsync(appName, LocalizationService.Default["Tray_TheApplicationIsStillRunningInThe"], canShow: () => IsNotificationLifetimeActive() && !IsWindowVisible);
            }

            return true;
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

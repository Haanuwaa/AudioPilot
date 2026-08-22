using System.Diagnostics;
using AudioPilot.Logging;
using Microsoft.Win32;

namespace AudioPilot.Coordinators
{
    internal enum AppRuntimeStartupInitializationOutcome
    {
        Succeeded,
        Cancelled,
        Fatal,
    }

    /// <summary>
    /// Contract for the view-model resume recovery pipeline that rehydrates audio, hotkeys, and related state
    /// after the system resumes.
    /// </summary>
    internal interface IResumeRecoveryHandler
    {
        Task RecoverAfterSystemResumeAsync(string? resumeOpId = null);
    }

    /// <summary>
    /// Aggregates the startup and resume hooks that the application runtime owns so the coordinator can stay
    /// focused on lifecycle orchestration rather than service lookup.
    /// </summary>
    internal readonly record struct AppRuntimeStartupResumeDependencies(
        Action RegisterNotificationClient,
        Func<bool> SettingsFileExists,
        Func<bool, Task> InitializeStartupAsync,
        Action CaptureInitialHotplugSnapshot);

    /// <summary>
    /// Coordinates one-time runtime startup initialization and serialized post-resume recovery scheduling.
    /// </summary>
    /// <remarks>
    /// Startup and resume are kept together because both paths are application-runtime lifecycle seams that must respect
    /// duplicate signals, shutdown races, and shared logging correlation ids.
    /// </remarks>
    internal sealed class AppRuntimeStartupResumeCoordinator : IDisposable
    {
        private readonly Logger _logger;
        private readonly IResumeRecoveryHandler _appVm;
        private readonly Func<string, Task> _showStartupError;
        private readonly Action _shutdown;
        private readonly AppRuntimeStartupResumeDependencies _dependencies;
        private readonly Func<Func<Task>, Task> _queueResumeRecoveryWork;
        private readonly Func<long> _getTickCount;
        private readonly Lock _initializationSync = new();
        private readonly Lock _resumeSync = new();

        private Task<AppRuntimeStartupInitializationOutcome>? _initializationTask;
        private volatile bool _disposed;
        private int _disposeStarted;
        private bool _resumeRecoveryScheduled;
        private bool _suspended;
        private long _resumeWorkerId;
        private long? _lastResumeSignalTick;
        private (string OpId, string OwnerMethodName)? _pendingResume;

        internal AppRuntimeStartupResumeCoordinator(
            Logger logger,
            AudioDeviceService audioService,
            SettingsService settingsService,
            AppStartupCoordinator startupCoordinator,
            IResumeRecoveryHandler appVm,
            AppHotplugOverlayCoordinator hotplugOverlayCoordinator,
            Func<string, Task> showStartupError,
            Action shutdown)
            : this(
                logger,
                appVm,
                new AppRuntimeStartupResumeDependencies(
                    audioService.RegisterNotificationClient,
                    settingsService.SettingsFileExists,
                    startupCoordinator.InitializeAsync,
                    hotplugOverlayCoordinator.CaptureInitialSnapshot),
                static work => Task.Run(work),
                showStartupError,
                shutdown)
        {
        }

        internal AppRuntimeStartupResumeCoordinator(
            Logger logger,
            IResumeRecoveryHandler appVm,
            AppRuntimeStartupResumeDependencies dependencies,
            Func<Func<Task>, Task> queueResumeRecoveryWork,
            Func<string, Task> showStartupError,
            Action shutdown,
            Func<long>? getTickCount = null)
        {
            _logger = logger;
            _appVm = appVm;
            _dependencies = dependencies;
            _queueResumeRecoveryWork = queueResumeRecoveryWork;
            _showStartupError = showStartupError;
            _shutdown = shutdown;
            _getTickCount = getTickCount ?? (() => Environment.TickCount64);
        }

        /// <summary>
        /// Performs one-time startup initialization for the runtime host, including device-notification
        /// registration, startup coordinator execution, and the initial hotplug snapshot capture.
        /// </summary>
        /// <remarks>
        /// Duplicate Loaded events are ignored. Notification registration failures are downgraded to warnings so
        /// fallback polling can continue, while startup initialization failures remain fatal and trigger shutdown.
        /// </remarks>
        public Task<AppRuntimeStartupInitializationOutcome> InitializeAsync(string ownerMethodName)
        {
            lock (_initializationSync)
            {
                return _initializationTask ??= InitializeCoreAsync(ownerMethodName);
            }
        }

        private async Task<AppRuntimeStartupInitializationOutcome> InitializeCoreAsync(string ownerMethodName)
        {
            var startupStopwatch = Stopwatch.StartNew();
            if (_disposed)
            {
                return AppRuntimeStartupInitializationOutcome.Cancelled;
            }

            try
            {
                _dependencies.RegisterNotificationClient();
            }
            catch (Exception ex)
            {
                _logger.Warning("AppRuntimeStartupResumeCoordinator", "Failed to register device notification callback, will use fallback polling", ownerMethodName, ex);
            }

            try
            {
                double notificationClientMs = startupStopwatch.Elapsed.TotalMilliseconds;
                bool noSettingsFileExists = !_dependencies.SettingsFileExists();
                double settingsBootstrapMs = startupStopwatch.Elapsed.TotalMilliseconds - notificationClientMs;

                await _dependencies.InitializeStartupAsync(noSettingsFileExists);

                startupStopwatch.Stop();
                if (_logger.IsEnabled(LogLevel.Info))
                {
                    _logger.Info("AppRuntimeStartupResumeCoordinator", () => $"startup-initialization-timing | notificationClientMs={notificationClientMs:F1} settingsBootstrapMs={settingsBootstrapMs:F1} totalMs={startupStopwatch.Elapsed.TotalMilliseconds:F1}");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("AppRuntimeStartupResumeCoordinator", "Startup initialization was canceled", ownerMethodName);
                return AppRuntimeStartupInitializationOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                _logger.Fatal("AppRuntimeStartupResumeCoordinator", () => $"startup-initialization-failed | error={ex.GetType().Name}", ownerMethodName, ex);
                try
                {
                    await _showStartupError("Failed to initialize application services. The app will now close. Please check AudioPilot.log for details.");
                }
                catch (Exception dialogEx)
                {
                    _logger.Warning(
                        "AppRuntimeStartupResumeCoordinator",
                        () => $"startup-failure-dialog-failed | error={dialogEx.GetType().Name}",
                        ownerMethodName,
                        dialogEx);
                }

                try
                {
                    _shutdown();
                }
                catch (Exception shutdownEx)
                {
                    _logger.Warning(
                        "AppRuntimeStartupResumeCoordinator",
                        () => $"startup-failure-shutdown-request-failed | error={shutdownEx.GetType().Name}",
                        ownerMethodName,
                        shutdownEx);
                }

                return AppRuntimeStartupInitializationOutcome.Fatal;
            }

            try
            {
                _dependencies.CaptureInitialHotplugSnapshot();
            }
            catch (Exception ex)
            {
                _logger.Warning("AppRuntimeStartupResumeCoordinator", () => $"startup-hotplug-snapshot-capture-failed | error={ex.GetType().Name}", ownerMethodName, ex);
            }

            return AppRuntimeStartupInitializationOutcome.Succeeded;
        }

        /// <summary>
        /// Responds to power-resume notifications by queueing at most one recovery pipeline at a time and tagging
        /// the run with a correlated resume operation id.
        /// </summary>
        /// <remarks>
        /// Duplicate resume signals are coalesced using a monotonic cooldown. A new suspend/resume cycle queues
        /// one trailing recovery if an earlier cycle is still running. Queued work waits for resume and respects shutdown.
        /// </remarks>
        public void HandlePowerModeChanged(PowerModeChangedEventArgs e, string ownerMethodName)
        {
            long workerId;
            lock (_resumeSync)
            {
                if (_disposed)
                {
                    return;
                }

                if (e.Mode == PowerModes.Suspend)
                {
                    _suspended = true;
                    _lastResumeSignalTick = null;
                    _pendingResume = null;
                    return;
                }

                if (e.Mode != PowerModes.Resume)
                {
                    return;
                }

                long now = _getTickCount();
                if (!_suspended && (_resumeRecoveryScheduled || (_lastResumeSignalTick is long last && now - last < 1000)))
                {
                    return;
                }

                _suspended = false;
                _lastResumeSignalTick = now;
                _pendingResume = ($"resume:{Guid.NewGuid():N}", ownerMethodName);
                if (_resumeRecoveryScheduled)
                {
                    return;
                }

                _resumeRecoveryScheduled = true;
                workerId = ++_resumeWorkerId;
            }

            _ = QueueResumeRecoveryAsync(workerId, ownerMethodName);
        }

        private async Task QueueResumeRecoveryAsync(long workerId, string ownerMethodName)
        {
            try
            {
                await _queueResumeRecoveryWork(() => RunResumeRecoveryAsync(workerId));
            }
            catch (Exception ex)
            {
                lock (_resumeSync)
                {
                    if (_resumeWorkerId == workerId)
                    {
                        _resumeRecoveryScheduled = false;
                        _pendingResume = null;
                        _lastResumeSignalTick = null;
                    }
                }

                if (!_disposed)
                {
                    _logger.Warning("AppRuntimeStartupResumeCoordinator", "power-resume-queue-failed", ownerMethodName, ex);
                }
            }
        }

        private async Task RunResumeRecoveryAsync(long workerId)
        {
            while (true)
            {
                (string OpId, string OwnerMethodName) request;
                lock (_resumeSync)
                {
                    if (_resumeWorkerId != workerId)
                    {
                        return;
                    }

                    if (_disposed || _suspended || _pendingResume == null)
                    {
                        _resumeRecoveryScheduled = false;
                        _pendingResume = null;
                        return;
                    }

                    request = _pendingResume.Value;
                    _pendingResume = null;
                }

                try
                {
                    _logger.Info("AppRuntimeStartupResumeCoordinator", () => $"power-resume-detected | opId={request.OpId}");
                    await _appVm.RecoverAfterSystemResumeAsync(request.OpId);
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                    {
                        _logger.Warning("AppRuntimeStartupResumeCoordinator", () => $"power-resume-recovery-failed | opId={request.OpId}", request.OwnerMethodName, ex);
                    }
                }
            }
        }

        /// <summary>
        /// Marks the coordinator disposed so future startup-resume work is ignored and queued recovery lambdas
        /// observe shutdown state before running.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _disposed = true;
        }
    }
}

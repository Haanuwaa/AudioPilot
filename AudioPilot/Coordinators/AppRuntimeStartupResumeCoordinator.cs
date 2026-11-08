using System.Diagnostics;
using AudioPilot.Logging;
using AudioPilot.ViewModels;
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
        Task RecoverAfterSystemResumeAsync(string? resumeOpId = null, CancellationToken cancellationToken = default);
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
        private readonly Action? _shutdown;
        private readonly AppRuntimeStartupResumeDependencies _dependencies;
        private readonly Func<Func<Task>, Task> _queueResumeRecoveryWork;
        private readonly RoutineSystemEventCoordinator? _systemEvents;
        private readonly Lock _initializationSync = new();
        private readonly Lock _resumeSync = new();

        private Task<AppRuntimeStartupInitializationOutcome>? _initializationTask;
        private readonly TaskCompletionSource<AppRuntimeStartupInitializationOutcome> _startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _disposed;
        private bool _resumeRecoveryScheduled;
        private bool _suspended;
        private long _resumeWorkerId;
        private long _wakeId;
        private bool _wakeAccepted;
        private WakeRequest? _pendingResume;
        private CancellationTokenSource? _activeWake;
        private Task _activeCancellation = Task.CompletedTask;
        private Task _resumeDrain = Task.CompletedTask;

        private readonly record struct WakeRequest(long Id, string OpId, string OwnerMethodName);

        internal AppRuntimeStartupResumeCoordinator(
            Logger logger,
            AudioDeviceService audioService,
            SettingsService settingsService,
            AppStartupCoordinator startupCoordinator,
            AppViewModel appVm,
            AppHotplugOverlayCoordinator hotplugOverlayCoordinator,
            Func<string, Task> showStartupError,
            Func<Task> publishShellAsync,
            CancellationToken shutdownToken)
            : this(
                logger,
                appVm,
                new AppRuntimeStartupResumeDependencies(
                    audioService.RegisterNotificationClient,
                    settingsService.SettingsFileExists,
                    noSettings => startupCoordinator.InitializeAsync(noSettings, publishShellAsync, shutdownToken),
                    hotplugOverlayCoordinator.CaptureInitialSnapshot),
                static work => Task.Run(work),
                showStartupError,
                shutdown: null,
                systemEvents: appVm.RoutineSystemEvents)
        {
        }

        internal AppRuntimeStartupResumeCoordinator(
            Logger logger,
            IResumeRecoveryHandler appVm,
            AppRuntimeStartupResumeDependencies dependencies,
            Func<Func<Task>, Task> queueResumeRecoveryWork,
            Func<string, Task> showStartupError,
            Action? shutdown,
            RoutineSystemEventCoordinator? systemEvents = null)
        {
            _logger = logger;
            _appVm = appVm;
            _dependencies = dependencies;
            _queueResumeRecoveryWork = queueResumeRecoveryWork;
            _showStartupError = showStartupError;
            _shutdown = shutdown;
            _systemEvents = systemEvents;
        }

        /// <summary>
        /// Performs one-time startup initialization for the runtime host, including device-notification
        /// registration, startup coordinator execution, and the initial hotplug snapshot capture.
        /// </summary>
        /// <remarks>
        /// Duplicate Loaded events are ignored. Notification registration failures are downgraded to warnings so
        /// startup can continue, while startup initialization failures remain fatal and trigger shutdown.
        /// </remarks>
        public Task<AppRuntimeStartupInitializationOutcome> InitializeAsync(string ownerMethodName)
        {
            lock (_initializationSync)
            {
                return _initializationTask ??= InitializeAndSignalAsync(ownerMethodName);
            }
        }

        private async Task<AppRuntimeStartupInitializationOutcome> InitializeAndSignalAsync(string ownerMethodName)
        {
            try
            {
                AppRuntimeStartupInitializationOutcome outcome = await InitializeCoreAsync(ownerMethodName);
                _startupReady.TrySetResult(outcome);
                return outcome;
            }
            catch
            {
                _startupReady.TrySetResult(AppRuntimeStartupInitializationOutcome.Fatal);
                throw;
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
                _logger.Warning("AppRuntimeStartupResumeCoordinator", "Failed to register device notification callback; device changes may require a refresh", ownerMethodName, ex);
            }

            try
            {
                double notificationClientMs = startupStopwatch.Elapsed.TotalMilliseconds;
                bool noSettingsFileExists = !_dependencies.SettingsFileExists();
                double settingsBootstrapMs = startupStopwatch.Elapsed.TotalMilliseconds - notificationClientMs;

                await _dependencies.InitializeStartupAsync(noSettingsFileExists);
                if (_disposed) return AppRuntimeStartupInitializationOutcome.Cancelled;

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
                if (_disposed) return AppRuntimeStartupInitializationOutcome.Cancelled;
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
                    _shutdown?.Invoke();
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

        internal void HandlePowerModeChanged(PowerModes mode, string ownerMethodName,
            Action? onSuspend = null, Action? onResume = null)
        {
            long workerId;
            TaskCompletionSource completion;
            lock (_resumeSync)
            {
                if (_disposed) return;

                if (mode == PowerModes.Suspend)
                {
                    if (_suspended) return;
                    _suspended = true;
                    _wakeAccepted = false;
                    _pendingResume = null;
                    CancelActiveWake();
                    _systemEvents?.Suspend();
                    onSuspend?.Invoke();
                    return;
                }

                if (mode != PowerModes.Resume || _wakeAccepted) return;
                _suspended = false;
                _wakeAccepted = true;
                _pendingResume = new(++_wakeId, $"resume:{Guid.NewGuid():N}", ownerMethodName);
                _systemEvents?.BeginResume(_wakeId);
                onResume?.Invoke();
                if (_resumeRecoveryScheduled) return;

                _resumeRecoveryScheduled = true;
                workerId = ++_resumeWorkerId;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _resumeDrain = completion.Task;
            }

            _ = QueueResumeRecoveryAsync(workerId, ownerMethodName, completion);
        }

        private async Task QueueResumeRecoveryAsync(long workerId, string ownerMethodName, TaskCompletionSource completion)
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
                        _wakeAccepted = false;
                        _systemEvents?.CompleteRecovery(_wakeId);
                    }
                }

                if (!_disposed)
                    _logger.Warning("AppRuntimeStartupResumeCoordinator", "power-resume-queue-failed", ownerMethodName, ex);
            }
            finally
            {
                completion.TrySetResult();
            }
        }

        private async Task RunResumeRecoveryAsync(long workerId)
        {
            var startup = await _startupReady.Task;
            while (true)
            {
                WakeRequest request;
                CancellationTokenSource wake;
                lock (_resumeSync)
                {
                    if (_resumeWorkerId != workerId) return;
                    if (_disposed || startup != AppRuntimeStartupInitializationOutcome.Succeeded || _pendingResume is not WakeRequest next)
                    {
                        _resumeRecoveryScheduled = false;
                        _pendingResume = null;
                        return;
                    }

                    request = next;
                    _pendingResume = null;
                    _activeWake = wake = new();
                    _activeCancellation = Task.CompletedTask;
                }

                try
                {
                    _logger.Info("AppRuntimeStartupResumeCoordinator", () => $"power-resume-detected | opId={request.OpId}");
                    await _appVm.RecoverAfterSystemResumeAsync(request.OpId, wake.Token);
                }
                catch (OperationCanceledException) when (wake.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                        _logger.Warning("AppRuntimeStartupResumeCoordinator", () => $"power-resume-recovery-failed | opId={request.OpId}", request.OwnerMethodName, ex);
                }
                finally
                {
                    Task cancellation;
                    lock (_resumeSync)
                    {
                        if (!_disposed && !wake.IsCancellationRequested)
                            _systemEvents?.CompleteRecovery(request.Id);
                        cancellation = _activeCancellation;
                        _activeWake = null;
                    }

                    try { await cancellation; }
                    catch (Exception ex)
                    {
                        _logger.Warning("AppRuntimeStartupResumeCoordinator", "power-resume-cancellation-failed", request.OwnerMethodName, ex);
                    }
                    finally { wake.Dispose(); }
                }
            }
        }

        internal Task DrainResumeAsync()
        {
            lock (_resumeSync) return _resumeDrain;
        }

        private void CancelActiveWake()
        {
            if (_activeWake is { IsCancellationRequested: false } wake)
                _activeCancellation = wake.CancelAsync();
        }

        public void Dispose()
        {
            lock (_resumeSync)
            {
                if (_disposed) return;
                _disposed = true;
                _pendingResume = null;
                CancelActiveWake();
                _startupReady.TrySetResult(AppRuntimeStartupInitializationOutcome.Cancelled);
            }
        }
    }
}

using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    internal interface IProcessAudioRouter
    {
        ProcessAudioRoutingResult TrySetProcessDevice(uint processId, DataFlow flow, string targetDeviceId, IReadOnlyList<NRole> roles);
        ProcessAudioRoutingResult TryClearProcessDevice(uint processId, DataFlow flow, IReadOnlyList<NRole> roles);
    }

    internal readonly record struct PerAppAudioRoutingResetResult(bool Success, bool HadAssignments);

    internal enum InputListenApplyStatus
    {
        NotApplied,
        AppliedUnverified,
        AppliedVerified,
    }

    internal readonly record struct InputListenStateChangeResult(
        InputListenApplyStatus Status,
        bool Enabled,
        string? Error)
    {
        public bool Applied => Status != InputListenApplyStatus.NotApplied;
        public bool Verified => Status == InputListenApplyStatus.AppliedVerified;
    }

    internal interface IPerAppAudioRoutingResetter
    {
        PerAppAudioRoutingResetResult TryResetAll();
    }

    internal readonly record struct ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult Result, string? DeviceName)
    {
        public bool Success => Result != ProcessAudioRoutingResult.Failed;
    }

    internal sealed class AudioPolicyProcessAudioRouter : IProcessAudioRouter
    {
        public ProcessAudioRoutingResult TrySetProcessDevice(uint processId, DataFlow flow, string targetDeviceId, IReadOnlyList<NRole> roles)
        {
            return AudioPolicyConfig.TrySetProcessDefaultDevice(processId, flow, roles, targetDeviceId);
        }

        public ProcessAudioRoutingResult TryClearProcessDevice(uint processId, DataFlow flow, IReadOnlyList<NRole> roles)
        {
            return AudioPolicyConfig.TryClearProcessDefaultDevice(processId, flow, roles);
        }
    }

    public partial class AudioDeviceService : IAudioDeviceEnumerator, IDisposable, IAsyncDisposable
    {
        internal static Action<bool>? SetMicrophoneMuteOverrideForTests { get; set; }
        internal static Action<bool>? SetPlaybackMuteOverrideForTests { get; set; }
        private static bool? _microphoneMuteStateForTests;
        private static bool? _playbackMuteStateForTests;

        public event Action? DeviceStateChanged;
        public event Action<AudioMixerMode>? AudioSessionCreated;
        internal event Action<AudioSessionLifecycleSignal>? AudioSessionLifecycleChanged;
        internal event Action<DataFlow, NRole>? DefaultAudioDeviceChanged;

        private readonly MMDeviceEnumerator _enumerator;
        private readonly Logger _logger;
        private readonly Func<DeviceCacheHelper?> _deviceCacheAccessor;
        private readonly ReaderWriterLockSlim _enumeratorLock = new();
        private readonly SwitchExecutionCoordinator _switchExecutionCoordinator = new();
        private long _outputSwitchRevision;
        private long _inputSwitchRevision;
        private const int DeferredProcessOutputLogEvery = 10;
        private const int DeferredProcessOutputLogCounterCapacity = 128;
        private static readonly NRole[] DefaultOutputRoles =
        [
            NRole.Multimedia,
            NRole.Communications,
            NRole.Console
        ];
        private static readonly NRole[] DefaultInputRoles =
        [
            NRole.Console,
            NRole.Communications,
            NRole.Multimedia
        ];
        private readonly AudioRoleConfiguration _roleConfiguration = new(DefaultOutputRoles, DefaultInputRoles);
        private readonly AudioDeviceRoleConfigurationHelper _roleConfigurationHelper;
        private readonly LogCooldownGate _debugLogCooldown = new(AppConstants.Timing.AudioDeviceQueryLogCooldownMs);
        private readonly BoundedLogOccurrenceCounter _deferredProcessOutputLogCounts = new(DeferredProcessOutputLogCounterCapacity);

        private readonly AudioSessionService _sessionService;
        private readonly VolumeControlService _volumeService;
        private readonly AudioDeviceSessionProcessResolver _sessionProcessResolver;
        private readonly AudioDeviceSessionVolumeRestoreHelper _sessionVolumeRestoreHelper;
        private readonly AudioDeviceListenStateHelper _listenStateHelper;
        private readonly AudioDeviceEndpointQueryHelper _endpointQueryHelper;
        private readonly AudioDeviceSessionMonitoringCoordinatorFacade _sessionMonitoringFacade;
        private readonly AudioDeviceProcessRoutingHelper _processRoutingHelper;
        private readonly IInputListenPropertyWriter _inputListenPropertyWriter;
        private readonly IInputListenPropertyReader _inputListenPropertyReader;
        private readonly IInputListenAudioDeviceResolver _inputListenDeviceResolver;
        private readonly IProcessAudioRouter _processAudioRouter;
        private readonly IPerAppAudioRoutingResetter _perAppAudioRoutingResetter;

        private volatile bool _disposed;

        internal ConcurrentDictionary<int, Task> BackgroundTasksForTests => _backgroundTasks;
        private int _disposeStarted;

        private readonly SessionMonitorCoordinator _playbackSessionMonitorCoordinator;
        private readonly SessionMonitorCoordinator _recordingSessionMonitorCoordinator;
        private readonly AudioDeviceBackgroundWorkHelper _backgroundWorkHelper;
        private readonly AudioDeviceNotificationRegistrationHelper _notificationRegistrationHelper;
        private readonly AudioDeviceResumeRecoveryHelper _resumeRecoveryHelper;
        private readonly AudioDeviceResumeRecoveryCoordinator _resumeRecoveryCoordinator;
        private readonly Action _outputSwitchCompletionSessionMonitoringUpdate;
        private readonly CancellationTokenSource _backgroundWorkCts = new();
        private readonly ConcurrentDictionary<int, Task> _backgroundTasks = new();
        private int _backgroundTaskId;
        private readonly DeviceStateMetricsTracker _deviceStateMetricsTracker = new();
        private readonly TaskCompletionSource<bool> _disposeStartedCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _disposeCleanupBarrierCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<Task>? _sessionMonitoringDrainOverride;

        public AudioDeviceService()
            : this(new InputListenPropertyWriter(Logger.Instance))
        {
        }

        internal AudioDeviceService(
            IInputListenPropertyWriter inputListenPropertyWriter,
            IInputListenPropertyReader? inputListenPropertyReader = null,
            IInputListenAudioDeviceResolver? inputListenDeviceResolver = null,
            IProcessAudioRouter? processAudioRouter = null,
            IPerAppAudioRoutingResetter? perAppAudioRoutingResetter = null,
            Func<IAudioDeviceEnumerator, AudioSessionService>? audioSessionServiceFactory = null,
            Action? outputSwitchCompletionSessionMonitoringUpdate = null,
            Func<Task>? sessionMonitoringDrainOverride = null,
            Logger? logger = null,
            Func<DeviceCacheHelper?>? deviceCacheAccessor = null)
        {
            _enumerator = new MMDeviceEnumerator();
            _logger = logger ?? Logger.Instance;
            _deviceCacheAccessor = deviceCacheAccessor ?? AudioSessionService.ResolveDeviceCacheOrNull;
            _endpointQueryHelper = new AudioDeviceEndpointQueryHelper(
                _enumerator,
                _enumeratorLock,
                _logger,
                () => _disposed,
                GetConfiguredOutputRolesSnapshot,
                GetConfiguredInputRolesSnapshot,
                LogDebugWithCooldown);
            _inputListenPropertyWriter = inputListenPropertyWriter;
            _inputListenPropertyReader = inputListenPropertyReader ?? new InputListenPropertyReader(_logger);
            _inputListenDeviceResolver = inputListenDeviceResolver ?? new InputListenAudioDeviceResolver(
                GetDefaultRecordingDevice,
                GetDefaultPlaybackDevice,
                TryGetPlaybackDeviceById,
                GetActivePlaybackCycleEntries);
            _listenStateHelper = new AudioDeviceListenStateHelper(
                _logger,
                _inputListenPropertyWriter,
                _inputListenPropertyReader,
                _inputListenDeviceResolver);
            _processAudioRouter = processAudioRouter ?? new AudioPolicyProcessAudioRouter();
            _processRoutingHelper = new AudioDeviceProcessRoutingHelper(_logger, _processAudioRouter, DeferredProcessOutputLogEvery);
            _perAppAudioRoutingResetter = perAppAudioRoutingResetter ?? new RegistryPerAppAudioRoutingResetter(_logger);
            _sessionMonitoringDrainOverride = sessionMonitoringDrainOverride;

            Func<IAudioDeviceEnumerator, AudioSessionService> resolvedAudioSessionServiceFactory =
                audioSessionServiceFactory ?? (enumerator => new AudioSessionService(
                    enumerator,
                    _deviceCacheAccessor,
                    _logger,
                    () => ResolveDetectionRole(GetConfiguredOutputRolesSnapshot(), NRole.Multimedia),
                    () => ResolveDetectionRole(GetConfiguredInputRolesSnapshot(), NRole.Console)));

            _sessionService = resolvedAudioSessionServiceFactory(this);
            _volumeService = new VolumeControlService(
                this,
                _sessionService.GetCachedProcessInfo,
                _sessionService.IsCacheEntryExpired);
            _sessionProcessResolver = new AudioDeviceSessionProcessResolver(
                _logger,
                _sessionService.GetCachedProcessInfo,
                _sessionService.IsCacheEntryExpired);
            _sessionVolumeRestoreHelper = new AudioDeviceSessionVolumeRestoreHelper(_sessionProcessResolver);
            _playbackSessionMonitorCoordinator = new SessionMonitorCoordinator(
                _logger,
                AudioMixerMode.Output,
                GetActivePlaybackMonitorEndpoints,
                OnSessionCreated,
                OnEndpointVolumeChanged,
                NotifyAudioSessionLifecycleChanged,
                RunBackgroundWork,
                () => _disposed);
            _recordingSessionMonitorCoordinator = new SessionMonitorCoordinator(
                _logger,
                AudioMixerMode.Input,
                GetActiveCaptureMonitorEndpoints,
                OnSessionCreated,
                OnEndpointVolumeChanged,
                NotifyAudioSessionLifecycleChanged,
                RunBackgroundWork,
                () => _disposed);
            _backgroundWorkHelper = new AudioDeviceBackgroundWorkHelper(_logger, () => _disposed);
            _sessionMonitoringFacade = new AudioDeviceSessionMonitoringCoordinatorFacade(
                _logger,
                _sessionService,
                _playbackSessionMonitorCoordinator,
                _recordingSessionMonitorCoordinator,
                () => _disposed,
                NotifyAudioSessionLifecycleChanged);
            _notificationRegistrationHelper = new AudioDeviceNotificationRegistrationHelper(
                _logger,
                CreateDeviceNotificationSubscription,
                () => _sessionMonitoringFacade.Update(),
                () => _sessionMonitoringFacade.Stop());
            _resumeRecoveryHelper = new AudioDeviceResumeRecoveryHelper(
                _logger,
                () => _disposed,
                () => _notificationRegistrationHelper.IsRegistered,
                RegisterNotificationClient,
                () => _sessionMonitoringFacade.Update());
            _resumeRecoveryCoordinator = new AudioDeviceResumeRecoveryCoordinator(
                _logger,
                () => _disposed);
            _outputSwitchCompletionSessionMonitoringUpdate = outputSwitchCompletionSessionMonitoringUpdate ?? _sessionMonitoringFacade.Update;
            _roleConfigurationHelper = new AudioDeviceRoleConfigurationHelper(_roleConfiguration, _logger);

            _logger.Info("AudioDeviceService", "Service initialized with delegated services");
        }

        internal static bool IsOutputSwitchDebounced(DateTime now, DateTime lastOutputSwitchTime)
        {
            return SwitchExecutionCoordinator.IsOutputSwitchDebounced(now, lastOutputSwitchTime);
        }

        internal static bool IsInputSwitchDebounced(DateTime now, DateTime lastInputSwitchTime)
        {
            return SwitchExecutionCoordinator.IsInputSwitchDebounced(now, lastInputSwitchTime);
        }

        /// <summary>
        /// Determines whether a captured session-volume snapshot should be persisted for post-switch restoration.
        /// </summary>
        /// <remarks>
        /// Snapshot registration is intentionally strict to avoid stale restore attempts when preservation is disabled
        /// or when no usable snapshot was captured.
        /// </remarks>
        internal static bool ShouldRegisterPreserveSnapshot(bool preserveAudioLevels, SessionVolumeSnapshot? snapshot)
        {
            return preserveAudioLevels && snapshot != null;
        }

        internal bool TryEnterOutputSwitchGateForTests() => _switchExecutionCoordinator.TryEnterOutputForTests();
        internal void ExitOutputSwitchGateForTests() => _switchExecutionCoordinator.ExitOutputForTests();
        internal bool TryEnterInputSwitchGateForTests() => _switchExecutionCoordinator.TryEnterInputForTests();
        internal void ExitInputSwitchGateForTests() => _switchExecutionCoordinator.ExitInputForTests();
        internal DateTime LastOutputSwitchTimeForTests => _switchExecutionCoordinator.LastOutputSwitchTime;
        internal DateTime LastInputSwitchTimeForTests => _switchExecutionCoordinator.LastInputSwitchTime;
        internal bool IsResumeRecoveryWaitingOnSemaphoreForTests => _resumeRecoveryCoordinator.IsWaitingOnSemaphoreForTests;
        internal int ActiveResumeRecoveryCountForTests => _resumeRecoveryCoordinator.ActiveRecoveryCountForTests;
        internal SemaphoreSlim ResumeRecoverySemaphoreForTests => _resumeRecoveryCoordinator.SemaphoreForTests;
        internal Task WaitForDisposeStartedForTestsAsync() => _disposeStartedCompletionSource.Task;
        internal Task WaitForDisposeCleanupBarrierForTestsAsync() => _disposeCleanupBarrierCompletionSource.Task;
        internal Task WaitForResumeRecoveryDrainedForTestsAsync() => _resumeRecoveryCoordinator.WaitForActiveResumeRecoveryAsync();
        internal void SetResumeRecoveryStateForTests(TaskCompletionSource<bool> completionSource, int activeCount) => _resumeRecoveryCoordinator.SetStateForTests(completionSource, activeCount);
        internal void CompleteOutputSwitchAttemptForTests(bool outputSwitchSucceeded) => CompleteOutputSwitchAttempt(outputSwitchSucceeded);
        internal void SetLastSwitchTimesForTests(DateTime? outputLast, DateTime? inputLast)
        {
            _switchExecutionCoordinator.SetLastSwitchTimes(outputLast, inputLast);
        }

        internal void RaiseAudioSessionCreatedForTests(AudioMixerMode mixerMode = AudioMixerMode.Output)
        {
            NotifyAudioSessionCreated(mixerMode);
        }

        internal void RaiseAudioSessionLifecycleChangedForTests(AudioSessionLifecycleSignal signal)
        {
            NotifyAudioSessionLifecycleChanged(signal);
        }

        internal void RaiseDeviceStateChangedForTests()
        {
            OnDeviceStateChange();
        }

        internal void RaiseDefaultPlaybackDeviceChangedForTests()
        {
            OnDeviceSwitchNotification(DataFlow.Render, NRole.Multimedia);
        }

        internal int GetSessionMonitoringConsumerCountForTests(AudioMixerMode mixerMode)
        {
            return _sessionMonitoringFacade.GetConsumerCountForTests(mixerMode);
        }

        internal int GetSessionMonitoringEndpointCountForTests(AudioMixerMode mixerMode)
        {
            return _sessionMonitoringFacade.GetEndpointMonitorCountForTests(mixerMode);
        }

        internal void InvalidateRecentMixerSnapshotState()
        {
            _sessionService.InvalidateRecentMixerSnapshotState();
        }

        public void UpdateRoleConfiguration(IEnumerable<string>? outputRoles, IEnumerable<string>? inputRoles)
        {
            _ = _roleConfigurationHelper.UpdateConfiguration(
                outputRoles,
                inputRoles,
                DefaultOutputRoles,
                DefaultInputRoles);
            _sessionService.InvalidateRecentMixerSnapshotState();
            _deviceCacheAccessor()?.InvalidateCache();
        }

        internal static NRole[] NormalizeConfiguredRoles(IEnumerable<string>? configuredRoles, IReadOnlyList<NRole> fallback)
        {
            return AudioRoleConfiguration.NormalizeConfiguredRoles(configuredRoles, fallback);
        }

        private NRole[] GetConfiguredOutputRolesSnapshot()
        {
            return _roleConfigurationHelper.GetOutputRolesSnapshot();
        }

        private NRole[] GetConfiguredInputRolesSnapshot()
        {
            return _roleConfigurationHelper.GetInputRolesSnapshot();
        }

        internal static NRole ResolveDetectionRole(IReadOnlyList<NRole> configuredRoles, NRole fallback)
        {
            return AudioRoleConfiguration.ResolveDetectionRole(configuredRoles, fallback);
        }

        private static void ApplyConfiguredRoles(string targetDeviceId, IReadOnlyList<NRole> roles)
        {
            AudioRoleConfiguration.ApplyConfiguredRoles(targetDeviceId, roles);
        }

        private static void ApplyConfiguredRole(string targetDeviceId, NRole role)
        {
            AudioRoleConfiguration.ApplyConfiguredRole(targetDeviceId, role);
        }

        private void LogDebugWithCooldown(string key, Func<string> messageFactory)
        {
            if (!_logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            if (!_debugLogCooldown.TryEnter(key))
            {
                return;
            }

            _logger.Debug("AudioDeviceService", messageFactory);
        }

        internal static bool ShouldLogEveryNthOccurrence(int occurrence, int every)
        {
            return occurrence <= 1 || every <= 1 || occurrence % every == 0;
        }

        private bool ShouldLogDeferredProcessAudio(string scope, string op, out int occurrence)
        {
            string key = $"{scope}-deferred:{op}";
            occurrence = _deferredProcessOutputLogCounts.Increment(key);
            return ShouldLogEveryNthOccurrence(occurrence, DeferredProcessOutputLogEvery);
        }

        private void ResetDeferredProcessAudioLogCount(string scope, string op)
        {
            string key = $"{scope}-deferred:{op}";
            _deferredProcessOutputLogCounts.Remove(key);
        }

        private bool TryRunBackgroundWork(Func<CancellationToken, Task> operation, string operationName)
        {
            return _backgroundWorkHelper.TryQueue(
                _backgroundTasks,
                ref _backgroundTaskId,
                _backgroundWorkCts,
                operation,
                operationName);
        }

        private void RunBackgroundWork(Func<CancellationToken, Task> operation, string operationName)
        {
            _ = TryRunBackgroundWork(operation, operationName);
        }

        private void ObserveDetachedTask(Task task, string operationName)
        {
            _ = task.ContinueWith(
                completedTask =>
                {
                    Exception? exception = completedTask.Exception;
                    if (exception != null && _logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.Warning(
                            "AudioDeviceService",
                            () => $"detached-task-faulted | operation={operationName} error={exception.GetBaseException().GetType().Name}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public async Task RecoverAfterSystemResumeAsync()
        {
            await _resumeRecoveryCoordinator.RecoverAfterSystemResumeAsync(
                _deviceCacheAccessor,
                _sessionService.InvalidateRecentMixerSnapshotState,
                _switchExecutionCoordinator.ResetSwitchTimes,
                QueueBestEffortResumeRecoveryWork,
                _backgroundWorkCts.Token);
        }

        private bool QueueBestEffortResumeRecoveryWork()
        {
            return _resumeRecoveryHelper.TryQueueBestEffortRecovery(
                _backgroundTasks,
                ref _backgroundTaskId,
                _backgroundWorkCts);
        }

        public MuteOperationResult ApplyMuteSettings(bool muteMic, bool muteSound, bool deafen)
        {
            return _volumeService.ApplyMuteSettings(muteMic, muteSound, deafen);
        }

        public MuteOperationResult SetMicrophoneMute(bool mute)
        {
            if (SetMicrophoneMuteOverrideForTests is Action<bool> overrideAction)
            {
                overrideAction(mute);
                _microphoneMuteStateForTests = mute;
                return new MuteOperationResult(1, 1, 0);
            }

            return _volumeService.SetMicrophoneMute(mute);
        }

        public MuteOperationResult SetPlaybackMute(bool mute)
        {
            if (SetPlaybackMuteOverrideForTests is Action<bool> overrideAction)
            {
                overrideAction(mute);
                _playbackMuteStateForTests = mute;
                return new MuteOperationResult(1, 1, 0);
            }

            return _volumeService.SetPlaybackMute(mute);
        }

        internal static void ResetTestHooks()
        {
            SetMicrophoneMuteOverrideForTests = null;
            SetPlaybackMuteOverrideForTests = null;
            _microphoneMuteStateForTests = null;
            _playbackMuteStateForTests = null;
        }

        internal static bool TryGetMuteStateOverrideForTests(out bool playbackMuted, out bool microphoneMuted)
        {
            if (SetMicrophoneMuteOverrideForTests == null && SetPlaybackMuteOverrideForTests == null)
            {
                playbackMuted = false;
                microphoneMuted = false;
                return false;
            }

            playbackMuted = _playbackMuteStateForTests ?? false;
            microphoneMuted = _microphoneMuteStateForTests ?? false;
            return true;
        }

        /// <summary>
        /// Reads the Windows "Listen to this device" state for the current default recording endpoint.
        /// </summary>
        /// <param name="enabled">Resolved listen state when read succeeds.</param>
        /// <param name="error">Stable error code when the read fails.</param>
        /// <returns><c>true</c> when the listen state was read; otherwise <c>false</c>.</returns>
        public bool TryGetCurrentInputListenState(out bool enabled, out string? error)
        {
            return _listenStateHelper.TryGetCurrentInputListenState(out enabled, out error);
        }

        /// <summary>
        /// Resolves the playback endpoint currently configured as monitor target for the default input listen setting.
        /// </summary>
        /// <param name="targetOutputDeviceName">Friendly output-device name when available; otherwise <c>null</c>.</param>
        /// <param name="error">Stable error code when read/resolve fails.</param>
        /// <returns><c>true</c> when read succeeds (even if no target is configured); otherwise <c>false</c>.</returns>
        public bool TryGetCurrentInputListenTargetOutputDeviceName(out string? targetOutputDeviceName, out string? error)
        {
            return _listenStateHelper.TryGetCurrentInputListenTargetOutputDeviceName(out targetOutputDeviceName, out error);
        }

        /// <summary>
        /// Writes the Windows "Listen to this device" state for the current default recording endpoint.
        /// </summary>
        /// <param name="enabled">Target listen state.</param>
        /// <param name="changed">
        /// <c>true</c> when the write was applied. Inspect <paramref name="error"/> for
        /// <see cref="AppConstants.Audio.ErrorCodes.Listen.StateVerifyUnknown"/> when read-back verification was unavailable.
        /// </param>
        /// <param name="error">Stable failure or verification-status code.</param>
        /// <returns><c>true</c> when the write was applied, including an unverified application; otherwise <c>false</c>.</returns>
        public bool TrySetCurrentInputListenState(bool enabled, out bool changed, out string? error)
        {
            return TrySetCurrentInputListenState(enabled, string.Empty, out changed, out error);
        }

        public bool TrySetCurrentInputListenState(bool enabled, string? preferredRenderDeviceId, out bool changed, out string? error)
        {
            return _listenStateHelper.TrySetCurrentInputListenState(enabled, preferredRenderDeviceId, out changed, out error);
        }

        public bool TrySetCurrentInputListenState(bool enabled, string? preferredRenderDeviceId, string? preferredRenderDeviceName, out bool changed, out string? error)
        {
            return _listenStateHelper.TrySetCurrentInputListenState(enabled, preferredRenderDeviceId, preferredRenderDeviceName, out changed, out error);
        }

        internal InputListenStateChangeResult SetCurrentInputListenState(string? preferredRenderDeviceId, string? preferredRenderDeviceName, bool enabled)
        {
            return _listenStateHelper.SetCurrentInputListenState(enabled, preferredRenderDeviceId, preferredRenderDeviceName);
        }

        /// <summary>
        /// Toggles the Windows "Listen to this device" state for the current default recording endpoint.
        /// </summary>
        /// <param name="enabled">Resulting listen state when toggle succeeds.</param>
        /// <param name="error">Stable failure or verification-status code.</param>
        /// <returns><c>true</c> when the toggle was applied, including an unverified application; otherwise <c>false</c>.</returns>
        public bool TryToggleCurrentInputListenState(out bool enabled, out string? error)
        {
            return TryToggleCurrentInputListenState(string.Empty, out enabled, out error);
        }

        public bool TryToggleCurrentInputListenState(string? preferredRenderDeviceId, out bool enabled, out string? error)
        {
            return _listenStateHelper.TryToggleCurrentInputListenState(preferredRenderDeviceId, out enabled, out error);
        }

        public bool TryToggleCurrentInputListenState(string? preferredRenderDeviceId, string? preferredRenderDeviceName, out bool enabled, out string? error)
        {
            return _listenStateHelper.TryToggleCurrentInputListenState(preferredRenderDeviceId, preferredRenderDeviceName, out enabled, out error);
        }

        internal InputListenStateChangeResult ToggleCurrentInputListenState(string? preferredRenderDeviceId, string? preferredRenderDeviceName)
        {
            return _listenStateHelper.ToggleCurrentInputListenState(preferredRenderDeviceId, preferredRenderDeviceName);
        }

        public MMDevice? TryGetPlaybackDeviceById(string deviceId)
        {
            return _endpointQueryHelper.TryGetDeviceById(deviceId);
        }

        public MMDevice? TryGetCaptureDeviceById(string deviceId)
        {
            return _endpointQueryHelper.TryGetDeviceById(deviceId);
        }

        internal static Dictionary<string, MMDevice> BuildDeviceLookup(IEnumerable<MMDevice> devices)
        {
            var lookup = new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase);

            foreach (MMDevice device in devices)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.ID))
                {
                    continue;
                }

                lookup[device.ID] = device;
            }

            return lookup;
        }

        /// <summary>
        /// Switches the default output endpoint between two configured devices with optional mute/deafen and
        /// session-volume preservation behavior.
        /// </summary>
        /// <remarks>
        /// The method uses debounce and semaphore gating to prevent switch storms, verifies role application, and
        /// performs post-switch operations (mute/deafen + optional volume restore) asynchronously.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _disposeStartedCompletionSource.TrySetResult(true);

            _disposed = true;

            if (_logger.IsEnabled(LogLevel.Info))
                _logger.Info("AudioDeviceService", "Disposing audio device service");

            bool backgroundTasksCompleted = true;
            Task[] pendingTasks = [];
            try
            {
                pendingTasks = BackgroundTaskHelper.CancelAndSnapshotPendingTasks(_backgroundWorkCts, _backgroundTasks);
                _resumeRecoveryCoordinator.SignalShutdown();

                UnregisterNotificationClient();

                Task sessionMonitoringDrainTask = StopSessionMonitoringAndDrainAsync();

                if (!sessionMonitoringDrainTask.IsCompleted)
                {
                    pendingTasks = [.. pendingTasks, sessionMonitoringDrainTask];
                }

                Task resumeRecoveryDrainTask = _resumeRecoveryCoordinator.CreateBoundedDrainTask();
                if (!resumeRecoveryDrainTask.IsCompleted)
                {
                    pendingTasks = [.. pendingTasks, resumeRecoveryDrainTask];
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        "AudioDeviceService",
                        () => $"shutdown-dispose-summary | notificationClientUnregistered=true sessionMonitoringDrainPending={!sessionMonitoringDrainTask.IsCompleted} resumeRecoveryDrainPending={!resumeRecoveryDrainTask.IsCompleted} pendingTaskCount={pendingTasks.Length}");
                }

                backgroundTasksCompleted = await AudioDeviceServiceLifecycle.DrainBackgroundTasksAsync(pendingTasks, _logger);
            }
            catch (Exception ex)
            {
                backgroundTasksCompleted = false;
                _logger.Warning("AudioDeviceService", "Audio background-work shutdown failed", nameof(DisposeAsync), ex);
            }
            finally
            {
                try
                {
                    BackgroundTaskHelper.DisposeResources(_backgroundWorkCts, _backgroundTasks);
                }
                catch
                {
                }
            }

            if (!backgroundTasksCompleted && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.Lifecycle.DisposeForced} | reason=cleanup-timeout inFlightBackgroundTasksPossible=true");
            }

            await Task.Run(DisposeFinalResources).ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private void DisposeFinalResources()
        {
            AudioDeviceServiceLifecycle.DisposeOwnedResource(_logger, () => _sessionService?.Dispose(), "session-service");
            AudioDeviceServiceLifecycle.DisposeOwnedResource(_logger, () => _volumeService?.Dispose(), "volume-service");
            _disposeCleanupBarrierCompletionSource.TrySetResult(true);

            AudioDeviceServiceLifecycle.DisposeOwnedResource(_logger, _enumerator.Dispose, "device-enumerator");
            AudioDeviceServiceLifecycle.DisposeOwnedResource(_logger, _enumeratorLock.Dispose, "enumerator-lock");
            AudioDeviceServiceLifecycle.DisposeOwnedResource(_logger, _switchExecutionCoordinator.Dispose, "switch-execution-coordinator");
            AudioDeviceServiceLifecycle.DisposeOwnedResource(_logger, _resumeRecoveryCoordinator.Dispose, "resume-recovery-coordinator");
            _debugLogCooldown.Clear();
            _deferredProcessOutputLogCounts.Clear();
            AudioDeviceServiceLifecycle.DisposeOwnedResource(_logger, AudioDeviceHelper.ClearCaches, "static-audio-caches");
        }

        public void Dispose()
        {
            Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}

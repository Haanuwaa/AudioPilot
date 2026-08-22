using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    public partial class AudioDeviceService
    {
        private void NotifyAudioSessionCreated(AudioMixerMode mixerMode)
        {
            AudioSessionCreated?.Invoke(mixerMode);
        }

        private void NotifyAudioSessionLifecycleChanged(AudioSessionLifecycleSignal signal)
        {
            AudioSessionLifecycleChanged?.Invoke(signal);
        }

        private void OnEndpointVolumeChanged(AudioMixerMode mixerMode, string endpointId, float volumePercent, bool isMuted)
        {
            _sessionMonitoringFacade.OnEndpointVolumeChanged(mixerMode, endpointId, volumePercent, isMuted);
        }

        private MMDeviceNotificationClient CreateDeviceNotificationSubscription()
        {
            MMDeviceNotificationClient client = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            client.DefaultDeviceChanged += (_, args) =>
            {
                if (_disposed)
                {
                    return;
                }

                _logger.Trace("DeviceNotificationClient", () => $"Default device changed: Flow={args.Flow}, Role={args.Role}");
                OnDeviceSwitchNotification(args.Flow, args.Role);
            };
            client.DeviceAdded += (_, _) =>
            {
                if (!_disposed)
                {
                    _logger.Trace("DeviceNotificationClient", "Device added");
                    OnDeviceStateChange();
                }
            };
            client.DeviceRemoved += (_, _) =>
            {
                if (!_disposed)
                {
                    _logger.Trace("DeviceNotificationClient", "Device removed");
                    OnDeviceStateChange();
                }
            };
            client.DeviceStateChanged += (_, args) =>
            {
                if (_disposed || (args.NewState != DeviceState.Active
                    && args.NewState != DeviceState.NotPresent
                    && args.NewState != DeviceState.Unplugged))
                {
                    return;
                }

                _logger.Trace("DeviceNotificationClient", () => $"Device state changed: State={args.NewState}");
                OnDeviceStateChange();
            };
            return client;
        }

        private IReadOnlyList<ISessionMonitorEndpoint> GetActivePlaybackMonitorEndpoints()
        {
            return SessionMonitorEndpointFactory.Materialize(GetActivePlaybackDevices());
        }

        private IReadOnlyList<ISessionMonitorEndpoint> GetActiveCaptureMonitorEndpoints()
        {
            return SessionMonitorEndpointFactory.Materialize(GetActiveCaptureDevices());
        }

        internal void AcquireSessionMonitoring(AudioMixerMode mixerMode)
        {
            _sessionMonitoringFacade.Acquire(mixerMode);
        }

        internal void ReleaseSessionMonitoring(AudioMixerMode mixerMode)
        {
            _sessionMonitoringFacade.Release(mixerMode);
        }

        internal Task PauseIdleSessionCacheCleanupAsync()
        {
            return _sessionService.PauseCleanupTaskAsync();
        }

        internal Task ResumeIdleSessionCacheCleanupAsync()
        {
            return _sessionService.ResumeCleanupTaskAsync();
        }

        private void OnDeviceSwitchNotification(DataFlow flow, Role role)
        {
            _deviceStateMetricsTracker.TrackAndLog(_logger);
            DeviceCacheHelper? deviceCache = _deviceCacheAccessor();
            deviceCache?.InvalidateCache();

            Role relevantRole;
            AudioMixerMode mixerMode;
            if (flow == DataFlow.Render)
            {
                relevantRole = ResolveDetectionRole(GetConfiguredOutputRolesSnapshot(), Role.Multimedia);
                mixerMode = AudioMixerMode.Output;
            }
            else if (flow == DataFlow.Capture)
            {
                relevantRole = ResolveDetectionRole(GetConfiguredInputRolesSnapshot(), Role.Console);
                mixerMode = AudioMixerMode.Input;
            }
            else
            {
                return;
            }

            if (role != relevantRole)
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("AudioDeviceService", () => $"Detected relevant default audio device change via notification | flow={flow} role={role} mode={mixerMode}");
            }

            _sessionService.InvalidateRecentMixerSnapshotState();
            UpdateSessionMonitoring();
            NotifyDefaultAudioDeviceChanged(flow, role);
        }

        private void NotifyDefaultAudioDeviceChanged(DataFlow flow, Role role)
        {
            Delegate[] subscribers = DefaultAudioDeviceChanged?.GetInvocationList() ?? [];
            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    ((Action<DataFlow, Role>)subscriber)(flow, role);
                }
                catch (Exception ex)
                {
                    _logger.Warning("AudioDeviceService", "Default-device change subscriber failed", nameof(NotifyDefaultAudioDeviceChanged), ex);
                }
            }
        }

        private void OnDeviceStateChange()
        {
            _deviceStateMetricsTracker.TrackAndLog(_logger);
            DeviceCacheHelper? deviceCache = _deviceCacheAccessor();
            deviceCache?.InvalidateCache();
            _sessionService.InvalidateRecentMixerSnapshotState();
            UpdateSessionMonitoring();
            DeviceStateChanged?.Invoke();
        }

        public void RegisterNotificationClient()
        {
            _notificationRegistrationHelper.Register();
        }

        public void UnregisterNotificationClient()
        {
            _notificationRegistrationHelper.Unregister();
        }

        private Task StopSessionMonitoringAndDrainAsync()
        {
            return _sessionMonitoringFacade.StopAndDrainAsync(_sessionMonitoringDrainOverride);
        }

        /// <summary>
        /// Reconciles audio-session monitoring against all active playback and recording endpoints.
        /// </summary>
        /// <remarks>
        /// Reconcile operations are debounced and executed in background work to absorb endpoint churn during hotplug
        /// storms. Existing endpoint subscriptions are retained when still active, new ones are attached, and stale
        /// ones are detached/disposed.
        /// </remarks>
        private void UpdateSessionMonitoring()
        {
            _sessionMonitoringFacade.Update();
        }

        /// <summary>
        /// Handles low-level session-creation notifications and applies any persisted per-session volume.
        /// </summary>
        /// <remarks>
        /// Work is scheduled in background, then COM-sensitive session access is marshaled through the dedicated
        /// CoreAudio executor after a short initialization delay. On completion it emits
        /// <see cref="AudioSessionCreated"/> so UI layers can coalesce mixer refreshes.
        /// </remarks>
        private void OnSessionCreated(AudioMixerMode mixerMode, object? sender, ISessionMonitorSession newSession)
        {
            RunSessionCreatedErrorBoundary(() =>
            {
                ISessionMonitorSessionLease? acquiredLease = newSession.TryAcquireLease();
                if (acquiredLease == null)
                {
                    return;
                }

                ISessionMonitorSessionLease sessionLease = acquiredLease;
                bool queued = false;
                try
                {
                    queued = TryQueueSessionCreatedWork(
                        _disposed,
                        sessionLease,
                        RunBackgroundWork,
                        async shutdownToken =>
                        {
                            using (sessionLease)
                            {
                                await RunSessionCreatedHandlerAsync(
                                    token => TryRunSessionCreatedWorkBeforeNotifyAsync(
                                        () => _disposed,
                                        token => Task.Delay(AppConstants.Timing.SessionInitDelayMs, token),
                                        token => ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
                                        {
                                            sessionLease.UseNativeControl(sessionControl =>
                                                AudioDeviceSessionControlRestoreHelper.TryRestoreSession(
                                                    sessionLease.State,
                                                    sessionLease.ProcessId,
                                                    sessionLease.DisplayName,
                                                    (pid, displayName) => _sessionVolumeRestoreHelper.TryApplySavedVolume(
                                                        pid,
                                                        displayName,
                                                        (processName, resolvedDisplayName) => _volumeService.ApplySavedVolume(sessionControl, processName, resolvedDisplayName))));
                                        }, token),
                                        token),
                                    () => NotifyAudioSessionCreated(mixerMode),
                                    ex => _logger.Error("AudioDeviceService", "Error handling new session", nameof(OnSessionCreated), ex),
                                    shutdownToken);
                            }
                        },
                        nameof(OnSessionCreated));
                }
                finally
                {
                    if (!queued)
                    {
                        sessionLease.Dispose();
                    }
                }
            }, ex => _logger.Error("AudioDeviceService", "Error in OnSessionCreated handler", nameof(OnSessionCreated), ex));
        }

        internal static void RunSessionCreatedErrorBoundary(Action body, Action<Exception> logOuterFailure)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                logOuterFailure(ex);
            }
        }

        internal static bool TryQueueSessionCreatedWork(
            bool disposed,
            ISessionMonitorSessionLease? newSession,
            Action<Func<CancellationToken, Task>, string> runBackgroundWork,
            Func<CancellationToken, Task> backgroundHandler,
            string context)
        {
            if (disposed || newSession == null)
            {
                return false;
            }

            runBackgroundWork(backgroundHandler, context);
            return true;
        }

        internal static async Task RunSessionCreatedHandlerAsync(
            Func<CancellationToken, Task<bool>> tryRunBeforeNotifyAsync,
            Action notifyAudioSessionCreated,
            Action<Exception> logBackgroundFailure,
            CancellationToken shutdownToken)
        {
            try
            {
                bool shouldNotify = await tryRunBeforeNotifyAsync(shutdownToken);
                if (shouldNotify)
                {
                    notifyAudioSessionCreated();
                }
            }
            catch (Exception ex)
            {
                logBackgroundFailure(ex);
            }
        }

        internal static async Task<bool> TryRunSessionCreatedWorkBeforeNotifyAsync(
            Func<bool> isDisposed,
            Func<CancellationToken, Task> waitForInitializationAsync,
            Func<CancellationToken, Task> runRestoreAsync,
            CancellationToken shutdownToken)
        {
            if (shutdownToken.IsCancellationRequested || isDisposed())
            {
                return false;
            }

            await waitForInitializationAsync(shutdownToken);

            if (shutdownToken.IsCancellationRequested || isDisposed())
            {
                return false;
            }

            await runRestoreAsync(shutdownToken);
            return true;
        }
    }
}

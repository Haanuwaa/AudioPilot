using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using RoutineAppStartProcessSnapshot = AudioPilot.Platform.RoutineProcessSnapshot;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        internal readonly record struct PendingMixerRefreshSignalsForTests(
            int PendingSessionCreatedSignals,
            int PendingSessionLifecycleSignals,
            int PendingShowWindowMixerRefreshSignals,
            bool HasSessionRefreshDebounce,
            int PendingOutputSessionCreatedSignals,
            int PendingInputSessionCreatedSignals,
            int PendingOutputSessionLifecycleSignals,
            int PendingInputSessionLifecycleSignals);

        internal static Func<bool, string, bool>? TryApplyStartupChangeOverrideForTests { get; set; }

        internal Task ApplySettingsForTestsAsync() => ApplySettingsAsync();

        internal Task SaveSettingsForTestsAsync() => SaveSettingsAsync();

        internal Task ExportSettingsForTestsAsync() => ExportSettingsAsync();

        internal Task ImportSettingsForTestsAsync() => ImportSettingsAsync();

        internal Task ResetPerAppAudioRoutingForTestsAsync() => ResetPerAppAudioRoutingAsync();

        internal void SetIsInitializingForTests(bool value) => _isInitializing = value;

        internal bool IsCleaningUpForTests() => _isCleaningUp;

        internal void SetIsCleaningUpForTests(bool value) => _isCleaningUp = value;

        internal void RequestStopAudioEndpointTestForTests(AudioEndpointTestStopReason reason)
            => RequestStopAudioEndpointTest(reason);

        internal void ApplyAudioEndpointTestStateForTests(AudioEndpointTestState state)
            => ApplyAudioEndpointTestState(state);

        internal void SetCachedSettingsForTests(Settings settings) => _cachedSettings = settings;

        internal long GetAutoSaveDirtyRevisionForTests() => Volatile.Read(ref _autoSaveDirtyRevision);

        internal void SetIsSavingForTests(bool value) => IsSaving = value;

        internal void TryQueueTrailingAutoSaveForTests(long revisionAtStart, string trigger)
            => TryQueueTrailingAutoSave(revisionAtStart, trigger);

        internal async Task EnterSettingsWriteLockForTestsAsync()
        {
            await _settingsWriteSemaphore.WaitAsync();
        }

        internal void ReleaseSettingsWriteLockForTests()
        {
            if (_settingsWriteSemaphore.CurrentCount == 0)
            {
                _settingsWriteSemaphore.Release();
            }
        }

        internal async Task WaitForQueuedBackgroundTasksForTestsAsync()
        {
            while (true)
            {
                Task[] pendingTasks = BackgroundTaskHelper.SnapshotPendingTasks(_backgroundTasks);
                if (pendingTasks.Length == 0)
                {
                    if (_backgroundWorkHelper.DeferredOperationCountForTests == 0)
                    {
                        return;
                    }

                    await Task.Yield();
                    continue;
                }

                await Task.WhenAll(pendingTasks);
            }
        }

        internal int GetBackgroundTaskCountForTests() => _backgroundTasks.Count;

        internal Dictionary<string, RoutineAppOutputLease> GetActiveRoutineAppOutputLeasesForTests() => _activeRoutineAppOutputLeases;

        internal Dictionary<string, RoutineStatefulSession> GetActiveRoutineStatefulSessionsForTests() => _activeRoutineStatefulSessions;

        internal void SetAppStartTriggeredRoutinesForTests(IEnumerable<AudioRoutine> routines)
        {
            _appStartTriggeredRoutines = [.. routines.Select(CloneRoutineForTests)];
        }

        internal void LogRoutineAppStartMatchSkippedForTests(RoutineAppStartMatch match, string reason, string? correlatedOperationId = null)
            => LogRoutineAppStartMatchSkipped(match, reason, correlatedOperationId);

        internal void UpdateRoutineAppStartMonitorStateForTests()
            => UpdateRoutineAppStartMonitorState();

        internal void RecalculateRoutineAppOutputLeasePendingCountsForTests()
        {
            lock (_routineAppStartMonitorLock)
            {
                RecalculateRoutineAppOutputLeasePendingCountsLocked();
            }
        }

        internal void MarkRoutineAppOutputLeaseCompletedForTests(RoutineAppOutputLease lease)
            => MarkRoutineAppOutputLeaseCompleted(lease);

        internal void CompletePendingAppAudioWaitForRemovedLeasesForTests(IReadOnlyList<RoutineAppOutputLease> removedLeases)
            => CompletePendingAppAudioWaitForRemovedLeases(removedLeases);

        internal void RegisterRoutineAppOutputLeaseForTests(
            AudioRoutine routine,
            int rootProcessId,
            bool outputApplied,
            bool inputApplied,
            bool completionOverlayShown)
            => RegisterRoutineAppOutputLease(routine, rootProcessId, outputApplied, inputApplied, completionOverlayShown);

        internal bool TryClaimRoutineAppStartMatchForTests(
            string? routineId,
            int processId,
            RoutineAppStartProcessSnapshot processSnapshot)
        {
            AudioRoutine routine = new()
            {
                Id = routineId ?? string.Empty,
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = processSnapshot.ExecutablePath,
            };

            return TryClaimRoutineAppStartMatch(routine, processId, processSnapshot, out _);
        }

        internal Task DeactivateRoutineStatefulSessionForTestsAsync(string sessionKey, long? restoreActivationSequence = null)
            => DeactivateRoutineStatefulSessionAsync(sessionKey, restoreActivationSequence);

        internal Task DeactivateRoutineFromApplicationTriggerForTestsAsync(
            AudioRoutine routine,
            int processId,
            Func<string, Task> deactivateSessionAsync)
            => DeactivateRoutineFromApplicationTrigger(routine, processId, deactivateSessionAsync);

        internal Task ExecuteRoutineFromApplicationTriggerForTestsAsync(
            AudioRoutine routine,
            Func<Task> executeRoutineAsync)
            => ExecuteRoutineFromApplicationTrigger(routine, executeRoutineAsync);

        internal Task ApplyRoutineAppOutputLeasesForTestsAsync(
            string operationId,
            int coalescedSignals,
            CancellationToken cancellationToken)
            => ApplyRoutineAppOutputLeasesAsync(operationId, coalescedSignals, cancellationToken);

        internal void ConfigureSteamBigPictureFallbackForTests(IEnumerable<AudioRoutine> routines, bool monitoringEnabled = true)
        {
            _routineAppStartMonitoringEnabled = monitoringEnabled;
            _steamBigPictureMonitorRunning = false;
            _steamBigPictureMonitorStartPending = false;
            _steamBigPictureUsingFallbackRevalidation = true;
            _steamBigPictureTriggeredRoutines = [.. routines.Select(CloneRoutineForTests)];
            _activeRoutineStatefulSessions.Clear();
        }

        internal int GetPendingSteamBigPictureSignalCountForTests()
        {
            return System.Threading.Interlocked.CompareExchange(ref _pendingSteamBigPictureSignals, 0, 0);
        }

        internal bool HasSteamBigPictureDebounceForTests() => _steamBigPictureDebounceCts != null;

        internal bool IsUsingSteamBigPictureFallbackForTests() => _steamBigPictureUsingFallbackRevalidation;

        internal bool TryRecoverSteamBigPictureMonitorFromFallbackForTests()
            => TryRecoverSteamBigPictureMonitorFromFallback();

        internal void InvokeRoutineLastRunRefreshTimerTickForTests()
            => OnRoutineLastRunRefreshTimerTick(this, EventArgs.Empty);

        internal void SetRoutineLastRunStateForTests(string routineId, RoutineLastRunState state, string? detail = null)
            => SetRoutineLastRunState(routineId, state, detail);

        internal void SetPendingSessionCreatedSignalsForTests(int value)
        {
            System.Threading.Interlocked.Exchange(ref _pendingOutputSessionCreatedSignals, value);
            System.Threading.Interlocked.Exchange(ref _pendingInputSessionCreatedSignals, 0);
        }

        internal void SetPendingSessionCreatedSignalsForTests(AudioMixerMode mixerMode, int value)
        {
            ref int target = ref mixerMode == AudioMixerMode.Input
                ? ref _pendingInputSessionCreatedSignals
                : ref _pendingOutputSessionCreatedSignals;
            System.Threading.Interlocked.Exchange(ref target, value);
        }

        internal PendingMixerRefreshSignalsForTests GetPendingMixerRefreshSignalsForTests()
        {
            int pendingOutputSessionCreatedSignals = System.Threading.Interlocked.CompareExchange(ref _pendingOutputSessionCreatedSignals, 0, 0);
            int pendingInputSessionCreatedSignals = System.Threading.Interlocked.CompareExchange(ref _pendingInputSessionCreatedSignals, 0, 0);
            int pendingOutputSessionLifecycleSignals = System.Threading.Interlocked.CompareExchange(ref _pendingOutputSessionLifecycleSignals, 0, 0);
            int pendingInputSessionLifecycleSignals = System.Threading.Interlocked.CompareExchange(ref _pendingInputSessionLifecycleSignals, 0, 0);
            return new PendingMixerRefreshSignalsForTests(
                pendingOutputSessionCreatedSignals + pendingInputSessionCreatedSignals,
                pendingOutputSessionLifecycleSignals + pendingInputSessionLifecycleSignals,
                System.Threading.Interlocked.CompareExchange(ref _pendingShowWindowMixerRefreshSignals, 0, 0),
                _sessionRefreshDebounceCts != null,
                pendingOutputSessionCreatedSignals,
                pendingInputSessionCreatedSignals,
                pendingOutputSessionLifecycleSignals,
                pendingInputSessionLifecycleSignals);
        }

        internal static void ResetTestHooks()
        {
            TryApplyStartupChangeOverrideForTests = null;
            ExitApplicationOverrideForTests = null;
            ApplyRoutineAbsoluteVolumeOverrideForTests = null;
            ResetSettingsTransferDialogsForTests();
        }

        internal void SetKnownActiveDeviceInfosForTests(
            IReadOnlyList<CycleDevice> outputDevices,
            IReadOnlyList<CycleDevice> inputDevices)
        {
            _outputDevices.Clear();
            _outputDevices.AddRange(outputDevices.Select(CloneCycleDeviceForTests));
            _inputDevices.Clear();
            _inputDevices.AddRange(inputDevices.Select(CloneCycleDeviceForTests));
        }

        private static CycleDevice CloneCycleDeviceForTests(CycleDevice device)
        {
            return new CycleDevice
            {
                Id = device.Id,
                Name = device.Name,
                DisplayOrder = device.DisplayOrder,
            };
        }

        private static AudioRoutine CloneRoutineForTests(AudioRoutine routine)
        {
            return new AudioRoutine
            {
                Id = routine.Id,
                Name = routine.Name,
                Enabled = routine.Enabled,
                TriggerKind = routine.TriggerKind,
                OutputDeviceId = routine.OutputDeviceId,
                OutputDeviceName = routine.OutputDeviceName,
                InputDeviceId = routine.InputDeviceId,
                InputDeviceName = routine.InputDeviceName,
                Hotkey = routine.Hotkey,
                UsesApplicationTrigger = routine.UsesApplicationTrigger,
                TriggerAppPath = routine.TriggerAppPath,
                ApplicationTriggerMode = routine.ApplicationTriggerMode,
                ApplicationTriggerTitlePattern = routine.ApplicationTriggerTitlePattern,
                ApplicationTriggerTitleMatchMode = routine.ApplicationTriggerTitleMatchMode,
                ShowInTrayMenu = routine.ShowInTrayMenu,
                RestorePreviousAudioOnDeactivate = routine.RestorePreviousAudioOnDeactivate,
            };
        }
    }
}

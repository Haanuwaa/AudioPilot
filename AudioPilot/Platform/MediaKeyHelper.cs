using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Windows.Media.Control;

namespace AudioPilot.Platform
{
    public static partial class MediaKeyHelper
    {
        private const uint ExpectedInputCount = 2;

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
            public static int Size => Marshal.SizeOf<INPUT>();
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg; public ushort wParamL; public ushort wParamH;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
        private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
        private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

        internal enum SystemMediaCommand
        {
            PlayPause,
            NextTrack,
            PreviousTrack,
        }

        internal enum MediaCommandRouteKind
        {
            None,
            Delegate,
            TestOverride,
            CurrentGsmc,
            PlayingGsmc,
            ControllableGsmc,
            SendInputFallback,
        }

        internal enum PlayPauseOperation
        {
            None,
            Toggle,
            Play,
            Pause,
        }

        internal readonly record struct MediaCommandSendOutcome(
            bool Sent,
            MediaCommandRouteKind Route,
            bool SuppressFallback = false,
            string? CandidateSourceAppUserModelId = null,
            string? FailureReason = null,
            double ElapsedMs = 0,
            int? ErrorCode = null)
        {
            public static MediaCommandSendOutcome FromDelegate(bool sent) =>
                new(sent, MediaCommandRouteKind.Delegate, FailureReason: sent ? null : "delegate-returned-false");

            public bool UsedSendInputFallback => Route == MediaCommandRouteKind.SendInputFallback;
        }

        private enum SystemMediaCommandCandidateKind
        {
            Current,
            Playing,
            Controllable,
        }

        private readonly record struct SystemMediaCommandCandidate(
            GlobalSystemMediaTransportControlsSession Session,
            GlobalSystemMediaTransportControlsSessionPlaybackInfo PlaybackInfo,
            SystemMediaCommandCandidateKind Kind);

        private readonly record struct SystemMediaManagerLease(
            GlobalSystemMediaTransportControlsSessionManager Manager,
            Task<GlobalSystemMediaTransportControlsSessionManager> RequestTask);

        private readonly record struct SystemMediaSessionState(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> Sessions,
            GlobalSystemMediaTransportControlsSession? Current);

        private static readonly Lock _lock = new();
        private static readonly Lock _systemMediaManagerLock = new();
        private static Task<GlobalSystemMediaTransportControlsSessionManager>? _systemMediaManagerTask;

        internal static Func<ushort, (uint Result, int ErrorCode)>? SendInputOverrideForTests { get; set; }
        internal static Func<ushort, nuint, (uint Result, int ErrorCode)>? DetailedSendInputOverrideForTests { get; set; }
        internal static Func<Task<GlobalSystemMediaTransportControlsSessionManager>>? SystemMediaManagerRequestOverrideForTests { get; set; }
        internal static Func<SystemMediaCommand, MediaCommandSendOutcome>? DetailedSystemMediaCommandOverrideForTests { get; set; }
        internal static Func<SystemMediaCommand, bool>? SystemMediaCommandOverrideForTests { get; set; }
        internal static ILogger? LoggerOverrideForTests { get; set; }

        public static bool TryPressPlayPause() => TryPressPlayPauseDetailed().Sent;
        public static bool TryPressNextTrack() => TryPressNextTrackDetailed().Sent;
        public static bool TryPressPreviousTrack() => TryPressPreviousTrackDetailed().Sent;
        public static async Task<bool> TryPressPlayPauseAsync(CancellationToken cancellationToken = default) => (await TryPressPlayPauseDetailedAsync(cancellationToken).ConfigureAwait(false)).Sent;
        public static async Task<bool> TryPressNextTrackAsync(CancellationToken cancellationToken = default) => (await TryPressNextTrackDetailedAsync(cancellationToken).ConfigureAwait(false)).Sent;
        public static async Task<bool> TryPressPreviousTrackAsync(CancellationToken cancellationToken = default) => (await TryPressPreviousTrackDetailedAsync(cancellationToken).ConfigureAwait(false)).Sent;
        internal static MediaCommandSendOutcome TryPressPlayPauseDetailed() => SendCommandDetailed(SystemMediaCommand.PlayPause, VK_MEDIA_PLAY_PAUSE, "PlayPause");
        internal static MediaCommandSendOutcome TryPressNextTrackDetailed() => SendCommandDetailed(SystemMediaCommand.NextTrack, VK_MEDIA_NEXT_TRACK, "NextTrack");
        internal static MediaCommandSendOutcome TryPressPreviousTrackDetailed() => SendCommandDetailed(SystemMediaCommand.PreviousTrack, VK_MEDIA_PREV_TRACK, "PreviousTrack");
        internal static Task<MediaCommandSendOutcome> TryPressPlayPauseDetailedAsync(CancellationToken cancellationToken = default) => SendCommandDetailedAsync(SystemMediaCommand.PlayPause, VK_MEDIA_PLAY_PAUSE, "PlayPause", cancellationToken);
        internal static Task<MediaCommandSendOutcome> TryPressNextTrackDetailedAsync(CancellationToken cancellationToken = default) => SendCommandDetailedAsync(SystemMediaCommand.NextTrack, VK_MEDIA_NEXT_TRACK, "NextTrack", cancellationToken);
        internal static Task<MediaCommandSendOutcome> TryPressPreviousTrackDetailedAsync(CancellationToken cancellationToken = default) => SendCommandDetailedAsync(SystemMediaCommand.PreviousTrack, VK_MEDIA_PREV_TRACK, "PreviousTrack", cancellationToken);
        public static async Task PrewarmSystemMediaCommandsAsync()
        {
            if (DetailedSystemMediaCommandOverrideForTests != null
                || SystemMediaCommandOverrideForTests != null
                || DetailedSendInputOverrideForTests != null
                || SendInputOverrideForTests != null)
            {
                return;
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(AppConstants.Timing.SystemMediaCommandTimeoutMs));
                _ = (await GetSystemMediaManagerAsync(timeoutCts.Token).ConfigureAwait(false)).Manager;
                GetLogger()?.Trace("MediaKeyHelper", "media-command-gsmc-prewarm-completed", nameof(PrewarmSystemMediaCommandsAsync));
            }
            catch (OperationCanceledException)
            {
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-prewarm-timeout timeoutMs={AppConstants.Timing.SystemMediaCommandTimeoutMs}", nameof(PrewarmSystemMediaCommandsAsync));
            }
            catch (Exception ex)
            {
                ResetFaultedSystemMediaManagerTask();
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-prewarm-failed reason={ex.GetType().Name}", nameof(PrewarmSystemMediaCommandsAsync));
            }
        }

        private static MediaCommandSendOutcome SendCommandDetailed(SystemMediaCommand command, ushort fallbackVk, string keyName)
        {
            long started = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                MediaCommandSendOutcome result = TrySendSystemMediaCommand(command, keyName);
                if (result.Sent)
                {
                    return CompleteOutcome(result, started);
                }

                if (result.SuppressFallback)
                {
                    return CompleteOutcome(result, started);
                }

                return CompleteOutcome(SendInputFallback(fallbackVk, keyName, result), started);
            }
        }

        private static async Task<MediaCommandSendOutcome> SendCommandDetailedAsync(SystemMediaCommand command, ushort fallbackVk, string keyName, CancellationToken cancellationToken)
        {
            long started = Stopwatch.GetTimestamp();
            MediaCommandSendOutcome result = await TrySendSystemMediaCommandAsync(command, keyName, cancellationToken).ConfigureAwait(false);
            if (result.Sent)
            {
                return CompleteOutcome(result, started);
            }

            if (result.SuppressFallback)
            {
                return CompleteOutcome(result, started);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CompleteOutcome(SendInputFallback(fallbackVk, keyName, result), started);
            }
        }

        internal static void ResetTestHooks()
        {
            SendInputOverrideForTests = null;
            DetailedSendInputOverrideForTests = null;
            SystemMediaManagerRequestOverrideForTests = null;
            DetailedSystemMediaCommandOverrideForTests = null;
            SystemMediaCommandOverrideForTests = null;
            LoggerOverrideForTests = null;
            ResetSystemMediaManagerTask();
        }

        private static MediaCommandSendOutcome TrySendSystemMediaCommand(SystemMediaCommand command, string keyName)
            => TrySendSystemMediaCommandAsync(command, keyName, CancellationToken.None).GetAwaiter().GetResult();

        private static async Task<MediaCommandSendOutcome> TrySendSystemMediaCommandAsync(SystemMediaCommand command, string keyName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DetailedSystemMediaCommandOverrideForTests != null)
            {
                return DetailedSystemMediaCommandOverrideForTests(command);
            }

            if (SystemMediaCommandOverrideForTests != null)
            {
                bool sent = SystemMediaCommandOverrideForTests(command);
                return new MediaCommandSendOutcome(
                    sent,
                    MediaCommandRouteKind.TestOverride,
                    FailureReason: sent ? null : "test-override-returned-false");
            }

            if (DetailedSendInputOverrideForTests != null || SendInputOverrideForTests != null)
            {
                return new MediaCommandSendOutcome(
                    Sent: false,
                    MediaCommandRouteKind.None,
                    FailureReason: "sendinput-test-override");
            }

            try
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(AppConstants.Timing.SystemMediaCommandTimeoutMs));
                return await TrySendSystemMediaCommandCoreAsync(command, keyName, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-timeout:{keyName} timeoutMs={AppConstants.Timing.SystemMediaCommandTimeoutMs}", nameof(TrySendSystemMediaCommand));
                return new MediaCommandSendOutcome(Sent: false, MediaCommandRouteKind.None, FailureReason: "gsmc-timeout");
            }
            catch (Exception ex)
            {
                ResetFaultedSystemMediaManagerTask();
                GetLogger()?.Trace("MediaKeyHelper", () => $"media-command-gsmc-fallback:{keyName} reason={ex.GetType().Name}", nameof(TrySendSystemMediaCommand));
                return new MediaCommandSendOutcome(Sent: false, MediaCommandRouteKind.None, FailureReason: $"gsmc-{ex.GetType().Name}");
            }
        }

        private static MediaCommandSendOutcome SendInputFallback(ushort fallbackVk, string keyName, MediaCommandSendOutcome previousOutcome)
        {
            try
            {
                var (result, errorCode) = SendInputMediaKey(fallbackVk);

                if (result != ExpectedInputCount)
                {
                    Exception failure = errorCode != 0
                        ? new System.ComponentModel.Win32Exception(errorCode)
                        : new InvalidOperationException($"SendInput returned {result} instead of {ExpectedInputCount}.");
                    GetLogger()?.Error("MediaKeyHelper", $"media-key-send-failed:{keyName}", nameof(SendCommandDetailed), failure);
                    return previousOutcome with
                    {
                        Sent = false,
                        Route = MediaCommandRouteKind.SendInputFallback,
                        FailureReason = "sendinput-partial",
                        ErrorCode = errorCode,
                    };
                }

                return previousOutcome with
                {
                    Sent = true,
                    Route = MediaCommandRouteKind.SendInputFallback,
                    FailureReason = null,
                    ErrorCode = null,
                };
            }
            catch (Exception ex)
            {
                GetLogger()?.Error("MediaKeyHelper", $"media-key-send-exception:{keyName}", nameof(SendCommandDetailed), ex);
                return previousOutcome with
                {
                    Sent = false,
                    Route = MediaCommandRouteKind.SendInputFallback,
                    FailureReason = $"sendinput-{ex.GetType().Name}",
                };
            }
        }

        private static async Task<MediaCommandSendOutcome> TrySendSystemMediaCommandCoreAsync(SystemMediaCommand command, string keyName, CancellationToken cancellationToken)
        {
            SystemMediaManagerLease managerLease = await GetSystemMediaManagerAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            SystemMediaSessionState sessionState = ReadSystemMediaSessionState(managerLease);
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = sessionState.Sessions;
            GlobalSystemMediaTransportControlsSession? current = sessionState.Current;

            List<SystemMediaCommandCandidate> candidates = SelectSystemMediaCommandCandidates(sessions, current, command);
            LogRoutingDiagnosticsIfNeeded(
                sessions,
                current,
                candidates,
                command,
                keyName);

            foreach (SystemMediaCommandCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool sent;
                try
                {
                    sent = await TrySendSystemMediaCommandAsync(candidate, command, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    GetLogger()?.Trace(
                        "MediaKeyHelper",
                        () => $"media-command-candidate-failed:{keyName} candidate={candidate.Kind} source={GetSafeSessionSourceForLog(candidate.Session)} reason={ex.GetType().Name}",
                        nameof(TrySendSystemMediaCommand));
                    continue;
                }

                if (sent)
                {
                    GetLogger()?.Trace(
                        "MediaKeyHelper",
                        () => $"media-command-sent:gsmc:{keyName} candidate={candidate.Kind} source={GetSafeSessionSourceForLog(candidate.Session)}",
                        nameof(TrySendSystemMediaCommand));
                    return BuildSystemMediaOutcome(
                        sent: true,
                        candidate,
                        suppressFallback: false,
                        failureReason: null);
                }
            }

            return new MediaCommandSendOutcome(
                Sent: false,
                MediaCommandRouteKind.None,
                SuppressFallback: false,
                FailureReason: "no-system-media-candidate");
        }

        private static MediaCommandSendOutcome BuildSystemMediaOutcome(
            bool sent,
            SystemMediaCommandCandidate candidate,
            bool suppressFallback,
            string? failureReason)
        {
            return new MediaCommandSendOutcome(
                sent,
                GetRouteKind(candidate.Kind),
                SuppressFallback: suppressFallback,
                CandidateSourceAppUserModelId: GetSafeSessionSource(candidate.Session),
                FailureReason: failureReason);
        }

        private static MediaCommandRouteKind GetRouteKind(SystemMediaCommandCandidateKind candidateKind)
        {
            return candidateKind switch
            {
                SystemMediaCommandCandidateKind.Current => MediaCommandRouteKind.CurrentGsmc,
                SystemMediaCommandCandidateKind.Playing => MediaCommandRouteKind.PlayingGsmc,
                SystemMediaCommandCandidateKind.Controllable => MediaCommandRouteKind.ControllableGsmc,
                _ => MediaCommandRouteKind.None,
            };
        }

        private static MediaCommandSendOutcome CompleteOutcome(MediaCommandSendOutcome outcome, long startedTimestamp)
        {
            return outcome with
            {
                ElapsedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            };
        }

        private static void LogRoutingDiagnosticsIfNeeded(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            GlobalSystemMediaTransportControlsSession? current,
            IReadOnlyCollection<SystemMediaCommandCandidate> candidates,
            SystemMediaCommand command,
            string keyName)
        {
            ILogger? logger = GetLogger();
            if (logger == null ||
                !logger.IsEnabled(LogLevel.Trace))
            {
                return;
            }

            try
            {
                bool currentSupports = SupportsSystemMediaCommand(current, command);
                int commandableCount = sessions.Count(session => SupportsSystemMediaCommand(session, command));
                int playingCount = sessions.Count(session => session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                string currentSource = LogPrivacy.Id(current?.SourceAppUserModelId);
                string sessionSummary = BuildMediaCommandSessionSummary(sessions, command);
                string routeSelectionReason = ClassifyRouteSelectionReason(sessions, current, currentSupports, command);
                string nextRoute = candidates.Count == 0
                    ? "none"
                    : candidates.First().Kind.ToString();

                logger.Trace(
                    "MediaKeyHelper",
                    () => $"media-command-routing-diagnostics | command={keyName} selectionReason={routeSelectionReason} nextRoute={nextRoute} sessions={sessions.Count} commandable={commandableCount} playing={playingCount} currentSource={currentSource} currentSupports={currentSupports} candidates={candidates.Count} sessionSummary={sessionSummary}",
                    nameof(TrySendSystemMediaCommand));
            }
            catch (Exception ex)
            {
                logger.Trace(
                    "MediaKeyHelper",
                    () => $"media-command-routing-diagnostics-failed | command={keyName} reason={ex.GetType().Name}",
                    nameof(TrySendSystemMediaCommand));
            }
        }

        private static string BuildMediaCommandSessionSummary(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            SystemMediaCommand command)
        {
            if (sessions.Count == 0)
            {
                return "[]";
            }

            IEnumerable<string> summaries = sessions.Take(5).Select(session =>
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = session.GetPlaybackInfo();
                return $"{LogPrivacy.Id(session.SourceAppUserModelId)}:status={playbackInfo.PlaybackStatus}:supports={SupportsSystemMediaCommand(session, command)}";
            });

            string suffix = sessions.Count > 5 ? $",+{sessions.Count - 5}" : string.Empty;
            return $"[{string.Join(",", summaries)}{suffix}]";
        }

        private static string ClassifyRouteSelectionReason(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            GlobalSystemMediaTransportControlsSession? current,
            bool currentSupports,
            SystemMediaCommand command)
        {
            if (sessions.Count == 0)
            {
                return "NoMediaSessions";
            }

            bool anyCommandable = sessions.Any(session => SupportsSystemMediaCommand(session, command));
            if (!anyCommandable)
            {
                return "NoCommandableSessions";
            }

            if (currentSupports)
            {
                return "CurrentCommandable";
            }

            bool anyPlayingCommandable = sessions.Any(session =>
                session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && SupportsSystemMediaCommand(session, command));
            if (anyPlayingCommandable)
            {
                return "PlayingCommandable";
            }

            return current is null
                ? "NoCurrentSession"
                : "ControllableFallback";
        }

        private static async Task<bool> TrySendSystemMediaCommandAsync(
            SystemMediaCommandCandidate candidate,
            SystemMediaCommand command,
            CancellationToken cancellationToken)
        {
            GlobalSystemMediaTransportControlsSession session = candidate.Session;
            return command switch
            {
                SystemMediaCommand.PlayPause => await TrySendPlayPauseAsync(session, candidate.PlaybackInfo, cancellationToken).ConfigureAwait(false),
                SystemMediaCommand.NextTrack => await session.TrySkipNextAsync().AsTask(cancellationToken).ConfigureAwait(false),
                SystemMediaCommand.PreviousTrack => await session.TrySkipPreviousAsync().AsTask(cancellationToken).ConfigureAwait(false),
                _ => false,
            };
        }

        private static async Task<bool> TrySendPlayPauseAsync(
            GlobalSystemMediaTransportControlsSession session,
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo,
            CancellationToken cancellationToken)
        {
            PlayPauseOperation operation = SelectPlayPauseOperation(
                playbackInfo.PlaybackStatus,
                playbackInfo.Controls.IsPlayPauseToggleEnabled,
                playbackInfo.Controls.IsPlayEnabled,
                playbackInfo.Controls.IsPauseEnabled);

            return operation switch
            {
                PlayPauseOperation.Toggle => await session.TryTogglePlayPauseAsync().AsTask(cancellationToken).ConfigureAwait(false),
                PlayPauseOperation.Play => await session.TryPlayAsync().AsTask(cancellationToken).ConfigureAwait(false),
                PlayPauseOperation.Pause => await session.TryPauseAsync().AsTask(cancellationToken).ConfigureAwait(false),
                _ => false,
            };
        }

        internal static PlayPauseOperation SelectPlayPauseOperation(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus,
            bool canToggle,
            bool canPlay,
            bool canPause)
        {
            if (canToggle)
            {
                return PlayPauseOperation.Toggle;
            }

            if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return canPause ? PlayPauseOperation.Pause : PlayPauseOperation.None;
            }

            return canPlay ? PlayPauseOperation.Play : PlayPauseOperation.None;
        }

        private static SystemMediaSessionState ReadSystemMediaSessionState(SystemMediaManagerLease managerLease)
        {
            try
            {
                return new SystemMediaSessionState(
                    managerLease.Manager.GetSessions(),
                    managerLease.Manager.GetCurrentSession());
            }
            catch
            {
                InvalidateSystemMediaManagerTask(managerLease.RequestTask);
                throw;
            }
        }

        internal static Task<bool> ProbeSystemMediaManagerForTestsAsync() =>
            ProbeSystemMediaManagerCoreForTestsAsync(CancellationToken.None);

        internal static async Task<bool> ProbeCancelledSystemMediaManagerForTestsAsync()
        {
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            return await ProbeSystemMediaManagerCoreForTestsAsync(cancellationSource.Token).ConfigureAwait(false);
        }

        private static async Task<bool> ProbeSystemMediaManagerCoreForTestsAsync(CancellationToken cancellationToken)
        {
            try
            {
                SystemMediaManagerLease managerLease =
                    await GetSystemMediaManagerAsync(cancellationToken).ConfigureAwait(false);
                _ = ReadSystemMediaSessionState(managerLease);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<SystemMediaManagerLease> GetSystemMediaManagerAsync(CancellationToken cancellationToken)
        {
            Task<GlobalSystemMediaTransportControlsSessionManager> requestTask;
            lock (_systemMediaManagerLock)
            {
                if (_systemMediaManagerTask == null || _systemMediaManagerTask.IsCanceled || _systemMediaManagerTask.IsFaulted)
                {
                    _systemMediaManagerTask = SystemMediaManagerRequestOverrideForTests?.Invoke()
                        ?? GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
                }

                requestTask = _systemMediaManagerTask;
            }

            try
            {
                GlobalSystemMediaTransportControlsSessionManager manager =
                    await requestTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new SystemMediaManagerLease(manager, requestTask);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!requestTask.IsCompleted)
                {
                    InvalidateSystemMediaManagerTask(requestTask);
                }

                throw;
            }
        }

        private static void ResetFaultedSystemMediaManagerTask()
        {
            lock (_systemMediaManagerLock)
            {
                if (_systemMediaManagerTask?.IsFaulted == true || _systemMediaManagerTask?.IsCanceled == true)
                {
                    _systemMediaManagerTask = null;
                }
            }
        }

        private static void InvalidateSystemMediaManagerTask(
            Task<GlobalSystemMediaTransportControlsSessionManager> expectedTask)
        {
            lock (_systemMediaManagerLock)
            {
                if (ReferenceEquals(_systemMediaManagerTask, expectedTask))
                {
                    _systemMediaManagerTask = null;
                }
            }
        }

        private static void ResetSystemMediaManagerTask()
        {
            lock (_systemMediaManagerLock)
            {
                _systemMediaManagerTask = null;
            }
        }

        private static List<SystemMediaCommandCandidate> SelectSystemMediaCommandCandidates(
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
            GlobalSystemMediaTransportControlsSession? current,
            SystemMediaCommand command)
        {
            List<SystemMediaCommandCandidate> candidates = [];

            if (TryCreateCandidate(current, command, SystemMediaCommandCandidateKind.Current, out SystemMediaCommandCandidate currentCandidate))
            {
                AddCandidateIfNew(candidates, currentCandidate);
            }

            List<SystemMediaCommandCandidate> commandableSessions = [];
            foreach (GlobalSystemMediaTransportControlsSession session in sessions)
            {
                if (TryCreateCandidate(session, command, SystemMediaCommandCandidateKind.Controllable, out SystemMediaCommandCandidate candidate))
                {
                    commandableSessions.Add(candidate);
                }
            }

            foreach (SystemMediaCommandCandidate candidate in commandableSessions)
            {
                if (candidate.PlaybackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    AddCandidateIfNew(candidates, candidate with { Kind = SystemMediaCommandCandidateKind.Playing });
                }
            }

            foreach (SystemMediaCommandCandidate candidate in commandableSessions)
            {
                AddCandidateIfNew(candidates, candidate);
            }

            return candidates;
        }

        private static bool TryCreateCandidate(
            GlobalSystemMediaTransportControlsSession? session,
            SystemMediaCommand command,
            SystemMediaCommandCandidateKind kind,
            out SystemMediaCommandCandidate candidate)
        {
            candidate = default;
            if (session == null)
            {
                return false;
            }

            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = session.GetPlaybackInfo();
                if (!SupportsSystemMediaCommand(playbackInfo, command))
                {
                    return false;
                }

                candidate = new SystemMediaCommandCandidate(session, playbackInfo, kind);
                return true;
            }
            catch (Exception ex)
            {
                GetLogger()?.Trace(
                    "MediaKeyHelper",
                    () => $"media-command-session-skipped | source={GetSafeSessionSourceForLog(session)} reason={ex.GetType().Name}",
                    nameof(SelectSystemMediaCommandCandidates));
                return false;
            }
        }

        private static void AddCandidateIfNew(
            List<SystemMediaCommandCandidate> candidates,
            SystemMediaCommandCandidate candidate)
        {
            if (!candidates.Any(existing => ReferenceEquals(existing.Session, candidate.Session)))
            {
                candidates.Add(candidate);
            }
        }

        private static bool SupportsSystemMediaCommand(
            GlobalSystemMediaTransportControlsSession? session,
            SystemMediaCommand command)
        {
            if (session == null)
            {
                return false;
            }

            try
            {
                return SupportsSystemMediaCommand(session.GetPlaybackInfo(), command);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SupportsSystemMediaCommand(
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo,
            SystemMediaCommand command)
        {
            GlobalSystemMediaTransportControlsSessionPlaybackControls controls = playbackInfo.Controls;
            return command switch
            {
                SystemMediaCommand.PlayPause => SelectPlayPauseOperation(
                    playbackInfo.PlaybackStatus,
                    controls.IsPlayPauseToggleEnabled,
                    controls.IsPlayEnabled,
                    controls.IsPauseEnabled) != PlayPauseOperation.None,
                SystemMediaCommand.NextTrack => controls.IsNextEnabled,
                SystemMediaCommand.PreviousTrack => controls.IsPreviousEnabled,
                _ => false,
            };
        }

        private static string? GetSafeSessionSource(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                return session.SourceAppUserModelId;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetSafeSessionSourceForLog(GlobalSystemMediaTransportControlsSession session)
            => LogPrivacy.Id(GetSafeSessionSource(session));

        private static (uint Result, int ErrorCode) SendInputMediaKey(ushort vk)
        {
            if (DetailedSendInputOverrideForTests != null)
            {
                return DetailedSendInputOverrideForTests(vk, AppConstants.Hotkeys.SyntheticMediaInputMarker);
            }

            if (SendInputOverrideForTests != null)
            {
                return SendInputOverrideForTests(vk);
            }

            INPUT[] inputs =
            [
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = vk,
                            dwFlags = KEYEVENTF_EXTENDEDKEY,
                            dwExtraInfo = (IntPtr)AppConstants.Hotkeys.SyntheticMediaInputMarker,
                        }
                    }
                },
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = vk,
                            dwFlags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP,
                            dwExtraInfo = (IntPtr)AppConstants.Hotkeys.SyntheticMediaInputMarker,
                        }
                    }
                }
            ];

            uint result = SendInput(ExpectedInputCount, inputs, INPUT.Size);
            int errorCode = result == ExpectedInputCount ? 0 : Marshal.GetLastWin32Error();
            return (result, errorCode);
        }

        private static ILogger? GetLogger()
        {
            return LoggerOverrideForTests ?? Logger.Instance;
        }
    }
}

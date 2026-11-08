using System.Diagnostics;
using AudioPilot.Constants;
using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayUnsupportedNavigationTests
{
    [Theory]
    [InlineData(MediaOverlayCommand.NextTrack)]
    [InlineData(MediaOverlayCommand.PreviousTrack)]
    public async Task UnsupportedSinglePlayer_UsesBriefObservationAndAccurateFeedback(MediaOverlayCommand command)
    {
        var fixture = new Scenario();

        MediaOverlayCommandResult result = await fixture.SendAsync(command);

        Assert.Equal(1, fixture.Sends);
        Assert.Equal([AppConstants.MediaOverlay.UnsupportedNavigationObservationMs], fixture.Waits);
        Assert.Equal("No track change detected", result.Overlay.Message);
        Assert.Equal("unsupported-navigation-observation", result.TrackNavigationDiagnostics?.FinalPhase);
        Assert.Equal("no-change-observed", result.TrackNavigationDiagnostics?.Confirmation);
        Assert.False(result.TrackNavigationDiagnostics?.UsedLateTrackLoadRecovery);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    [InlineData(20, false)]
    [InlineData(null, false)]
    public async Task TimelineTick_OnlyOrdinaryForwardProgressKeepsShortObservation(int? delta, bool staysShort)
    {
        var fixture = new Scenario
        {
            ObservationEvent = true,
            ObservationEventKind = MediaEventAssistKind.TimelinePropertiesChanged,
        };
        fixture.OnObservation = () => fixture.Current = fixture.Current with { PositionSeconds = delta.HasValue ? 20 + delta.Value : null };

        MediaOverlayCommandResult result = await fixture.SendAsync(MediaOverlayCommand.NextTrack);

        Assert.Equal(staysShort, result.TrackNavigationDiagnostics?.FinalPhase == "unsupported-navigation-observation");
        Assert.Equal(1, fixture.Sends);
        if (staysShort)
        {
            Assert.InRange(fixture.Waits.Count, 1, 2);
            Assert.All(fixture.Waits, delay => Assert.InRange(delay, 1, AppConstants.MediaOverlay.UnsupportedNavigationObservationMs));
        }
    }

    [Theory]
    [InlineData(nameof(MediaEventAssistKind.MediaPropertiesChanged))]
    [InlineData(nameof(MediaEventAssistKind.PlaybackInfoChanged))]
    [InlineData(nameof(MediaEventAssistKind.CurrentSessionChanged))]
    [InlineData(nameof(MediaEventAssistKind.SessionsChanged))]
    public async Task RelevantEventDuringFinalRead_PreventsShortFeedbackEvenAfterTimelineBurst(string eventKind)
    {
        using var waiter = new MediaOverlayCommandEventWaiter();
        var fixture = new Scenario
        {
            NavigationEventVersionOverride = () => waiter.NavigationChangeVersion,
            OnSessionRead = () =>
            {
                waiter.Signal(new(true, "PlayerFixture", Enum.Parse<MediaEventAssistKind>(eventKind)));
                for (int index = 0; index < 40; index++)
                    waiter.Signal(new(true, "PlayerFixture", MediaEventAssistKind.TimelinePropertiesChanged));
            },
        };

        MediaOverlayCommandResult result = await fixture.SendAsync(MediaOverlayCommand.NextTrack);

        Assert.NotEqual("No track change detected", result.Overlay.Message);
        Assert.NotEqual("unsupported-navigation-observation", result.TrackNavigationDiagnostics?.FinalPhase);
        Assert.Equal(1, fixture.Sends);
        Assert.Equal(1, waiter.NavigationChangeVersion);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("lost-during-read")]
    [InlineData("already-observed")]
    [InlineData("failed")]
    [InlineData("canceled-provider")]
    public async Task UncertainMonitoringOrPriorEvent_KeepsNormalRecovery(string mode)
    {
        var fixture = new Scenario();
        switch (mode)
        {
            case "lost-during-read": fixture.OnSessionRead = () => fixture.NavigationEventVersion = null; break;
            case "already-observed": fixture.NavigationEventVersion = 1; break;
            case "failed": fixture.NavigationEventVersionOverride = () => throw new InvalidOperationException("Monitoring unavailable"); break;
            case "canceled-provider": fixture.NavigationEventVersionOverride = () => throw new OperationCanceledException(); break;
            default: fixture.NavigationEventVersion = null; break;
        }

        MediaOverlayCommandResult result = await fixture.SendAsync(MediaOverlayCommand.PreviousTrack);

        Assert.NotEqual("unsupported-navigation-observation", result.TrackNavigationDiagnostics?.FinalPhase);
        Assert.NotNull(result.TrackNavigationDiagnostics);
        Assert.Equal(1, fixture.Sends);
    }

    [Fact]
    public async Task TimelineEventStorm_BoundsRevalidationWithoutResending()
    {
        var fixture = new Scenario
        {
            WaitOverride = _ => Task.FromResult(new MediaEventAssistOutcome(true, "PlayerFixture", MediaEventAssistKind.TimelinePropertiesChanged)),
        };

        MediaOverlayCommandResult result = await fixture.SendAsync(MediaOverlayCommand.NextTrack);

        Assert.NotNull(result.TrackNavigationDiagnostics);
        Assert.InRange(fixture.Waits.Count(delay => delay > 0), 1, AppConstants.MediaOverlay.UnsupportedNavigationMaxTimelineRechecks);
        Assert.Equal(1, fixture.Sends);
    }

    [Fact]
    public async Task ResumedRecoveryTiming_IncludesPreliminaryObservation()
    {
        var fixture = new Scenario();
        double observationMs = 0;
        fixture.WaitOverride = async token =>
        {
            if (fixture.Waits.Last() == AppConstants.MediaOverlay.UnsupportedNavigationObservationMs)
            {
                long started = Stopwatch.GetTimestamp();
                await Task.Delay(80, token);
                observationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                fixture.Current = fixture.Current with { IsNextEnabled = true };
            }
            return new MediaEventAssistOutcome(false, null);
        };

        MediaOverlayCommandResult result = await fixture.SendAsync(MediaOverlayCommand.NextTrack);

        Assert.NotNull(result.ElapsedMs);
        Assert.NotNull(result.TrackNavigationDiagnostics?.PhaseElapsedMs);
        Assert.True(observationMs > 0);
        Assert.True(result.ElapsedMs.Value - result.TrackNavigationDiagnostics.Value.PhaseElapsedMs.Value >= observationMs);
        TestContext.Current.TestOutputHelper!.WriteLine($"navigation-timing | totalMs={result.ElapsedMs:F1} phaseMs={result.TrackNavigationDiagnostics.Value.PhaseElapsedMs:F1} observationMs={observationMs:F1}");
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("unknown")]
    [InlineData("baseline-unknown")]
    [InlineData("no-hint")]
    [InlineData("wrong-source")]
    [InlineData("not-native-fallback")]
    [InlineData("multiple-sources")]
    [InlineData("send-failure")]
    public async Task SupportedUnknownOrAmbiguousContext_KeepsNormalRecovery(string mode)
    {
        var fixture = new Scenario();
        switch (mode)
        {
            case "supported": fixture.Current = fixture.Current with { IsNextEnabled = true }; break;
            case "unknown": fixture.Current = fixture.Current with { IsNextEnabled = null }; break;
            case "baseline-unknown": fixture.BeforeSend = fixture.Current with { IsNextEnabled = null }; break;
            case "no-hint": fixture.Outcome = fixture.Outcome with { UnsupportedNavigationSourceAppUserModelId = null }; break;
            case "wrong-source": fixture.Outcome = fixture.Outcome with { UnsupportedNavigationSourceAppUserModelId = "OtherPlayer" }; break;
            case "not-native-fallback": fixture.Outcome = fixture.Outcome with { Route = MediaKeyHelper.MediaCommandRouteKind.CurrentGsmc }; break;
            case "multiple-sources": fixture.OtherSessions.Add(fixture.Current with { SourceAppUserModelId = "OtherPlayer" }); break;
            case "send-failure": fixture.Outcome = fixture.Outcome with { Sent = false }; break;
        }

        MediaOverlayCommandResult result = await fixture.SendAsync(MediaOverlayCommand.NextTrack);

        Assert.DoesNotContain(AppConstants.MediaOverlay.UnsupportedNavigationObservationMs, fixture.Waits);
        Assert.NotEqual("unsupported-navigation-observation", result.TrackNavigationDiagnostics?.FinalPhase);
        Assert.Equal(1, fixture.Sends);
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("unknown")]
    [InlineData("metadata")]
    [InlineData("restart")]
    [InlineData("session-drop")]
    [InlineData("source-switch")]
    [InlineData("second-session")]
    [InlineData("same-source-sibling")]
    [InlineData("read-failure")]
    [InlineData("session-appears-during-read")]
    [InlineData("event")]
    public async Task NewEvidenceDuringObservation_ResumesNormalRecoveryWithoutResending(string change)
    {
        var fixture = new Scenario();
        fixture.OnObservation = () =>
        {
            switch (change)
            {
                case "supported": fixture.Current = fixture.Current with { IsPreviousEnabled = true }; break;
                case "unknown": fixture.Current = fixture.Current with { IsPreviousEnabled = null }; break;
                case "metadata": fixture.Current = fixture.Current with { Title = "New track", PositionSeconds = 0 }; break;
                case "restart": fixture.Current = fixture.Current with { PositionSeconds = 0 }; break;
                case "session-drop": fixture.Current = MediaOverlaySessionSnapshot.Empty; break;
                case "source-switch": fixture.Current = fixture.Current with { SourceAppUserModelId = "OtherPlayer" }; break;
                case "second-session": fixture.OtherSessions.Add(fixture.Current with { SourceAppUserModelId = "OtherPlayer" }); break;
                case "same-source-sibling": fixture.OtherSessions.Add(fixture.Current with { Title = "Other tab" }); break;
                case "read-failure": fixture.FailSessionRead = true; break;
                case "session-appears-during-read":
                    fixture.OnSessionRead = () => fixture.OtherSessions.Add(fixture.Current with { SourceAppUserModelId = "OtherPlayer" });
                    break;
            }
        };
        fixture.ObservationEvent = change == "event";

        MediaOverlayCommandResult result = await fixture.SendAsync(MediaOverlayCommand.PreviousTrack);

        Assert.Equal(1, fixture.Sends);
        Assert.Contains(AppConstants.MediaOverlay.UnsupportedNavigationObservationMs, fixture.Waits);
        Assert.NotEqual("unsupported-navigation-observation", result.TrackNavigationDiagnostics?.FinalPhase);
        Assert.NotEqual("No track change detected", result.Overlay.Message);
        if (change is "metadata" or "restart") Assert.True(result.Overlay.IsTrackMessage);
    }

    [Fact]
    public async Task CancellationDuringObservation_HidesFeedbackWithoutResending()
    {
        var fixture = new Scenario();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.WaitOverride = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new MediaEventAssistOutcome(false, null);
        };
        Task<MediaOverlayCommandResult> pending = fixture.SendAsync(MediaOverlayCommand.NextTrack, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        MediaOverlayCommandResult result = await pending;

        Assert.Equal(1, fixture.Sends);
        Assert.Equal(MediaOverlayResultKind.Hidden, result.Overlay.Kind);
        Assert.Equal("media-overlay-canceled", result.DiagCode);
    }

    [Fact]
    public async Task NewerCommandDuringObservation_SuppressesOlderFeedback()
    {
        var fixture = new Scenario();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waits = 0;
        fixture.WaitOverride = async token =>
        {
            if (Interlocked.Increment(ref waits) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return new MediaEventAssistOutcome(false, null);
        };
        Task<MediaOverlayCommandResult> older = fixture.SendAsync(MediaOverlayCommand.NextTrack);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            fixture.Outcome = fixture.Outcome with { UnsupportedNavigationSourceAppUserModelId = null };
            await fixture.SendAsync(MediaOverlayCommand.PreviousTrack);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(MediaOverlayResultKind.Hidden, (await older).Overlay.Kind);
        Assert.Equal(2, fixture.Sends);
    }

    private sealed class Scenario
    {
        private MediaOverlayEngine? _engine;
        private bool _observationDelivered;
        private long _utcNowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public MediaOverlaySessionSnapshot Current { get; set; } = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Live programme", "Presenter", null, "PlayerFixture", 20)
        { IsNextEnabled = false, IsPreviousEnabled = false };
        public MediaOverlaySessionSnapshot? BeforeSend { get; set; }
        public MediaKeyHelper.MediaCommandSendOutcome Outcome { get; set; } = new(
            true, MediaKeyHelper.MediaCommandRouteKind.SendInputFallback,
            UnsupportedNavigationSourceAppUserModelId: "PlayerFixture");
        public List<int> Waits { get; } = [];
        public List<MediaOverlaySessionSnapshot> OtherSessions { get; } = [];
        public int Sends { get; private set; }
        public Action? OnObservation { get; set; }
        public bool ObservationEvent { get; set; }
        public MediaEventAssistKind ObservationEventKind { get; set; }
        public long? NavigationEventVersion { get; set; } = 0;
        public Func<long?>? NavigationEventVersionOverride { get; set; }
        public bool FailSessionRead { get; set; }
        public Action? OnSessionRead { get; set; }
        public Func<CancellationToken, Task<MediaEventAssistOutcome>>? WaitOverride { get; set; }

        public Task<MediaOverlayCommandResult> SendAsync(MediaOverlayCommand command, CancellationToken? token = null)
        {
            _observationDelivered = false;
            MediaOverlaySessionSnapshot Snapshot() => Sends == 0 ? BeforeSend ?? Current : Current;
            List<MediaOverlaySessionSnapshot> Sessions() => [Snapshot(), .. OtherSessions];
            _engine ??= new MediaOverlayEngine(
                currentSnapshotOverride: (_, _, _) => Task.FromResult(Snapshot()),
                snapshotsBySourceOverride: (_, _) => Task.FromResult(Sessions()
                    .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId))
                    .GroupBy(snapshot => snapshot.SourceAppUserModelId!)
                    .ToDictionary(group => group.Key, group => group.First())),
                sessionSnapshotsOverride: (_, _) =>
                {
                    if (FailSessionRead)
                    {
                        FailSessionRead = false;
                        throw new InvalidOperationException("Session read failed");
                    }
                    List<MediaOverlaySessionSnapshot> sessions = Sessions();
                    OnSessionRead?.Invoke();
                    OnSessionRead = null;
                    return Task.FromResult(sessions);
                },
                eventWaitOverride: async (_, delay, _, cancellationToken) =>
                {
                    Waits.Add(delay);
                    Interlocked.Add(ref _utcNowMilliseconds, delay);
                    if (WaitOverride is not null) return await WaitOverride(cancellationToken);
                    if (!_observationDelivered && delay > 0)
                    {
                        _observationDelivered = true;
                        OnObservation?.Invoke();
                        if (ObservationEvent && ObservationEventKind != MediaEventAssistKind.TimelinePropertiesChanged)
                            NavigationEventVersion++;
                        return new MediaEventAssistOutcome(ObservationEvent, "PlayerFixture", ObservationEventKind);
                    }
                    return new MediaEventAssistOutcome(false, null);
                },
                timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile(maxCaptureDurationMs: 30_000),
                utcNow: () => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Increment(ref _utcNowMilliseconds)),
                navigationEventVersionOverride: () => NavigationEventVersionOverride is { } getVersion ? getVersion() : NavigationEventVersion);

            return _engine.SendWithDetailedResultAsync(command,
                () => { Sends++; return Task.FromResult(Outcome.Sent); },
                () => Outcome.CommandTargetSourceAppUserModelId, token ?? TestContext.Current.CancellationToken, () => Outcome);
        }
    }
}

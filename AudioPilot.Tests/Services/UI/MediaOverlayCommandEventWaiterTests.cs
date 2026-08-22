namespace AudioPilot.Tests.Services.UI;

public sealed class MediaOverlayCommandEventWaiterTests
{
    [Fact]
    public async Task WaitAsync_ReturnsAlreadyObservedRelevantEvent()
    {
        using var waiter = new MediaOverlayCommandEventWaiter();
        waiter.Signal(new MediaEventAssistOutcome(
            ObservedEvent: true,
            SignaledSourceAppUserModelId: "spotify",
            MediaEventAssistKind.TimelinePropertiesChanged));

        MediaEventAssistOutcome outcome = await waiter.WaitAsync("spotify", 100, CancellationToken.None);

        Assert.True(outcome.ObservedEvent);
        Assert.Equal("spotify", outcome.SignaledSourceAppUserModelId);
        Assert.Equal(MediaEventAssistKind.TimelinePropertiesChanged, outcome.EventKind);
        Assert.Equal(0, waiter.PendingWaiterCountForTests);
        Assert.Equal(0, waiter.ObservedEventCountForTests);
    }

    [Fact]
    public async Task WaitAsync_IgnoresDifferentSourceUntilRelevantEventArrives()
    {
        using var waiter = new MediaOverlayCommandEventWaiter();

        Task<MediaEventAssistOutcome> waitTask = waiter.WaitAsync("spotify", 5000, CancellationToken.None);
        Assert.Equal(1, waiter.PendingWaiterCountForTests);

        waiter.Signal(new MediaEventAssistOutcome(
            ObservedEvent: true,
            SignaledSourceAppUserModelId: "chrome",
            MediaEventAssistKind.MediaPropertiesChanged));

        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, waiter.PendingWaiterCountForTests);

        waiter.Signal(new MediaEventAssistOutcome(
            ObservedEvent: true,
            SignaledSourceAppUserModelId: "spotify",
            MediaEventAssistKind.PlaybackInfoChanged));

        MediaEventAssistOutcome outcome = await waitTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(outcome.ObservedEvent);
        Assert.Equal("spotify", outcome.SignaledSourceAppUserModelId);
        Assert.Equal(MediaEventAssistKind.PlaybackInfoChanged, outcome.EventKind);
        Assert.Equal(0, waiter.PendingWaiterCountForTests);
    }

    [Fact]
    public async Task WaitAsync_DoesNotReplayEventThatCompletedActiveWaiter()
    {
        using var waiter = new MediaOverlayCommandEventWaiter();

        Task<MediaEventAssistOutcome> firstWait = waiter.WaitAsync("spotify", 500, CancellationToken.None);
        waiter.Signal(new MediaEventAssistOutcome(
            ObservedEvent: true,
            SignaledSourceAppUserModelId: "spotify",
            MediaEventAssistKind.TimelinePropertiesChanged));

        MediaEventAssistOutcome firstOutcome = await firstWait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        MediaEventAssistOutcome secondOutcome = await waiter.WaitAsync("spotify", 10, CancellationToken.None);

        Assert.True(firstOutcome.ObservedEvent);
        Assert.False(secondOutcome.ObservedEvent);
        Assert.Equal(0, waiter.ObservedEventCountForTests);
        Assert.Equal(0, waiter.PendingWaiterCountForTests);
    }

    [Fact]
    public async Task WaitAsync_ReturnsTimeoutOutcomeAndCleansRegistration_WhenNoRelevantEventArrives()
    {
        using var waiter = new MediaOverlayCommandEventWaiter();

        MediaEventAssistOutcome outcome = await waiter.WaitAsync("spotify", 10, CancellationToken.None);

        Assert.False(outcome.ObservedEvent);
        Assert.Null(outcome.SignaledSourceAppUserModelId);
        Assert.Equal(0, waiter.PendingWaiterCountForTests);
    }

    [Fact]
    public async Task WaitAsync_ReturnsCanceledOutcomeAndCleansRegistration_WhenCancellationIsRequested()
    {
        using var waiter = new MediaOverlayCommandEventWaiter();
        using var cts = new CancellationTokenSource();

        Task<MediaEventAssistOutcome> waitTask = waiter.WaitAsync("spotify", 500, cts.Token);
        Assert.Equal(1, waiter.PendingWaiterCountForTests);

        cts.Cancel();
        MediaEventAssistOutcome outcome = await waitTask;

        Assert.False(outcome.ObservedEvent);
        Assert.Equal(0, waiter.PendingWaiterCountForTests);
    }

    [Fact]
    public async Task Dispose_CompletesPendingWaitersAndClearsObservedEvents()
    {
        var waiter = new MediaOverlayCommandEventWaiter();
        waiter.Signal(new MediaEventAssistOutcome(
            ObservedEvent: true,
            SignaledSourceAppUserModelId: "chrome",
            MediaEventAssistKind.MediaPropertiesChanged));

        Task<MediaEventAssistOutcome> waitTask = waiter.WaitAsync("spotify", 500, CancellationToken.None);
        Assert.Equal(1, waiter.PendingWaiterCountForTests);
        Assert.Equal(1, waiter.ObservedEventCountForTests);

        waiter.Dispose();
        MediaEventAssistOutcome outcome = await waitTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.False(outcome.ObservedEvent);
        Assert.Equal(0, waiter.PendingWaiterCountForTests);
        Assert.Equal(0, waiter.ObservedEventCountForTests);
    }
}

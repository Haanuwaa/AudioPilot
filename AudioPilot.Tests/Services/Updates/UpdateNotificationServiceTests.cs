using AudioPilot.Logging;
using AudioPilot.Services.Updates;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Updates;

public sealed class UpdateNotificationServiceTests
{
    [Fact]
    public async Task NotifiedVersionSurvivesRestart_AndOnlyNewerReleasesNotify()
    {
        using var directory = new TestScopedDirectory(nameof(UpdateNotificationServiceTests));
        string path = Path.Combine(directory.Root, "update-notification.json");
        var delivered = new List<Version>();
        Task<bool> Deliver(Version version, CancellationToken _) { delivered.Add(version); return Task.FromResult(true); }
        var first = new UpdateNotificationService(Logger.Instance, Deliver, path);
        Assert.True(await first.NotifyAsync(new Version(2, 0, 0), TestContext.Current.CancellationToken));
        Assert.Equal("\"2.0.0\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        var restarted = new UpdateNotificationService(Logger.Instance, Deliver, path);
        Assert.True(await restarted.NotifyAsync(new Version(2, 0, 0), TestContext.Current.CancellationToken));
        await restarted.NotifyAsync(new Version(1, 9, 0), TestContext.Current.CancellationToken);
        await restarted.NotifyAsync(new Version(2, 1, 0), TestContext.Current.CancellationToken);
        Assert.Equal([new Version(2, 0, 0), new Version(2, 1, 0)], delivered);
        Assert.Equal("\"2.1.0\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("\"1.0.0\"", true)]
    public async Task FailedDeliveryReleasesReservation_AndCanRetryAfterRestart(string? previous, bool throws)
    {
        using var directory = new TestScopedDirectory(nameof(UpdateNotificationServiceTests));
        string path = Path.Combine(directory.Root, "update-notification.json");
        if (previous != null) await File.WriteAllTextAsync(path, previous, TestContext.Current.CancellationToken);
        var failed = new UpdateNotificationService(Logger.Instance, (_, _) => throws
            ? Task.FromException<bool>(new InvalidOperationException("Notification fixture")) : Task.FromResult(false), path);
        Assert.False(await failed.NotifyAsync(new Version(2, 0, 0), TestContext.Current.CancellationToken));
        if (previous == null) Assert.False(File.Exists(path));
        else Assert.Equal(previous, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        int deliveries = 0;
        var restarted = new UpdateNotificationService(Logger.Instance, (_, _) => { deliveries++; return Task.FromResult(true); }, path);
        await restarted.NotifyAsync(new Version(2, 0, 0), TestContext.Current.CancellationToken);
        Assert.Equal(1, deliveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedDeliveryRechecksDisableOrShutdown_AndDoesNotConsumeVersion(bool shutdown)
    {
        using var directory = new TestScopedDirectory(nameof(UpdateNotificationServiceTests));
        string path = Path.Combine(directory.Root, "update-notification.json");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tray = new TestAppTrayIconService { BalloonGate = gate };
        using var shell = new AppShellService(new TestAppMainWindowManager(), tray);
        var service = new UpdateNotificationService(Logger.Instance, shell.NotifyUpdateAvailableAsync, path);
        Task pending = service.NotifyAsync(new Version(2, 0, 0), cancellation.Token);
        Assert.Equal("\"2.0.0\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        if (shutdown) shell.Dispose();
        else cancellation.Cancel();
        gate.SetResult(true);
        await pending;
        Assert.Equal(0, tray.BalloonCalls);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("\"not a version\"")]
    [InlineData("\"{0}\"")]
    public async Task InvalidOrOversizedStateIsReplaced_WithoutBlockingDelivery(string content)
    {
        using var directory = new TestScopedDirectory(nameof(UpdateNotificationServiceTests));
        string path = Path.Combine(directory.Root, "update-notification.json");
        await File.WriteAllTextAsync(path, content.Replace("{0}", new string('x', 8192), StringComparison.Ordinal), TestContext.Current.CancellationToken);
        int deliveries = 0;
        var service = new UpdateNotificationService(Logger.Instance, (_, _) => { deliveries++; return Task.FromResult(true); }, path);
        await service.NotifyAsync(new Version(2, 0, 0), TestContext.Current.CancellationToken);
        Assert.Equal(1, deliveries);
        Assert.Equal("\"2.0.0\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnwritableStateDoesNotSendAnUnrememberedNotification()
    {
        using var directory = new TestScopedDirectory(nameof(UpdateNotificationServiceTests));
        int deliveries = 0;
        var service = new UpdateNotificationService(Logger.Instance, (_, _) => { deliveries++; return Task.FromResult(true); }, directory.Root);
        Assert.False(await service.NotifyAsync(new Version(2, 0, 0), TestContext.Current.CancellationToken));
        Assert.Equal(0, deliveries);
        Assert.True(Directory.Exists(directory.Root));
    }
}

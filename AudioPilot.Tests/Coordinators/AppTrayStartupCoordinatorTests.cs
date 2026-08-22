using AudioPilot.Coordinators;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppTrayStartupCoordinatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task RegistrationStopsImmediatelyAfterSuccess(int failures)
    {
        using var scope = TestLoggerScope.CreateInMemory("tray-startup");
        TimeSpan elapsed = TimeSpan.Zero;
        int attempts = 0;
        bool ready = await AppTrayStartupCoordinator.EnsureVisibleAsync(
            () => attempts++ >= failures, scope.Logger, CancellationToken.None,
            (duration, _) => { elapsed += duration; return Task.CompletedTask; }, () => elapsed);

        Assert.True(ready);
        Assert.Equal(failures + 1, attempts);
        Assert.Equal(TimeSpan.FromMilliseconds(failures * 250), elapsed);
    }

    [Fact]
    public async Task UnavailableTrayStopsAtDeadlineAndAllowsFallback()
    {
        using var scope = TestLoggerScope.CreateInMemory("tray-timeout");
        TimeSpan elapsed = TimeSpan.Zero;
        int attempts = 0;
        bool ready = await AppTrayStartupCoordinator.EnsureVisibleAsync(
            () => { attempts++; return false; }, scope.Logger, CancellationToken.None,
            (duration, _) => { elapsed += duration; return Task.CompletedTask; }, () => elapsed);

        Assert.False(ready);
        Assert.Equal(TimeSpan.FromSeconds(10), elapsed);
        Assert.Equal(41, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShutdownPreventsFurtherRegistration(bool alreadyCancelled)
    {
        using var scope = TestLoggerScope.CreateInMemory("tray-cancel");
        using var cancellation = new CancellationTokenSource();
        if (alreadyCancelled) cancellation.Cancel();
        int attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppTrayStartupCoordinator.EnsureVisibleAsync(
            () => { attempts++; return false; }, scope.Logger, cancellation.Token,
            (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, () => TimeSpan.Zero));

        Assert.Equal(alreadyCancelled ? 0 : 1, attempts);
    }
}

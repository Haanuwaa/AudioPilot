using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceResumeRecoveryHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoversMissingNotifications_AndRetriesOnlyIncompleteMonitoring(bool alreadyRegistered)
    {
        bool registered = alreadyRegistered;
        int registrations = 0;
        int monitoringAttempts = 0;
        var helper = new AudioDeviceResumeRecoveryHelper(Logger.Instance, () => false, () => registered,
            () => { registrations++; registered = true; },
            _ => Task.FromResult(++monitoringAttempts == 2), retryDelayMs: 0);

        Assert.True(await helper.RecoverCallbacksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(alreadyRegistered ? 0 : 1, registrations);
        Assert.Equal(2, monitoringAttempts);
    }

    [Fact]
    public async Task MissingRegistrationAfterRetryIsIncomplete_EvenWithHealthyMonitoring()
    {
        int registrations = 0;
        int monitoringAttempts = 0;
        var helper = new AudioDeviceResumeRecoveryHelper(Logger.Instance, () => false, () => false,
            () => registrations++, _ => { monitoringAttempts++; return Task.FromResult(true); }, retryDelayMs: 0);

        Assert.False(await helper.RecoverCallbacksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, registrations);
        Assert.Equal(1, monitoringAttempts);
    }

    [Fact]
    public async Task CancellationDuringRegistrationSkipsMonitoring()
    {
        using var wake = new CancellationTokenSource();
        int monitoringAttempts = 0;
        var helper = new AudioDeviceResumeRecoveryHelper(Logger.Instance, () => false, () => false,
            wake.Cancel, _ => { monitoringAttempts++; return Task.FromResult(true); }, retryDelayMs: 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => helper.RecoverCallbacksAsync(wake.Token));
        Assert.Equal(0, monitoringAttempts);
    }

    [Fact]
    public async Task FailedMonitoringAttemptsReportIncompleteRecovery()
    {
        int monitoringAttempts = 0;
        var helper = new AudioDeviceResumeRecoveryHelper(Logger.Instance, () => false, () => true,
            () => { }, _ => { monitoringAttempts++; throw new InvalidOperationException("Audio is unavailable."); }, retryDelayMs: 0);

        Assert.False(await helper.RecoverCallbacksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, monitoringAttempts);
    }
}

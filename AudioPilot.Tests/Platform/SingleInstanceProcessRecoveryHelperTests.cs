using AudioPilot.Logging;

namespace AudioPilot.Tests.Platform;

public sealed class SingleInstanceProcessRecoveryHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_DoesNotTouchAnotherUserOrSession(bool differentUser)
    {
        var current = new SingleInstanceScope(1, "S-1-5-21-100");
        var other = differentUser ? new SingleInstanceScope(1, "S-1-5-21-200") : current with { SessionId = 2 };
        var helper = new SingleInstanceProcessRecoveryHelper(Logger.Instance,
            enumerateProcesses: () => [new(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", true, other)],
            getCurrentScope: () => current,
            getCurrentExecutablePath: () => @"C:\Apps\AudioPilot.exe",
            tryCloseMainWindow: _ => throw new InvalidOperationException("Must not close another session"),
            killProcess: _ => throw new InvalidOperationException("Must not kill another session"));
        Assert.Equal("no-matching-process", helper.TryTerminateMatchingExistingProcess().FailureReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_RevalidatesScopeBeforeCloseAndKill(bool changesAfterClose)
    {
        var current = new SingleInstanceScope(1, "S-1-5-21-100");
        int reads = 0;
        int closes = 0;
        bool killed = false;
        var helper = new SingleInstanceProcessRecoveryHelper(Logger.Instance,
            enumerateProcesses: () => [new(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", true, current)],
            getCurrentExecutablePath: () => @"C:\Apps\AudioPilot.exe",
            getCurrentScope: () => current,
            getProcessIdentity: _ => new(true, @"C:\Apps\AudioPilot.exe",
                changesAfterClose && reads++ == 0 ? current : current with { SessionId = 2 }),
            tryCloseMainWindow: _ => { closes++; return true; },
            waitForExit: (_, _) => false,
            killProcess: _ => killed = true);
        Assert.Equal("process-identity-changed", helper.TryTerminateMatchingExistingProcess().FailureReason);
        Assert.Equal(changesAfterClose ? 1 : 0, closes);
        Assert.False(killed);
    }

    [Fact]
    public void TryTerminateMatchingExistingProcess_FailsWhenCurrentExecutableUnavailable()
    {
        var helper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () => [],
            getCurrentExecutablePath: static () => null);

        SingleInstanceProcessRecoveryResult result = helper.TryTerminateMatchingExistingProcess();

        Assert.False(result.Success);
        Assert.Equal("current-executable-unavailable", result.FailureReason);
    }

    [Fact]
    public void TryTerminateMatchingExistingProcess_DoesNotTerminateUnmatchedProcesses()
    {
        List<int> killed = [];
        var helper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () =>
            [
                new SingleInstanceProcessInfo(11, "AudioPilot", @"C:\Apps\OtherApp.exe", HasMainWindow: true, Scope: SingleInstanceScope.Current),
            ],
            getCurrentProcessId: static () => 77,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            killProcess: processId => killed.Add(processId));

        SingleInstanceProcessRecoveryResult result = helper.TryTerminateMatchingExistingProcess();

        Assert.False(result.Success);
        Assert.Equal("no-matching-process", result.FailureReason);
        Assert.Empty(killed);
    }

    [Fact]
    public void TryTerminateMatchingExistingProcess_ClosesMatchingProcessGracefully()
    {
        List<int> closed = [];
        List<int> killed = [];
        var helper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () =>
            [
                new SingleInstanceProcessInfo(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", HasMainWindow: true, Scope: SingleInstanceScope.Current),
            ],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            getProcessIdentity: static _ => new SingleInstanceProcessIdentity(true, @"C:\Apps\AudioPilot.exe", SingleInstanceScope.Current),
            tryCloseMainWindow: processId =>
            {
                closed.Add(processId);
                return true;
            },
            waitForExit: static (_, _) => true,
            killProcess: processId => killed.Add(processId));

        SingleInstanceProcessRecoveryResult result = helper.TryTerminateMatchingExistingProcess();

        Assert.True(result.Success);
        Assert.Equal(1, result.MatchedProcessCount);
        Assert.Equal([42], closed);
        Assert.Empty(killed);
    }

    [Fact]
    public void TryTerminateMatchingExistingProcess_FallsBackToKillWhenGracefulCloseDoesNotExit()
    {
        List<int> closed = [];
        List<int> killed = [];
        Dictionary<int, int> waitCounts = [];

        var helper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () =>
            [
                new SingleInstanceProcessInfo(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", HasMainWindow: true, Scope: SingleInstanceScope.Current),
            ],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            getProcessIdentity: static _ => new SingleInstanceProcessIdentity(true, @"C:\Apps\AudioPilot.exe", SingleInstanceScope.Current),
            tryCloseMainWindow: processId =>
            {
                closed.Add(processId);
                return true;
            },
            waitForExit: (processId, _) =>
            {
                waitCounts.TryGetValue(processId, out int count);
                waitCounts[processId] = count + 1;
                return count > 0;
            },
            killProcess: processId => killed.Add(processId));

        SingleInstanceProcessRecoveryResult result = helper.TryTerminateMatchingExistingProcess();

        Assert.True(result.Success);
        Assert.Equal([42], closed);
        Assert.Equal([42], killed);
    }

    [Fact]
    public void TryTerminateMatchingExistingProcess_DoesNotTouchProcessWhoseIdentityChanged()
    {
        List<int> closed = [];
        List<int> killed = [];
        var helper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () =>
            [
                new SingleInstanceProcessInfo(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", HasMainWindow: true, Scope: SingleInstanceScope.Current),
            ],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            getProcessIdentity: static _ => new SingleInstanceProcessIdentity(true, @"C:\Apps\Replacement.exe", SingleInstanceScope.Current),
            tryCloseMainWindow: processId =>
            {
                closed.Add(processId);
                return true;
            },
            killProcess: processId => killed.Add(processId));

        SingleInstanceProcessRecoveryResult result = helper.TryTerminateMatchingExistingProcess();

        Assert.False(result.Success);
        Assert.Equal("process-identity-changed", result.FailureReason);
        Assert.Empty(closed);
        Assert.Empty(killed);
    }

    [Fact]
    public void TryTerminateMatchingExistingProcess_RevalidatesIdentityBeforeForceKill()
    {
        List<int> closed = [];
        List<int> killed = [];
        Queue<SingleInstanceProcessIdentity> identities = new(
        [
            new SingleInstanceProcessIdentity(true, @"C:\Apps\AudioPilot.exe", SingleInstanceScope.Current),
            new SingleInstanceProcessIdentity(true, @"C:\Apps\Replacement.exe", SingleInstanceScope.Current),
        ]);
        var helper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () =>
            [
                new SingleInstanceProcessInfo(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", HasMainWindow: true, Scope: SingleInstanceScope.Current),
            ],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            getProcessIdentity: _ => identities.Dequeue(),
            tryCloseMainWindow: processId =>
            {
                closed.Add(processId);
                return true;
            },
            waitForExit: static (_, _) => false,
            killProcess: processId => killed.Add(processId));

        SingleInstanceProcessRecoveryResult result = helper.TryTerminateMatchingExistingProcess();

        Assert.False(result.Success);
        Assert.Equal("process-identity-changed", result.FailureReason);
        Assert.Equal([42], closed);
        Assert.Empty(killed);
    }

    [Fact]
    public void TryTerminateMatchingExistingProcess_TreatsAlreadyExitedMatchAsRecovered()
    {
        List<int> killed = [];
        var helper = new SingleInstanceProcessRecoveryHelper(
            Logger.Instance,
            enumerateProcesses: static () =>
            [
                new SingleInstanceProcessInfo(42, "AudioPilot", @"C:\Apps\AudioPilot.exe", HasMainWindow: false, Scope: SingleInstanceScope.Current),
            ],
            getCurrentProcessId: static () => 99,
            getCurrentExecutablePath: static () => @"C:\Apps\AudioPilot.exe",
            getProcessIdentity: static _ => new SingleInstanceProcessIdentity(false, null),
            killProcess: processId => killed.Add(processId));

        SingleInstanceProcessRecoveryResult result = helper.TryTerminateMatchingExistingProcess();

        Assert.True(result.Success);
        Assert.Equal(1, result.MatchedProcessCount);
        Assert.Empty(killed);
    }
}

using System.IO.Pipes;
using System.Text;

namespace AudioPilot.Tests.Helpers;

public sealed class SingleInstanceContentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogonLaunchDoesNotActivateTheOwnerAndIdentifiesHeadlessHandoffs(bool headless)
    {
        string name = $"AudioPilot.Tests.Logon.{Guid.NewGuid():N}";
        await using var owner = new SingleInstanceHelper(name, $"{name}.Activation", isHeadless: headless);
        int activations = 0;
        owner.ActivationRequested += (_, _) => Interlocked.Increment(ref activations);
        owner.OwnerStateRequested += _ => Task.FromResult(SingleInstanceOwnerState.Unresponsive);
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;
        await using var client = new SingleInstanceHelper(name, $"{name}.Activation");

        SingleInstanceAcquireResult result = client.TryAcquireForLogon();

        Assert.True(result.ExistingHealthy);
        Assert.Equal(headless, result.ResponseErrorCode == SingleInstanceHelper.HeadlessBusyErrorCode);
        Assert.Equal(0, Volatile.Read(ref activations));
    }

    [Theory]
    [InlineData((int)SingleInstanceOwnerState.Responsive)]
    [InlineData((int)SingleInstanceOwnerState.Starting)]
    [InlineData((int)SingleInstanceOwnerState.Stopping)]
    [InlineData((int)SingleInstanceOwnerState.Unresponsive)]
    [InlineData((int)SingleInstanceOwnerState.Headless)]
    public async Task OwnerProbeSeparatesUiStateFromActivationAndUsesThePipeProcessIdentity(int ownerState)
    {
        string name = $"AudioPilot.Tests.Owner.{Guid.NewGuid():N}";
        var state = (SingleInstanceOwnerState)ownerState;
        await using var owner = new SingleInstanceHelper(name, $"{name}.Activation", isHeadless: state == SingleInstanceOwnerState.Headless);
        owner.OwnerStateRequested += _ => Task.FromResult(state);
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;
        using var client = new SingleInstanceHelper(name, $"{name}.Activation");

        SingleInstanceOwnerProbe probe = await client.ProbeExistingOwnerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(state, probe.State);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(new SingleInstanceOwnerIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks), probe.Identity);
    }

    [Fact]
    public async Task OwnershipRecheckDoesNotForwardActivationAndCanAcquireAfterOwnerExits()
    {
        string name = $"AudioPilot.Tests.Ownership.{Guid.NewGuid():N}";
        await using var owner = new SingleInstanceHelper(name, $"{name}.Activation");
        int activations = 0;
        owner.ActivationRequested += (_, _) => Interlocked.Increment(ref activations);
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;
        await using var client = new SingleInstanceHelper(name, $"{name}.Activation");
        Assert.False(client.TryAcquireDetailed(showUserErrors: false, signalExistingInstance: false).Acquired);
        // An explicit probe roundtrip drains the listener without sending an activation.
        await client.ProbeExistingOwnerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref activations));
        await owner.DisposeAsync();
        Assert.True(client.TryAcquireDetailed(showUserErrors: false, signalExistingInstance: false).Acquired);
    }

    [Fact]
    public async Task ScopedTransports_KeepSessionResponsesSeparateForTheSameApplication()
    {
        string application = $"AudioPilot.Tests.{Guid.NewGuid():N}";
        string firstName = new SingleInstanceScope(1, "S-1-5-21-100").Qualify(application);
        string secondName = new SingleInstanceScope(2, "S-1-5-21-100").Qualify(application);
        await using var first = new SingleInstanceHelper(firstName, $"{firstName}.Activation");
        await using var second = new SingleInstanceHelper(secondName, $"{secondName}.Activation");
        first.CommandRequested += (_, _) => Task.FromResult(new SingleInstanceCommandResult(0, "first-session"));
        second.CommandRequested += (_, _) => Task.FromResult(new SingleInstanceCommandResult(0, "second-session"));
        Assert.True(first.TryAcquire());
        Assert.True(second.TryAcquire());
        await Task.WhenAll(first.ActivationListenerReadyForTests, second.ActivationListenerReadyForTests);
        using var firstClient = new SingleInstanceHelper(firstName, $"{firstName}.Activation");
        using var secondClient = new SingleInstanceHelper(secondName, $"{secondName}.Activation");
        Assert.True(firstClient.TryAcquireDetailed("which-session", showUserErrors: false).ExistingHealthy);
        Assert.True(secondClient.TryAcquireDetailed("which-session", showUserErrors: false).ExistingHealthy);
        Assert.Equal("first-session", firstClient.LastSignalOutput);
        Assert.Equal("second-session", secondClient.LastSignalOutput);
    }

    [Fact]
    public async Task CommandForwarding_PreservesResponsesLargerThan64KiB()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";
        string output = new string('x', 96 * 1024) + "\nCafé \"quoted\" \U0001F3B5";
        await using var owner = new SingleInstanceHelper(mutexName, pipeName);
        owner.CommandRequested += (_, _) => Task.FromResult(new SingleInstanceCommandResult(0, output));
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;
        using var client = new SingleInstanceHelper(mutexName, pipeName);

        Assert.False(client.TryAcquire("large-response"));

        Assert.True(client.LastSignalExistingSucceeded);
        Assert.Equal(output, client.LastSignalOutput);
    }

    [Fact]
    public async Task ServerThatDoesNotRead_RequestWriteTimesOut()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";
        using var mutex = new Mutex(false, mutexName);
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 1024, outBufferSize: 1024);
        using var clock = new ManualTimeProvider();
        using var client = new SingleInstanceHelper(mutexName, pipeName, responseTimeoutMs: 100, timeProvider: clock);
        Task<SingleInstanceAcquireResult> forwarding = Task.Run(
            () => client.TryAcquireDetailed(new string('x', 32 * 1024), showUserErrors: false),
            TestContext.Current.CancellationToken);
        try
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            byte[] header = new byte[sizeof(int)];
            await server.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
            await clock.WaitForTimerAsync();
            clock.Advance(TimeSpan.FromMilliseconds(100));

            SingleInstanceAcquireResult result = await forwarding.WaitAsync(
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.True(result.ExistingUnresponsive);
            Assert.Equal(SingleInstanceSignalFailureKind.OutcomeUnknown, result.FailureKind);
        }
        finally
        {
            server.Dispose();
            await forwarding.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LongCommand_AllowsActivationWhileOtherCommandsRemainSerialized()
    {
        string scope = Guid.NewGuid().ToString("N");
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";
        await using var owner = new SingleInstanceHelper($"AudioPilot.Tests.Mutex.{scope}", pipeName);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.ActivationRequested += static (_, _) => { };
        owner.CommandRequested += async (payload, cancellationToken) =>
        {
            if (payload == "first")
            {
                firstStarted.TrySetResult();
                await release.Task;
            }
            else
            {
                secondStarted.TrySetResult();
            }
            return new SingleInstanceCommandResult(0, payload);
        };
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;

        await using var first = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var second = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var activation = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await SendAsync(first, "first");
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await SendAsync(second, "second");
            await SendAsync(activation, "ACTIVATE");

            SingleInstanceCommandResult response = await ReceiveAsync(activation);
            Assert.Equal(0, response.ExitCode);
            Assert.False(secondStarted.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal("first", (await ReceiveAsync(first)).Output);
        Assert.Equal("second", (await ReceiveAsync(second)).Output);
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ClientThatDoesNotRead_ResponseTimesOutAndListenerRemainsAvailable()
    {
        string scope = Guid.NewGuid().ToString("N");
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";
        using var clock = new ManualTimeProvider();
        await using var owner = new SingleInstanceHelper($"AudioPilot.Tests.Mutex.{scope}", pipeName, responseWriteTimeoutMs: 100, timeProvider: clock);
        owner.CommandRequested += static (_, _) => Task.FromResult(new SingleInstanceCommandResult(0, new string('x', 256 * 1024)));
        owner.ActivationRequested += static (_, _) => { };
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;

        await using var stalled = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await SendAsync(stalled, "large-response");
        // The request read, command execution, and response write deadlines.
        for (int i = 0; i < 3; i++) await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await owner.ResponseWriteTimedOutForTests.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await using var activation = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await SendAsync(activation, "ACTIVATE");
        Assert.Equal(0, (await ReceiveAsync(activation)).ExitCode);
    }

    [Fact]
    public async Task DeferredActivationListener_StartsOnlyAfterEnsureActivationListenerStarted()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";
        int activations = 0;
        await using var owner = new SingleInstanceHelper(mutexName, pipeName, deferActivationListener: true);
        Assert.True(owner.TryAcquire());

        using (var earlyClient = new SingleInstanceHelper(mutexName, pipeName))
        {
            SingleInstanceAcquireResult earlyResult = earlyClient.TryAcquireDetailed(showUserErrors: false);
            Assert.True(earlyResult.ExistingUnresponsive);
            Assert.Equal(SingleInstanceSignalFailureKind.ConnectionFailed, earlyResult.FailureKind);
        }

        owner.ActivationRequested += (_, _) => Interlocked.Increment(ref activations);
        owner.EnsureActivationListenerStarted();
        await owner.ActivationListenerReadyForTests.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var readyClient = new SingleInstanceHelper(mutexName, pipeName);
        SingleInstanceAcquireResult readyResult = readyClient.TryAcquireDetailed(showUserErrors: false);
        Assert.True(readyResult.ExistingHealthy);
        Assert.Equal(0, readyResult.ResponseExitCode);
        Assert.Equal(1, Volatile.Read(ref activations));
    }

    [Fact]
    public async Task ActivationTimeout_BoundsHungActivationWithoutWaitingForCliCommandBudget()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";
        using var clock = new ManualTimeProvider();
        await using var owner = new SingleInstanceHelper(
            mutexName, pipeName, responseTimeoutMs: 30000, activationTimeoutMs: 150, timeProvider: clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.ActivationRequestedAsync += async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        };
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;

        using var client = new SingleInstanceHelper(
            mutexName, pipeName, responseTimeoutMs: 30000, activationTimeoutMs: 250, timeProvider: clock);
        Task<SingleInstanceAcquireResult> pending = Task.Run(
            () => client.TryAcquireDetailed(showUserErrors: false),
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(150));

        SingleInstanceAcquireResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.ExistingHealthy);
        Assert.Equal("activation-expired", result.ResponseErrorCode);
    }

    private static async Task SendAsync(NamedPipeClientStream client, string payload)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(timeout.Token);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await client.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token);
        await client.WriteAsync(bytes, timeout.Token);
        await client.FlushAsync(timeout.Token);
    }

    private static async Task<SingleInstanceCommandResult> ReceiveAsync(Stream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        byte[] length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length, timeout.Token);
        byte[] response = new byte[BitConverter.ToInt32(length)];
        await stream.ReadExactlyAsync(response, timeout.Token);
        Assert.True(SingleInstanceCommandResultParser.TryParse(response, out var parsed));
        return parsed;
    }
}

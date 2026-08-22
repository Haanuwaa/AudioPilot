using System.IO.Pipes;
using System.Text;
using AudioPilot.Cli;

namespace AudioPilot.Tests.Helpers;

public sealed class SingleInstanceCommandLifetimeTests
{
    [Theory]
    [InlineData(0, 30000)]
    [InlineData(60000, 90000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ExplicitDeviceWaitExtendsTransportBudgetWithoutOverflow(int waitMs, int expected)
    {
        var command = new CliCommand { Action = CliAction.WaitForDevice, Key = "missing", Value = waitMs.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Equal(expected, SingleInstanceHelper.ResolveCommandTimeoutMs(command.ToPipePayload(), 30000));
        Assert.Equal(30000, SingleInstanceHelper.ResolveCommandTimeoutMs("ACTIVATE", 30000));
    }

    [Fact]
    public async Task ExpiredCommand_RetainsSerializationUntilItDrains_AndQueuedCommandNeverRuns()
    {
        string scope = Guid.NewGuid().ToString("N");
        string pipe = $"AudioPilot.Tests.Pipe.{scope}";
        await using var owner = new SingleInstanceHelper($"AudioPilot.Tests.Mutex.{scope}", pipe, commandTimeoutMs: 200);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int executions = 0;
        owner.ActivationRequested += static (_, _) => { };
        owner.CommandRequested += async (payload, _) =>
        {
            Interlocked.Increment(ref executions);
            if (payload == "blocked")
            {
                started.TrySetResult();
                await release.Task;
            }
            return new(0, payload);
        };
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;
        try
        {
            await using var first = await SendAsync(pipe, "blocked");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("forwarded-command-outcome-unknown", (await ReadAsync(first)).ErrorCode);

            await using var queued = await SendAsync(pipe, "must-not-run");
            Assert.Equal("forwarded-command-expired", (await ReadAsync(queued)).ErrorCode);
            Assert.Equal(1, Volatile.Read(ref executions));

            await using var activation = await SendAsync(pipe, "ACTIVATE");
            Assert.Equal(0, (await ReadAsync(activation)).ExitCode);

            release.TrySetResult();
            await using var next = await SendAsync(pipe, "next");
            Assert.Equal("next", (await ReadAsync(next)).Output);
            Assert.Equal(2, Volatile.Read(ref executions));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrShutdown_CancelsAcceptedCooperativeWork(bool shutdown)
    {
        string scope = Guid.NewGuid().ToString("N");
        string pipe = $"AudioPilot.Tests.Pipe.{scope}";
        await using var owner = new SingleInstanceHelper($"AudioPilot.Tests.Mutex.{scope}", pipe);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.CommandRequested += async (payload, token) =>
        {
            if (payload != "wait") return new(0, payload);
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                canceled.TrySetResult();
            }
            return new(0);
        };
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;
        await using var client = await SendAsync(pipe, "wait");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (shutdown) owner.BeginShutdown();
        else await client.DisposeAsync();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (!shutdown)
        {
            await using var next = await SendAsync(pipe, "next");
            Assert.Equal("next", (await ReadAsync(next)).Output);
        }
    }

    private static async Task<NamedPipeClientStream> SendAsync(string pipe, string payload)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await client.WriteAsync(BitConverter.GetBytes(bytes.Length), TestContext.Current.CancellationToken);
            await client.WriteAsync(bytes, TestContext.Current.CancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<SingleInstanceCommandResult> ReadAsync(NamedPipeClientStream client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await client.ReadExactlyAsync(header, deadline.Token);
        int length = BitConverter.ToInt32(header);
        Assert.InRange(length, 1, 8192);
        byte[] body = new byte[length];
        await client.ReadExactlyAsync(body, deadline.Token);
        Assert.True(SingleInstanceCommandResultParser.TryParse(Encoding.UTF8.GetString(body), out var parsed));
        return parsed.Response;
    }
}

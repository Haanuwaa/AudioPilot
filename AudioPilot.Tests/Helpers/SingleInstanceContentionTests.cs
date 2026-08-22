using System.IO.Pipes;
using System.Text;

namespace AudioPilot.Tests.Helpers;

public sealed class SingleInstanceContentionTests
{
    [Fact]
    public async Task CommandForwarding_PreservesResponsesLargerThan64KiB()
    {
        string scope = Guid.NewGuid().ToString("N");
        string mutexName = $"AudioPilot.Tests.Mutex.{scope}";
        string pipeName = $"AudioPilot.Tests.Pipe.{scope}";
        string output = new('x', 96 * 1024);
        await using var owner = new SingleInstanceHelper(mutexName, pipeName);
        owner.CommandRequested += _ => Task.FromResult(new SingleInstanceCommandResult(0, output));
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
        using var client = new SingleInstanceHelper(mutexName, pipeName, responseTimeoutMs: 100);
        Task<SingleInstanceAcquireResult> forwarding = Task.Run(
            () => client.TryAcquireDetailed(new string('x', 32 * 1024), showUserErrors: false),
            TestContext.Current.CancellationToken);
        try
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            byte[] header = new byte[sizeof(int)];
            await server.ReadExactlyAsync(header, TestContext.Current.CancellationToken);

            SingleInstanceAcquireResult result = await forwarding.WaitAsync(
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.True(result.ExistingUnresponsive);
            Assert.Equal(SingleInstanceSignalFailureKind.ConnectionFailed, result.FailureKind);
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
        owner.CommandRequested += async payload =>
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
        await using var owner = new SingleInstanceHelper($"AudioPilot.Tests.Mutex.{scope}", pipeName, responseWriteTimeoutMs: 100);
        owner.CommandRequested += static _ => Task.FromResult(new SingleInstanceCommandResult(0, new string('x', 256 * 1024)));
        owner.ActivationRequested += static (_, _) => { };
        Assert.True(owner.TryAcquire());
        await owner.ActivationListenerReadyForTests;

        await using var stalled = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await SendAsync(stalled, "large-response");
        await owner.ResponseWriteTimedOutForTests.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await using var activation = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await SendAsync(activation, "ACTIVATE");
        Assert.Equal(0, (await ReceiveAsync(activation)).ExitCode);
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
        Assert.True(SingleInstanceCommandResultParser.TryParse(Encoding.UTF8.GetString(response), out var parsed));
        return parsed.Response;
    }
}

using AudioPilot.Coordinators;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeResumeRecoveryHandler : IResumeRecoveryHandler
{
    private readonly TaskCompletionSource<string> _invocationSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _blockEntrySource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? ExceptionToThrow { get; set; }
    public bool BlockUntilReleased { get; set; }
    public int InvocationCount { get; private set; }

    public async Task RecoverAfterSystemResumeAsync(string? resumeOpId = null)
    {
        try
        {
            InvocationCount++;
            _invocationSource.TrySetResult(resumeOpId ?? string.Empty);

            if (BlockUntilReleased)
            {
                _blockEntrySource.TrySetResult();
                await _releaseSource.Task;
            }

            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            await Task.CompletedTask;
        }
        finally
        {
            _completionSource.TrySetResult();
        }
    }

    public Task<string> WaitForInvocationAsync()
    {
        return _invocationSource.Task;
    }

    public Task WaitForBlockEntryAsync()
    {
        return _blockEntrySource.Task;
    }

    public Task WaitForCompletionAsync()
    {
        return _completionSource.Task;
    }

    public void Release()
    {
        _releaseSource.TrySetResult();
    }
}

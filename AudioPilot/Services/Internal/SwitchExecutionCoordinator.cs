namespace AudioPilot.Services.Internal
{
    internal sealed class SwitchExecutionCoordinator : IDisposable
    {
        private readonly SemaphoreSlim _outputSwitchSemaphore = new(1, 1);
        private readonly SemaphoreSlim _inputSwitchSemaphore = new(1, 1);

        /// <summary>
        /// Attempts to enter the single-flight output switch gate without waiting so callers can reject overlapping
        /// output switch requests deterministically.
        /// </summary>
        public Task<bool> TryEnterOutputAsync(CancellationToken cancellationToken = default)
            => TryEnterAsync(_outputSwitchSemaphore, cancellationToken);

        /// <summary>
        /// Attempts to enter the single-flight input switch gate without waiting so callers can reject overlapping
        /// input switch requests deterministically.
        /// </summary>
        public Task<bool> TryEnterInputAsync(CancellationToken cancellationToken = default)
            => TryEnterAsync(_inputSwitchSemaphore, cancellationToken);

        private static async Task<bool> TryEnterAsync(SemaphoreSlim gate, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled) return await gate.WaitAsync(0, cancellationToken);
            try
            {
                await gate.WaitAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        public void ReleaseOutput()
        {
            _outputSwitchSemaphore.Release();
        }

        public void ReleaseInput()
        {
            _inputSwitchSemaphore.Release();
        }

        public bool TryEnterOutputForTests() => _outputSwitchSemaphore.Wait(0);
        public void ExitOutputForTests() => _outputSwitchSemaphore.Release();
        public bool TryEnterInputForTests() => _inputSwitchSemaphore.Wait(0);
        public void ExitInputForTests() => _inputSwitchSemaphore.Release();

        public void Dispose()
        {
            _outputSwitchSemaphore.Dispose();
            _inputSwitchSemaphore.Dispose();
        }
    }
}

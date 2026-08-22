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
        public Task<bool> TryEnterOutputAsync()
        {
            return _outputSwitchSemaphore.WaitAsync(0);
        }

        /// <summary>
        /// Attempts to enter the single-flight input switch gate without waiting so callers can reject overlapping
        /// input switch requests deterministically.
        /// </summary>
        public Task<bool> TryEnterInputAsync()
        {
            return _inputSwitchSemaphore.WaitAsync(0);
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

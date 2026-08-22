namespace AudioPilot.Services.Internal
{
    internal sealed class SwitchExecutionCoordinator : IDisposable
    {
        private readonly SemaphoreSlim _outputSwitchSemaphore = new(1, 1);
        private readonly SemaphoreSlim _inputSwitchSemaphore = new(1, 1);

        private DateTime _lastOutputSwitchTime;
        private DateTime _lastInputSwitchTime;

        public DateTime LastOutputSwitchTime => _lastOutputSwitchTime;
        public DateTime LastInputSwitchTime => _lastInputSwitchTime;

        /// <summary>
        /// Returns whether a new output switch falls within the configured debounce window since the last successful
        /// output switch.
        /// </summary>
        public static bool IsOutputSwitchDebounced(DateTime now, DateTime lastOutputSwitchTime)
        {
            return (now - lastOutputSwitchTime).TotalMilliseconds < RuntimeTuningConfig.OutputSwitchDebounceMs;
        }

        /// <summary>
        /// Returns whether a new input switch falls within the configured debounce window since the last successful
        /// input switch.
        /// </summary>
        public static bool IsInputSwitchDebounced(DateTime now, DateTime lastInputSwitchTime)
        {
            return (now - lastInputSwitchTime).TotalMilliseconds < RuntimeTuningConfig.InputSwitchDebounceMs;
        }

        public bool IsOutputDebounced(DateTime now)
        {
            return IsOutputSwitchDebounced(now, _lastOutputSwitchTime);
        }

        public bool IsInputDebounced(DateTime now)
        {
            return IsInputSwitchDebounced(now, _lastInputSwitchTime);
        }

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

        /// <summary>
        /// Records the timestamp of a successful output switch so the next output debounce decision uses the actual
        /// success time rather than the request start time.
        /// </summary>
        public void MarkOutputSwitchSuccess(DateTime timestamp)
        {
            _lastOutputSwitchTime = timestamp;
        }

        /// <summary>
        /// Records the timestamp of a successful input switch so the next input debounce decision uses the actual
        /// success time rather than the request start time.
        /// </summary>
        public void MarkInputSwitchSuccess(DateTime timestamp)
        {
            _lastInputSwitchTime = timestamp;
        }

        public bool TryEnterOutputForTests() => _outputSwitchSemaphore.Wait(0);
        public void ExitOutputForTests() => _outputSwitchSemaphore.Release();
        public bool TryEnterInputForTests() => _inputSwitchSemaphore.Wait(0);
        public void ExitInputForTests() => _inputSwitchSemaphore.Release();

        public void SetLastSwitchTimes(DateTime? outputLast, DateTime? inputLast)
        {
            if (outputLast.HasValue)
            {
                _lastOutputSwitchTime = outputLast.Value;
            }

            if (inputLast.HasValue)
            {
                _lastInputSwitchTime = inputLast.Value;
            }
        }

        public void ResetSwitchTimes()
        {
            _lastOutputSwitchTime = default;
            _lastInputSwitchTime = default;
        }

        public void Dispose()
        {
            _outputSwitchSemaphore.Dispose();
            _inputSwitchSemaphore.Dispose();
        }
    }
}

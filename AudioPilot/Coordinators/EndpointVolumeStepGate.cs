using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal sealed class EndpointVolumeStepGate
    {
        private readonly Lock _playbackLock = new();
        private readonly Lock _recordingLock = new();

        internal bool Execute(AudioMixerMode mode, Func<bool> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            Lock targetLock = mode == AudioMixerMode.Input ? _recordingLock : _playbackLock;
            lock (targetLock)
            {
                return operation();
            }
        }
    }
}

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayCommandSequenceStore
    {
        private long _latestCommandSequence;
        private long _latestReadOnlyCommandSequence;

        public long GetNextCommandSequence()
        {
            return Interlocked.Increment(ref _latestCommandSequence);
        }

        public long GetNextReadOnlyCommandSequence()
        {
            return Interlocked.Decrement(ref _latestReadOnlyCommandSequence);
        }

        public bool IsCommandSequenceCurrent(long commandSequence)
        {
            if (commandSequence < 0)
            {
                return true;
            }

            return commandSequence == Volatile.Read(ref _latestCommandSequence);
        }
    }
}

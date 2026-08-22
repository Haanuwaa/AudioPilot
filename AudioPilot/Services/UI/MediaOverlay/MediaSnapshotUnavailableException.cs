namespace AudioPilot.Services.UI.MediaOverlay;

/// <summary>Distinguishes a failed read from a successful query with no media session.</summary>
internal sealed class MediaSnapshotUnavailableException : Exception
{
    internal string ErrorType => InnerException?.GetType().Name ?? nameof(MediaSnapshotUnavailableException);
    internal MediaSnapshotUnavailableException(Exception? inner = null)
        : base("Media information could not be read. Try again shortly.", inner)
    {
        if (inner != null) HResult = inner.HResult;
    }
}

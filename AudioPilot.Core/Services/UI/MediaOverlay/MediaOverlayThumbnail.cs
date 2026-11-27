using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AudioPilot.Services.UI.MediaOverlay;

internal static class MediaOverlayThumbnail
{
    internal static IRandomAccessStreamReference? Get(GlobalSystemMediaTransportControlsSessionMediaProperties? properties)
    {
        try { return properties?.Thumbnail; }
        catch (Exception) { return null; } // A provider's artwork failure must not discard its text.
    }
}

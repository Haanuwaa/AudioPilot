namespace AudioPilot.Helpers;

internal static class NetworkHelper
{
    /// <summary>
    /// Preserves the exact network identity, returning empty string if null.
    /// </summary>
    /// <param name="networkName">The network name to normalize.</param>
    /// <returns>The normalized network name.</returns>
    public static string NormalizeNetworkName(string? networkName)
    {
        return networkName ?? string.Empty;
    }
}

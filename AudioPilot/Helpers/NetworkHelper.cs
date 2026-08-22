namespace AudioPilot.Helpers;

internal static class NetworkHelper
{
    /// <summary>
    /// Normalizes a network name by trimming whitespace and returning empty string if null.
    /// </summary>
    /// <param name="networkName">The network name to normalize.</param>
    /// <returns>The normalized network name.</returns>
    public static string NormalizeNetworkName(string? networkName)
    {
        return networkName?.Trim() ?? string.Empty;
    }
}

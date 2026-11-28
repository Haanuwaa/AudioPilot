namespace AudioPilot.Helpers;

internal static class NetworkHelper
{
    public static string NormalizeNetworkName(string? networkName)
    {
        return networkName ?? string.Empty;
    }
}

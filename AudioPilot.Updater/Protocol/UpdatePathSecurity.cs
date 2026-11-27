namespace AudioPilot.UpdateProtocol;

/// <summary>Shared reparse-point checks for update paths and ancestor directories.</summary>
public static class UpdatePathSecurity
{
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public static bool HasRedirectedAncestor(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if (IsReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }
}

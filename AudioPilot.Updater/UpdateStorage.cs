namespace AudioPilot.Updater;

internal sealed record UpdateDirectoryOwner(int Schema, string Kind, string Identity);

/// <summary>Reclaims only marked updater directories, preserving live deployment and recovery references.</summary>
internal static class UpdateStorage
{
    internal const string Marker = ".updater-owned.json";

    internal static string CreateDirectory(OwnedWorkspace workspace, string relative, string kind)
    {
        string path = workspace.Resolve(relative);
        if (Directory.Exists(path)) throw new IOException("The updater directory already exists.");
        Directory.CreateDirectory(path);
        try { OwnedWorkspace.WriteJson(Path.Combine(path, Marker), new UpdateDirectoryOwner(1, kind, Path.GetFileName(path))); }
        catch
        {
            try
            {
                OwnedWorkspace.CheckAncestors(path);
                if (Directory.EnumerateFileSystemEntries(path).All(entry => Path.GetFileName(entry) == Marker || Path.GetFileName(entry) == Marker + ".new"))
                {
                    File.Delete(Path.Combine(path, Marker + ".new"));
                    File.Delete(Path.Combine(path, Marker));
                    Directory.Delete(path);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { ReportDeferred(error); }
            throw;
        }
        return path;
    }

    internal static void Trim(OwnedWorkspace workspace, string relative, string kind, HashSet<string> retained)
    {
        try
        {
            string root = workspace.Resolve(relative);
            if (!Directory.Exists(root)) return;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (retained.Contains(Path.GetFileName(directory))) continue;
                Remove(workspace, Path.GetRelativePath(workspace.Root, directory), kind);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { ReportDeferred(error); }
    }

    internal static void Remove(OwnedWorkspace workspace, string relative, string kind)
    {
        try
        {
            string directory = workspace.Resolve(relative);
            string marker = workspace.Resolve(Path.Combine(relative, Marker));
            if (!File.Exists(marker)) return;
            var owner = OwnedWorkspace.ReadJson<UpdateDirectoryOwner>(marker);
            if (owner != new UpdateDirectoryOwner(1, kind, Path.GetFileName(directory))) return;
            CheckTree(directory);
            DeleteOwnedDirectory(directory, owner);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ReportDeferred(error); }
    }

    internal static void ReportDeferred(Exception error)
    {
        try { Console.Error.WriteLine($"Updater cleanup deferred: {error.GetType().Name}"); }
        catch { }
    }

    private static void DeleteOwnedDirectory(string directory, UpdateDirectoryOwner owner)
    {
        string marker = Path.Combine(directory, Marker);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (path.Equals(marker, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        File.Delete(marker);
        try { Directory.Delete(directory); }
        catch
        {
            if (Directory.Exists(directory)) OwnedWorkspace.WriteJson(marker, owner);
            throw;
        }
    }

    private static void CheckTree(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Redirected cleanup target.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
            }
        }
    }
}

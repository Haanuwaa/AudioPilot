using System.Security.Cryptography;

namespace AudioPilot.Updater;

internal sealed record SnapshotFile(string Name, string Sha256);
internal sealed record SnapshotManifest(int Schema, string DataRoot, SnapshotFile[] Files, bool PlacementOnly = false);

/// <summary>Durable, bounded settings and placement backup. Logs and unrelated user files are never restored or deleted.</summary>
internal sealed class ConfigurationSnapshot(OwnedWorkspace workspace)
{
    public string Capture(string dataRoot, bool placementOnly = false)
    {
        dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (dataRoot == Path.GetPathRoot(dataRoot)) throw new InvalidDataException("A configuration root must be a dedicated folder.");
        OwnedWorkspace.CheckAncestors(dataRoot);
        string id = Guid.NewGuid().ToString("N");
        string directory = workspace.Resolve("snapshots/" + id);
        Directory.CreateDirectory(directory);
        var files = ReadNames(dataRoot).Where(name => !placementOnly || name == "window-placement.json").ToArray();
        long total = 0;
        var entries = new List<SnapshotFile>();
        foreach (string name in files)
        {
            byte[] bytes = ReadBounded(Path.Combine(dataRoot, name));
            total = checked(total + bytes.Length);
            if (total > 16 * 1024 * 1024) throw new InvalidDataException("Configuration backup exceeds its bound.");
            string destination = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            WriteDurable(destination, bytes);
            entries.Add(new(name, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        OwnedWorkspace.WriteJson(Path.Combine(directory, "snapshot.json"), new SnapshotManifest(1, dataRoot, [.. entries], placementOnly));
        return id;
    }

    public void Restore(string id, string expectedDataRoot)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid configuration snapshot.");
        string directory = workspace.Resolve("snapshots/" + id);
        var manifest = OwnedWorkspace.ReadJson<SnapshotManifest>(Path.Combine(directory, "snapshot.json"));
        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDataRoot));
        if (manifest.Schema != 1 || !data.Equals(manifest.DataRoot, StringComparison.OrdinalIgnoreCase) || manifest.Files.Length > 32)
            throw new InvalidDataException("Configuration snapshot owner does not match.");
        OwnedWorkspace.CheckAncestors(data);
        var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (!IsConfigurationFile(file.Name) || manifest.PlacementOnly && file.Name != "window-placement.json" || contents.ContainsKey(file.Name)) throw new InvalidDataException("Invalid configuration entry.");
            string source = workspace.Resolve("snapshots/" + id + "/" + file.Name);
            byte[] bytes = ReadBounded(source);
            total += bytes.Length;
            if (total > 16 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != file.Sha256)
                throw new InvalidDataException("Configuration backup verification failed.");
            contents.Add(file.Name, bytes);
        }
        string[] current = [.. ReadNames(data).Where(name => !manifest.PlacementOnly || name == "window-placement.json")];
        foreach (var (name, bytes) in contents)
        {
            string target = Path.Combine(data, name);
            OwnedWorkspace.CheckAncestors(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".update-restore";
            OwnedWorkspace.CheckAncestors(temporary);
            WriteDurable(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
        foreach (string name in current.Except(contents.Keys, StringComparer.OrdinalIgnoreCase)) File.Delete(Path.Combine(data, name));
    }

    private static IEnumerable<string> ReadNames(string root)
    {
        if (!Directory.Exists(root)) yield break;
        int count = 0;
        foreach (string directory in new[] { root, Path.Combine(root, "backups") })
        {
            OwnedWorkspace.CheckAncestors(directory);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetRelativePath(root, path);
                if (!IsConfigurationFile(name)) continue;
                OwnedWorkspace.CheckAncestors(path);
                if (++count > 32 || new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Configuration backup exceeds its bound.");
                yield return name;
            }
        }
    }

    private static bool IsConfigurationFile(string name)
    {
        if (name.Contains(':') || Path.IsPathRooted(name)) return false;
        string[] parts = name.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' '))) return false;
        string file = parts[^1];
        return (parts.Length == 1 && (file == "window-placement.json" || file == "settings.json" ||
                file.StartsWith("settings.json.", StringComparison.Ordinal))) ||
            (parts.Length == 2 && parts[0] == "backups" && file.StartsWith("settings.json", StringComparison.Ordinal));
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(true);
    }

    private static byte[] ReadBounded(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("Oversized configuration file.");
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }
}

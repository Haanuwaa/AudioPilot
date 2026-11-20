using System.Buffers;
using System.Security.Cryptography;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Updater;

internal sealed record SnapshotFile(string Name, string Sha256);
internal sealed record SnapshotManifest(int Schema, string DataRoot, SnapshotFile[] Files, bool PlacementOnly = false);

/// <summary>Durable, bounded settings and placement backup. Logs and unrelated user files are never restored or deleted.</summary>
internal sealed class ConfigurationSnapshot(OwnedWorkspace workspace)
{
    private const int MaxFiles = 32;
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxSnapshotBytes = SettingsStorageLimits.SnapshotBudgetBytes;
    private static readonly SearchValues<char> LocationHex = SearchValues.Create("0123456789ABCDEFabcdef");
    public string Capture(string dataRoot, bool placementOnly = false)
    {
        dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (dataRoot == Path.GetPathRoot(dataRoot)) throw new InvalidDataException("A configuration root must be a dedicated folder.");
        OwnedWorkspace.CheckAncestors(dataRoot);
        string id = Guid.NewGuid().ToString("N");
        string directory = UpdateStorage.CreateDirectory(workspace, "snapshots/" + id, "snapshot");
        try
        {
            int remainingBytes = MaxSnapshotBytes;
            var entries = new List<SnapshotFile>();
            foreach (string name in ReadNames(dataRoot, placementOnly))
            {
                byte[] bytes = ReadBounded(Path.Combine(dataRoot, name), remainingBytes);
                remainingBytes -= bytes.Length;
                string destination = Path.Combine(directory, name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                WriteDurable(destination, bytes);
                entries.Add(new(name, Convert.ToHexString(SHA256.HashData(bytes))));
            }
            OwnedWorkspace.WriteJson(Path.Combine(directory, "snapshot.json"), new SnapshotManifest(1, dataRoot, [.. entries], placementOnly));
            UpdateLog.Write(workspace.Diagnostics, new(UpdateLogKind.ConfigurationCaptured));
            return id;
        }
        catch
        {
            UpdateStorage.Remove(workspace, "snapshots/" + id, "snapshot");
            throw;
        }
    }

    public void Restore(string id, string expectedDataRoot)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid configuration snapshot.");
        var manifest = OwnedWorkspace.ReadJson<SnapshotManifest>(workspace.Resolve("snapshots/" + id + "/snapshot.json"));
        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDataRoot));
        if (manifest.Schema != 1 || !data.Equals(manifest.DataRoot, StringComparison.OrdinalIgnoreCase) || manifest.Files.Length > MaxFiles)
            throw new InvalidDataException("Configuration snapshot owner does not match.");
        OwnedWorkspace.CheckAncestors(data);
        var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int remainingBytes = MaxSnapshotBytes;
        foreach (var file in manifest.Files)
        {
            string name = file.Name.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (!IsConfigurationFile(name) || manifest.PlacementOnly && !IsPlacementFile(name) || contents.ContainsKey(name)) throw new InvalidDataException("Invalid configuration entry.");
            string source = workspace.Resolve("snapshots/" + id + "/" + name);
            byte[] bytes = ReadBounded(source, remainingBytes);
            remainingBytes -= bytes.Length;
            if (Convert.ToHexString(SHA256.HashData(bytes)) != file.Sha256)
                throw new InvalidDataException("Configuration backup verification failed.");
            contents.Add(name, bytes);
        }
        string[] current = [.. ReadNames(data, manifest.PlacementOnly)];
        foreach (var (name, bytes) in contents)
        {
            string target = Path.Combine(data, name);
            OwnedWorkspace.CheckAncestors(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".update-restore-" + Guid.NewGuid().ToString("N");
            OwnedWorkspace.CheckAncestors(temporary);
            try
            {
                WriteDurable(temporary, bytes);
                File.Move(temporary, target, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        foreach (string name in current.Except(contents.Keys, StringComparer.OrdinalIgnoreCase)) File.Delete(Path.Combine(data, name));
        UpdateLog.Write(workspace.Diagnostics, new(UpdateLogKind.ConfigurationRestored));
    }

    private static IEnumerable<string> ReadNames(string root, bool placementOnly)
    {
        if (!Directory.Exists(root)) yield break;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] directories = placementOnly ? [root] : [root, Path.Combine(root, "backups")];
        foreach (string directory in directories)
        {
            OwnedWorkspace.CheckAncestors(directory);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetRelativePath(root, path);
                if (!(placementOnly ? IsPlacementFile(name) : IsConfigurationFile(name))) continue;
                OwnedWorkspace.CheckAncestors(path);
                if (!names.Add(name)) throw new InvalidDataException("Ambiguous configuration filename.");
                if (names.Count > MaxFiles) throw new InvalidDataException("Configuration backup exceeds its bound.");
                yield return name;
            }
        }
    }

    private static bool IsPlacementFile(string name) =>
        name.Equals("window-placement.json", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("quick-panel-layouts.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsConfigurationFile(string name)
    {
        if (name.Contains(':') || Path.IsPathRooted(name)) return false;
        string[] parts = name.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' '))) return false;
        string file = parts[^1];
        if (parts.Length == 1)
            return IsPlacementFile(file) || file.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
                file.Length == 39 && file.StartsWith("settings.json.", StringComparison.OrdinalIgnoreCase) &&
                file.EndsWith(".location", StringComparison.OrdinalIgnoreCase) && file.AsSpan(14, 16).IndexOfAnyExcept(LocationHex) < 0;
        if (parts.Length != 2 || !parts[0].Equals("backups", StringComparison.OrdinalIgnoreCase)) return false;
        return file.Equals("settings.json.bak", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("settings.json.bak.pending", StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith("settings.json.bak.", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(file.AsSpan(18), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) &&
            index > 0 && file[18] != '0';
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(true);
    }

    private static byte[] ReadBounded(string path, int remainingBytes)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = input.Length;
        if (length > MaxFileBytes || length > remainingBytes)
            throw new InvalidDataException("Configuration backup exceeds its bound.");
        byte[] bytes = new byte[(int)length];
        input.ReadExactly(bytes);
        return bytes;
    }
}

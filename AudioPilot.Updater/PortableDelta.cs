using System.IO.Compression;
using System.Security.Cryptography;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

internal sealed record DeltaFile(string Name, long Length, string Sha256);
internal sealed record PortableDeltaManifest(int Schema, string BaseVersion, string BaseSha256, string TargetSha256, DeltaFile[] BaseFiles, DeltaFile[] Files);

/// <summary>A TUF-authenticated list of complete target files. Reused files are copied, never linked to the live generation.</summary>
internal static class PortableDelta
{
    internal const string DirectoryName = "job-delta";
    private const string ManifestName = "manifest.json";
    private const string EntryPrefix = "files/";
    private const int BufferSize = 65536;
    private const long MaxBytes = 1024L * 1024 * 1024;
    private const int MaxManifest = 4 * 1024 * 1024;
    private static readonly DateTimeOffset EntryTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static string Name(string target, string baseVersion) => target[..^4] + ".from-" + baseVersion + ".delta.zip";

    internal static bool IsGeneration(string generation, string version) =>
        generation.StartsWith(version + "_", StringComparison.Ordinal) &&
        Guid.TryParseExact(generation[(version.Length + 1)..], "N", out _);

    internal static async Task CreateAsync(string baseline, string target, string baseVersion, string output, CancellationToken token)
    {
        _ = UpdateTrustMetadata.ParseVersion(baseVersion);
        OwnedWorkspace.CheckAncestors(baseline);
        OwnedWorkspace.CheckAncestors(target);
        OwnedWorkspace.CheckAncestors(output);
        await using var baseStream = OpenRead(baseline);
        string baseHash = Convert.ToHexString(await SHA256.HashDataAsync(baseStream, token));
        baseStream.Position = 0;
        using var previous = new ZipArchive(baseStream, ZipArchiveMode.Read, leaveOpen: true);
        await using var targetStream = OpenRead(target);
        string targetHash = Convert.ToHexString(await SHA256.HashDataAsync(targetStream, token));
        targetStream.Position = 0;
        using var current = new ZipArchive(targetStream, ZipArchiveMode.Read, leaveOpen: true);
        var oldEntries = ReadEntries(previous);
        var entries = ReadEntries(current);
        var baseFiles = await DescribeAsync(oldEntries, token);
        var files = await DescribeAsync(entries, token);
        var baseByName = baseFiles.ToDictionary(file => file.Name, StringComparer.Ordinal);
        var manifest = new PortableDeltaManifest(1, baseVersion, baseHash, targetHash, baseFiles, files);
        byte[] metadata = UpdateTrustMetadata.Encode(manifest);
        if (metadata.Length > MaxManifest) throw new InvalidDataException("Delta manifest exceeds its size limit.");
        await using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        using var delta = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);
        await using (Stream entry = CreateEntry(delta, ManifestName))
            await entry.WriteAsync(metadata, token);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (baseByName.GetValueOrDefault(file.Name) == file) continue;
            await using Stream input = entries[file.Name].Open();
            await using Stream changed = CreateEntry(delta, EntryPrefix + file.Name);
            await input.CopyToAsync(changed, token);
        }
    }

    private static Stream CreateEntry(ZipArchive archive, string name)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = EntryTimestamp;
        return entry.Open();
    }

    internal static async Task StageAsync(OwnedWorkspace workspace, string baseline, string generation, string deltaPath,
        AuthorizedUpdate authorization, string baseVersion, CancellationToken token)
    {
        using var archive = await OpenVerifiedAsync(deltaPath, authorization.Delta!, token);
        var (manifest, changed) = ReadManifest(archive, authorization.Artifact, baseVersion);
        var oldFiles = ReadFiles(baseline);
        var targetFiles = manifest.Files.ToDictionary(file => file.Name, StringComparer.Ordinal);
        var baseFiles = manifest.BaseFiles.ToDictionary(file => file.Name, StringComparer.Ordinal);
        if (oldFiles.Count != baseFiles.Count || oldFiles.Keys.Any(name => !baseFiles.ContainsKey(name)))
            throw new InvalidDataException("Installed portable file set differs from the signed baseline.");
        string destination = UpdateStorage.CreateDirectory(workspace, Path.Combine(DirectoryName, generation), "generation");
        byte[] buffer = new byte[BufferSize];
        foreach (var file in manifest.BaseFiles)
        {
            token.ThrowIfCancellationRequested();
            using var input = OpenRead(oldFiles[file.Name]);
            if (targetFiles.GetValueOrDefault(file.Name) == file)
                await WriteVerifiedAsync(input, destination, file, buffer, token);
            else
                await VerifyFileAsync(input, file, token);
        }
        foreach (var file in manifest.Files)
        {
            if (!changed.TryGetValue(file.Name, out var source)) continue;
            token.ThrowIfCancellationRequested();
            using var input = source.Open();
            await WriteVerifiedAsync(input, destination, file, buffer, token);
        }
        VerifyPublisher(destination, authorization, token);
    }

    internal static async Task VerifyStagedAsync(string directory, string deltaPath, AuthorizedUpdate authorization, string baseVersion, CancellationToken token)
    {
        using var archive = await OpenVerifiedAsync(deltaPath, authorization.Delta!, token);
        await VerifyStagedAsync(directory, archive, authorization, baseVersion, token);
    }

    internal static async Task VerifyStagedAsync(string directory, ZipArchive archive, AuthorizedUpdate authorization, string baseVersion, CancellationToken token)
    {
        var (manifest, _) = ReadManifest(archive, authorization.Artifact, baseVersion);
        var paths = ReadFiles(directory);
        if (paths.Count != manifest.Files.Length) throw new InvalidDataException("Prepared portable file set changed.");
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!paths.TryGetValue(file.Name, out string? path)) throw new InvalidDataException("Prepared portable file is missing.");
            using var stream = OpenRead(path);
            await VerifyFileAsync(stream, file, token);
        }
        VerifyPublisher(directory, authorization, token);
    }

    private static void VerifyPublisher(string directory, AuthorizedUpdate authorization, CancellationToken token)
    {
        if (authorization.WindowsPublisher is { } publisher) WindowsUpdatePublisher.VerifyPortableDirectory(directory, publisher, token);
    }

    internal static async Task<ZipArchive> OpenVerifiedAsync(string path, UpdateArtifact artifact, CancellationToken token)
    {
        var stream = OpenRead(path);
        try
        {
            if (stream.Length != artifact.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != artifact.Sha256)
                throw new InvalidDataException("Portable delta does not match its TUF target.");
            stream.Position = 0;
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch { stream.Dispose(); throw; }
    }

    private static (PortableDeltaManifest, Dictionary<string, ZipArchiveEntry>) ReadManifest(ZipArchive archive, UpdateArtifact full, string baseVersion)
    {
        var entries = ReadEntries(archive, isDelta: true);
        if (!entries.Remove(ManifestName, out var metadata) || metadata.Length is < 1 or > MaxManifest)
            throw new InvalidDataException("Portable delta manifest is missing or oversized.");
        using var input = metadata.Open();
        byte[] bytes = new byte[(int)metadata.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("Portable delta manifest exceeds its declared length.");
        var manifest = UpdateTrustMetadata.Decode<PortableDeltaManifest>(bytes, MaxManifest);
        if (manifest.Schema != 1 || manifest.BaseVersion != baseVersion || !UpdateTrustMetadata.IsDigest(manifest.BaseSha256) || manifest.TargetSha256 != full.Sha256)
            throw new InvalidDataException("Portable delta belongs to another base or target.");
        ValidateFiles(manifest.BaseFiles);
        ValidateFiles(manifest.Files);
        var baseFiles = manifest.BaseFiles.ToDictionary(file => file.Name, StringComparer.Ordinal);
        var changed = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (baseFiles.GetValueOrDefault(file.Name) == file) continue;
            string name = EntryPrefix + file.Name;
            if (!entries.Remove(name, out var entry) || entry.FullName != name || entry.Length != file.Length)
                throw new InvalidDataException("Portable delta is missing a changed file.");
            changed.Add(file.Name, entry);
        }
        if (entries.Count != 0) throw new InvalidDataException("Portable delta contains undeclared files.");
        return (manifest, changed);
    }

    private static void ValidateFiles(DeltaFile[] files)
    {
        if (files.Length is < 1 or > 4096) throw new InvalidDataException("Invalid portable delta file count.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long length = 0;
        foreach (var file in files)
        {
            if (file == null) throw new InvalidDataException("Missing portable delta file identity.");
            ValidateName(file.Name);
            if (!UpdateTrustMetadata.IsDigest(file.Sha256) || !seen.Add(file.Name) || file.Length is < 0 or > MaxBytes || (length += file.Length) > MaxBytes)
                throw new InvalidDataException("Invalid portable delta file identity or size.");
        }
    }

    private static Dictionary<string, ZipArchiveEntry> ReadEntries(ZipArchive archive, bool isDelta = false)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count == 0 || archive.Entries.Count > (isDelta ? 4097 : 4096)) throw new InvalidDataException("Invalid portable archive entry count.");
        long length = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateName(entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                entry.Length is < 0 or > MaxBytes || (length += entry.Length) > MaxBytes + (isDelta ? MaxManifest : 0) || !entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("Unsafe or oversized portable archive.");
        }
        return entries;
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 240 || name.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl) ||
            name.Split('/').Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                PortableDeployment.IsDeviceName(part) || part.Equals(UpdateStorage.Marker, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unsafe portable delta file path.");
    }

    private static Dictionary<string, string> ReadFiles(string root)
    {
        OwnedWorkspace.CheckAncestors(root);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        int directories = 0;
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Redirected portable path.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (++directories > 4096) throw new InvalidDataException("Too many portable directories.");
                    pending.Push(path);
                    continue;
                }
                string name = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (name == UpdateStorage.Marker) continue;
                ValidateName(name);
                if (files.Count == 4096) throw new InvalidDataException("Too many portable files.");
                files.Add(name, path);
            }
        }
        return files;
    }

    private static async Task<DeltaFile[]> DescribeAsync(Dictionary<string, ZipArchiveEntry> entries, CancellationToken token)
    {
        var result = new List<DeltaFile>(entries.Count);
        foreach (var entry in entries.Values.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            using var stream = entry.Open();
            result.Add(new(entry.FullName, entry.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, token))));
        }
        return [.. result];
    }

    private static FileStream OpenRead(string path)
    {
        OwnedWorkspace.CheckAncestors(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async Task VerifyFileAsync(Stream stream, DeltaFile file, CancellationToken token)
    {
        if (stream.Length != file.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != file.Sha256)
            throw new InvalidDataException("Portable file does not match the signed manifest.");
    }

    private static async Task WriteVerifiedAsync(Stream input, string destination, DeltaFile file, byte[] buffer, CancellationToken token)
    {
        string path = Path.Combine(destination, file.Name);
        OwnedWorkspace.CheckAncestors(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if ((length += count) > file.Length) throw new InvalidDataException("Portable delta expanded past its declared length.");
            digest.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (length != file.Length || Convert.ToHexString(digest.GetHashAndReset()) != file.Sha256)
            throw new InvalidDataException("Portable delta file digest mismatch.");
        output.Flush(flushToDisk: true);
    }
}

using System.Buffers;
using System.ComponentModel;
using System.Text.Json;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Updater;

/// <summary>Mirrors user-facing documents from the settled generation without affecting deployment outcomes.</summary>
internal static class PortableDocuments
{
    internal const string ReceiptName = ".documents-generation";
    internal static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(
    [
        "ABOUT.txt", "licenses/LICENSE", "licenses/THIRD-PARTY-NOTICES.txt", "licenses/UPDATER-NOTICES.txt"
    ]);

    internal static void Synchronize(OwnedWorkspace workspace, PortableState state, bool onlyIfNeeded = false)
    {
        try
        {
            string layoutPath = workspace.Resolve(PortableLayout.FileName);
            if (!File.Exists(layoutPath)) return;
            var layout = OwnedWorkspace.ReadJson<PortableLayoutDescriptor>(layoutPath);
            if (!layout.Managed) return;
            if (layout.Schema != 1 || !Guid.TryParseExact(layout.DiagnosticId, "N", out var id) || id == Guid.Empty)
                throw new InvalidDataException("Invalid portable document identity.");
            if (Path.GetFileName(workspace.Root) != PortableLayout.AppDirectoryName)
                throw new InvalidDataException("Invalid portable document root.");
            string? generation = state.PendingHealth ? state.Previous : state.Current;
            if (generation == null) return;
            string root = Path.GetDirectoryName(workspace.Root)!;
            string receipt = workspace.Resolve(ReceiptName);
            string? mirroredGeneration = File.Exists(receipt) && new FileInfo(receipt).Length <= 100 ? File.ReadAllText(receipt) : null;
            if (onlyIfNeeded && mirroredGeneration == generation && Names.All(name => File.Exists(Path.Combine(root, name)))) return;
            foreach (string name in Names)
            {
                string source = workspace.Resolve(Path.Combine("versions", generation, name));
                string destination = Path.Combine(root, name);
                string temporary = workspace.Resolve(".document-" + Path.GetFileName(name) + ".new");
                try
                {
                    OwnedWorkspace.CheckAncestors(destination);
                    using var input = File.OpenRead(source);
                    if (input.Length is 0 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid portable document size.");
                    if (Matches(input, destination)) continue;
                    if (mirroredGeneration == generation)
                    {
                        File.Delete(receipt);
                        mirroredGeneration = null;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    input.Position = 0;
                    using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    {
                        input.CopyTo(output);
                        output.Flush(true);
                    }
                    OwnedWorkspace.Promote(temporary, destination);
                }
                finally
                {
                    OwnedWorkspace.CheckAncestors(temporary);
                    File.Delete(temporary);
                }
            }
            if (mirroredGeneration != generation)
            {
                try { OwnedWorkspace.WriteText(receipt, generation); }
                finally { File.Delete(workspace.Resolve(ReceiptName + ".new")); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Win32Exception)
        {
            UpdateLog.Write(workspace.Diagnostics, new(UpdateLogKind.CleanupFailed, Operation: "portable-documents", Reason: "refresh-deferred", Error: error));
            try { Console.Error.WriteLine($"AudioPilot document refresh deferred: {error.GetType().Name}"); } catch { }
        }
    }

    private static bool Matches(Stream source, string destination)
    {
        if (!File.Exists(destination)) return false;
        using var current = File.OpenRead(destination);
        if (current.Length != source.Length) return false;
        byte[] left = ArrayPool<byte>.Shared.Rent(65536);
        byte[] right = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (source.Position < source.Length)
            {
                int count = source.Read(left);
                current.ReadExactly(right.AsSpan(0, count));
                if (!left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) return false;
            }
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(left); ArrayPool<byte>.Shared.Return(right); }
    }
}

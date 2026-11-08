using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioPilot.UpdateProtocol;

public sealed record UpdaterEndpoint(int Schema, string Pipe, int Pid, long Started, string Helper);
public sealed record UpdaterControlRequest(int Schema, string Operation, string? Version = null, bool Approve = false);
public sealed record UpdaterControlReply(int Schema, bool Accepted, string Phase, string? Version = null,
    long Bytes = 0, long? Total = null, string? Error = null, string? Reason = null);

/// <summary>Current-user control channel bound to the staged helper's exact process creation identity.</summary>
public static class UpdaterControlWire
{
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    public static string Workspace(string directory)
    {
        string? data = PortableLayout.GetDataRoot(directory, null);
        if (data != null) return Path.GetDirectoryName(data)!;
        string app = Path.GetFullPath(Path.Combine(directory, "AudioPilot.exe"));
        return Path.Combine(Path.GetDirectoryName(UpdateClientPolicy.RuntimeRoot)!, "installations",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(app.ToUpperInvariant())))[..32]);
    }
    public static string EndpointPath(string workspace) => Path.Combine(workspace, "control.json");

    public static UpdaterEndpoint? ReadEndpoint(string workspace)
    {
        string path = EndpointPath(workspace);
        try
        {
            CheckPath(path);
            using var stream = File.OpenRead(path);
            if (stream.Length > 4096) throw new InvalidDataException("Oversized updater endpoint.");
            var endpoint = JsonSerializer.Deserialize<UpdaterEndpoint>(stream, Json) ?? throw new InvalidDataException("Missing updater endpoint.");
            if (endpoint.Schema != 1 || !endpoint.Pipe.StartsWith("AudioPilot.Updater.", StringComparison.Ordinal) ||
                !Guid.TryParseExact(endpoint.Pipe["AudioPilot.Updater.".Length..], "N", out _) || endpoint.Pid <= 0 || !UpdateClientPolicy.IsHelperPath(endpoint.Helper))
                throw new InvalidDataException("Invalid updater endpoint identity.");
            using var process = Process.GetProcessById(endpoint.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == endpoint.Started ? endpoint : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or ArgumentException) { return null; }
    }

    public static UpdaterControlReply? ReadLastResult(string workspace)
    {
        string path = Path.Combine(workspace, "control-result.json");
        try
        {
            CheckPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 4096) throw new InvalidDataException("Oversized updater result.");
            var reply = JsonSerializer.Deserialize<UpdaterControlReply>(stream, Json);
            if (reply?.Schema != 1 || reply.Bytes < 0 || reply.Total < 0) throw new InvalidDataException("Invalid updater result.");
            return reply;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    public static async Task<UpdaterControlReply> SendAsync(UpdaterEndpoint endpoint, UpdaterControlRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", endpoint.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        UpdateLifecycleWire.VerifyServer(pipe, new(1, new string('A', 64), endpoint.Pipe, endpoint.Pid, endpoint.Started, endpoint.Helper, new string('0', 32), false, false));
        bool submitted = false;
        try
        {
            submitted = true;
            await WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            var reply = await ReadAsync<UpdaterControlReply>(pipe, timeout.Token).ConfigureAwait(false);
            if (reply.Schema != 1) throw new InvalidDataException("Unsupported updater control reply.");
            return reply;
        }
        catch (Exception error) when (submitted && error is IOException or OperationCanceledException or JsonException)
        { throw new IOException("The updater may have accepted this command. Inspect update status before retrying.", error); }
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 16384) throw new InvalidDataException("Oversized updater control message.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 16384) throw new InvalidDataException("Invalid updater control message length.");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Missing updater control message.");
    }
    public static void CheckPath(string path)
    {
        for (string? ancestor = Path.GetFullPath(path); ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
            if (Path.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Redirected updater control path.");
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioPilot.UpdateProtocol;

public sealed record UpdateLaunch(int Schema, string Nonce, string PipeName, int HelperPid, long HelperStartedUtcTicks,
    string HelperPath, string DiagnosticId, bool Restart, bool Visible, bool SandboxTest = false, bool Pilot = false);
public sealed record UpdateRuntimeInfo(string ApplicationDirectory, string SettingsPath, string LocalStateRoot);
public sealed record UpdateMessage(int Schema, string Nonce, string Kind, int Pid, string? Version = null,
    bool Visible = false, string? Reason = null, UpdateRuntimeInfo? Runtime = null);

/// <summary>Bounded, same-user IPC for explicitly supervised lifecycle probes. It never resolves an owner by executable name.</summary>
public static partial class UpdateLifecycleWire
{
    public const string EnvironmentVariable = "AUDIOPILOT_UPDATE_LIFECYCLE";
    private const int MaximumBytes = 8192;
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public static string SerializeLaunch(UpdateLaunch launch) => JsonSerializer.Serialize(launch, Json);
    public static UpdateLaunch ParseLaunch(string text, string? diagnosticId)
    {
        if (text.Length > MaximumBytes) throw new InvalidDataException("Oversized update launch descriptor.");
        var launch = JsonSerializer.Deserialize<UpdateLaunch>(text, Json) ?? throw new InvalidDataException("Empty update launch descriptor.");
        if (launch.Schema != 1 || launch.Nonce.Length != 64 || !launch.Nonce.All(Uri.IsHexDigit) ||
            launch.PipeName != "AudioPilot.Update." + launch.Nonce || launch.HelperPid <= 0 || launch.HelperStartedUtcTicks <= 0 ||
            !Path.IsPathFullyQualified(launch.HelperPath) || !Guid.TryParseExact(launch.DiagnosticId, "N", out var id) || id == Guid.Empty ||
            !(launch.Pilot ? !launch.SandboxTest && diagnosticId == null && UpdateClientPolicy.IsHelperPath(launch.HelperPath) :
                launch.SandboxTest ? diagnosticId == null && Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase) : string.Equals(diagnosticId, launch.DiagnosticId, StringComparison.Ordinal)))
            throw new InvalidDataException("Lifecycle integration requires a valid, isolated diagnostic launch.");
        return launch;
    }

    public static UpdateMessage Message(UpdateLaunch launch, string kind, string? reason = null, bool visible = false) =>
        new(1, launch.Nonce, kind, Environment.ProcessId, typeof(UpdateLifecycleWire).Assembly.GetName().Version?.ToString(), visible, reason);

    public static async Task WriteAsync(Stream pipe, UpdateMessage message, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await WriteBytesAsync(pipe, bytes, token);
    }
    private static async Task WriteBytesAsync(Stream pipe, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Oversized update message.");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await pipe.WriteAsync(prefix, token).ConfigureAwait(false);
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<UpdateMessage> ReadAsync(Stream pipe, UpdateLaunch launch, int expectedPid, CancellationToken token)
    {
        byte[] bytes = await ReadBytesAsync(pipe, token);
        var message = JsonSerializer.Deserialize<UpdateMessage>(bytes, Json) ?? throw new InvalidDataException("Empty update message.");
        if (message.Schema != 1 || message.Nonce != launch.Nonce || message.Pid != expectedPid)
            throw new InvalidDataException("Update peer identity does not match the supervised process.");
        return message;
    }
    private static async Task<byte[]> ReadBytesAsync(Stream pipe, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await pipe.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid update message length.");
        byte[] bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
    public static string AttachmentPipe(int pid, long startedUtcTicks) => $"AudioPilot.Update.Attach.{pid}.{startedUtcTicks}";
    public static Task WriteLaunchAsync(Stream pipe, UpdateLaunch launch, CancellationToken token) => WriteBytesAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(launch, Json), token);
    public static async Task<UpdateLaunch> ReadLaunchAsync(NamedPipeServerStream pipe, string session, CancellationToken token)
    {
        var launch = ParseLaunch(System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(pipe, token)), null);
        if (!(launch.SandboxTest || launch.Pilot) || launch.Restart || launch.DiagnosticId != session || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != launch.HelperPid)
            throw new InvalidDataException("Unexpected updater attachment.");
        VerifyProcess(launch.HelperPid, launch.HelperStartedUtcTicks, launch.HelperPath);
        return launch;
    }

    public static void VerifyServer(NamedPipeClientStream pipe, UpdateLaunch launch)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != launch.HelperPid)
            throw new InvalidDataException("Unexpected lifecycle helper process.");
        VerifyProcess(launch.HelperPid, launch.HelperStartedUtcTicks, launch.HelperPath);
    }
    private static void VerifyProcess(int pid, long started, string path)
    {
        using var helper = Process.GetProcessById(pid);
        using var self = Process.GetCurrentProcess();
        if (helper.HasExited || helper.SessionId != self.SessionId || helper.StartTime.ToUniversalTime().Ticks != started ||
            !string.Equals(helper.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Lifecycle helper identity changed.");
    }

    public static void VerifyClient(NamedPipeServerStream pipe, Process child)
    {
        if (child.HasExited || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != child.Id)
            throw new InvalidDataException("Unexpected lifecycle app process.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}

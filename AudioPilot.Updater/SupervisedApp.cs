using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Updater;

/// <summary>Uses process creation identity and the known launch path without enumerating a still-loading image.</summary>
internal sealed record CandidateOwner(int Pid, long StartedUtcTicks, string Path)
{
    public static CandidateOwner Capture(Process process, string launchedPath) => new(process.Id,
        process.StartTime.ToUniversalTime().Ticks, System.IO.Path.GetFullPath(launchedPath));

    public async Task WaitForExitAsync(CancellationToken token)
    {
        Process process;
        try { process = Process.GetProcessById(Pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            try
            {
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != StartedUtcTicks) return;
                await process.WaitForExitAsync(token);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (!process.HasExited) throw;
            }
        }
    }
}

/// <summary>Authenticates a specific child and keeps staged startup reversible until deployment commits.</summary>
internal sealed class SupervisedApp : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly UpdateLaunch _launch;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(90));
    private readonly string _version;
    private CandidateJob? _candidateJob;
    public Process Process { get; }
    public CancellationToken Token => _timeout.Token;
    public bool Committed { get; private set; }

    internal void ResumeOperationDeadline()
    {
        Token.ThrowIfCancellationRequested();
        _timeout.CancelAfter(TimeSpan.FromSeconds(90));
    }

    internal void SuspendOperationDeadline() => _timeout.CancelAfter(Timeout.InfiniteTimeSpan);

    private SupervisedApp(NamedPipeServerStream pipe, Process child, UpdateLaunch launch, string version)
    {
        _pipe = pipe;
        Process = child;
        _launch = launch;
        _version = version;
    }

    public static async Task<SupervisedApp> LaunchAsync(string path, string diagnosticId, bool restart, bool visible,
        Action<CandidateOwner>? started = null, bool managed = false)
    {
        MsiDeployment.RequireRuntime();
        bool pilot = UpdateClientPolicy.IsHelperPath(Environment.ProcessPath!);
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var helper = Process.GetCurrentProcess();
        var launch = new UpdateLaunch(1, nonce, "AudioPilot.Update." + nonce, helper.Id,
            helper.StartTime.ToUniversalTime().Ticks, helper.MainModule!.FileName!, diagnosticId, restart, visible, managed && !pilot, pilot);
        var server = new NamedPipeServerStream(launch.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        path = Path.GetFullPath(path);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        start.Environment[UpdateLifecycleWire.EnvironmentVariable] = UpdateLifecycleWire.SerializeLaunch(launch);
        if (managed || pilot)
        {
            start.Environment.Remove("AUDIOPILOT_DIAGNOSTIC_SESSION");
            if (pilot) start.Environment.Remove("AUDIOPILOT_UPDATE_TEST_SESSION");
            else start.Environment["AUDIOPILOT_UPDATE_TEST_SESSION"] = diagnosticId;
        }
        else start.Environment["AUDIOPILOT_DIAGNOSTIC_SESSION"] = diagnosticId;
        SupervisedApp? app = null;
        try
        {
            string version = FileVersionInfo.GetVersionInfo(Path.Combine(start.WorkingDirectory, "AudioPilot.dll")).FileVersion
                ?? throw new InvalidDataException("The candidate has no file version.");
            var child = Process.Start(start) ?? throw new InvalidOperationException("AudioPilot did not start.");
            app = new(server, child, launch, version);
            if (restart) app._candidateJob = new CandidateJob(child);
            started?.Invoke(CandidateOwner.Capture(child, path));
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(app.Token);
            Task accepting = server.WaitForConnectionAsync(connection.Token);
            Task exiting = child.WaitForExitAsync(connection.Token);
            try
            {
                if (await Task.WhenAny(accepting, exiting) == exiting)
                    throw new IOException("AudioPilot exited before its lifecycle connection was established.");
                await accepting;
            }
            finally
            {
                connection.Cancel();
                try { await accepting; } catch (OperationCanceledException) { }
                try { await exiting; } catch (OperationCanceledException) { }
            }
            UpdateLifecycleWire.VerifyClient(server, child);
            return app;
        }
        catch
        {
            if (app != null) await app.DisposeAsync();
            else await server.DisposeAsync();
            throw;
        }
    }

    public void Disconnect() => _pipe.Dispose();
    public static async Task<SupervisedApp> ConnectAsync(CandidateOwner owner, string identity)
    {
        MsiDeployment.RequireRuntime();
        var process = Process.GetProcessById(owner.Pid);
        NamedPipeServerStream? server = null;
        SupervisedApp? app = null;
        try
        {
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != owner.StartedUtcTicks || !string.Equals(process.MainModule?.FileName, owner.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The original process identity changed.");
            using var helper = Process.GetCurrentProcess();
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            bool pilot = UpdateClientPolicy.IsHelperPath(Environment.ProcessPath!);
            var launch = new UpdateLaunch(1, nonce, "AudioPilot.Update." + nonce, helper.Id, helper.StartTime.ToUniversalTime().Ticks, helper.MainModule!.FileName!, identity, false, false, !pilot, pilot);
            server = new NamedPipeServerStream(launch.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            app = new(server, process, launch, FileVersionInfo.GetVersionInfo(Path.Combine(Path.GetDirectoryName(owner.Path)!, "AudioPilot.dll")).FileVersion!);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(app.Token);
            Task accepting = server.WaitForConnectionAsync(connection.Token);
            try
            {
                using var attach = new NamedPipeClientStream(".", UpdateLifecycleWire.AttachmentPipe(owner.Pid, owner.StartedUtcTicks), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await attach.ConnectAsync(15000, connection.Token);
                UpdateLifecycleWire.VerifyServer(attach, launch with { HelperPid = owner.Pid, HelperStartedUtcTicks = owner.StartedUtcTicks, HelperPath = owner.Path });
                await UpdateLifecycleWire.WriteLaunchAsync(attach, launch, connection.Token);
                byte[] ack = new byte[1]; await attach.ReadExactlyAsync(ack, connection.Token);
                if (ack[0] != 1) throw new IOException("The app declined the updater attachment.");
                await accepting; UpdateLifecycleWire.VerifyClient(server, process);
                if ((await app.ReadAsync()).Kind != "ready") throw new IOException("The app did not acknowledge its updater.");
                return app;
            }
            finally { connection.Cancel(); try { await accepting; } catch (OperationCanceledException) { } }
        }
        catch
        {
            if (app != null) await app.DisposeAsync();
            else { server?.Dispose(); process.Dispose(); }
            throw;
        }
    }
    public async Task<UpdateMessage> ReadAsync()
    {
        var message = await UpdateLifecycleWire.ReadAsync(_pipe, _launch, Process.Id, Token);
        if (message.Version != _version) throw new InvalidDataException("Unexpected AudioPilot version acknowledged readiness.");
        return message;
    }
    public Task SendAsync(string kind) => UpdateLifecycleWire.WriteAsync(_pipe, UpdateLifecycleWire.Message(_launch, kind), Token);
    public async Task PrepareAsync()
    {
        while (true)
        {
            await SendAsync("prepare");
            var response = await ReadAsync();
            if (response.Kind == "prepared") return;
            if (response.Kind != "deferred" || response.Reason is not ("routine-work-active" or "settings-busy" or "resume-in-progress"))
                throw new InvalidOperationException("Restart was deferred: " + response.Reason);
            await Task.Delay(100, Token);
        }
    }
    public async Task StopNormallyAsync()
    {
        await PrepareAsync();
        await SendAsync("shutdown");
        if ((await ReadAsync()).Kind != "stopped") throw new InvalidOperationException("Runtime did not confirm a complete drain.");
        await Process.WaitForExitAsync(Token);
        if (Process.ExitCode != 0) throw new InvalidOperationException("Normal update shutdown failed.");
    }
    public async Task WaitStagedAsync()
    {
        if ((await ReadAsync()).Kind != "staged-ready") throw new InvalidOperationException("Candidate did not reach its startup gate.");
    }
    public async Task<UpdateMessage> CommitAsync()
    {
        _candidateJob?.Release();
        _candidateJob = null;
        // Deployment is already durable. A later communication failure must not roll back a committed MSI.
        Committed = true;
        await SendAsync("commit");
        var ready = await ReadAsync();
        if (ready.Kind != "ready" || ready.Visible != _launch.Visible) throw new InvalidOperationException("Committed app did not restore its requested presentation.");
        return ready;
    }
    public async Task AbortAsync()
    {
        if (!_launch.Restart || Committed) throw new InvalidOperationException("An original or committed app requires normal shutdown.");
        if (!Process.HasExited)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await UpdateLifecycleWire.WriteAsync(_pipe, UpdateLifecycleWire.Message(_launch, "abort"), deadline.Token); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { Disconnect(); }
            try { await Process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) when (_candidateJob != null)
            {
                _candidateJob.Dispose();
                using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Process.WaitForExitAsync(stopDeadline.Token);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync();
        try
        {
            _candidateJob?.Dispose();
            if (_launch.Restart && !Committed)
            {
                using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Process.WaitForExitAsync(stopDeadline.Token);
            }
        }
        finally { _timeout.Dispose(); Process.Dispose(); }
    }
}

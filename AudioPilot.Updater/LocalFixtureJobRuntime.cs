using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

internal sealed record FixtureJobContext(int Schema, string Kind, string DiagnosticId, string DataRoot, bool Visible, string Mode, MsiOptions? InstallerOptions = null, bool ExerciseAutoSave = false);

/// <summary>Sandbox-only adapter for the job controller. Real app admission, autosave, shutdown and recovery remain authoritative.</summary>
internal sealed class LocalFixtureJobRuntime(OwnedWorkspace workspace, FixtureJobContext context) : ILocalUpdateRuntime, IAsyncDisposable
{
    private SupervisedApp? _original;
    private bool _prepared;
    public string Context { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.Root + "|" + context.Kind + "|" + context.DiagnosticId + "|" + context.DataRoot + "|" + context.InstallerOptions?.ToProperties())));
    public bool RecoveryPending => new DeploymentRecovery(workspace).IsPending;
    private string CurrentApp => context.Kind == "portable" ? PortableLauncher.CurrentApp(workspace, new PortableDeployment(workspace).ReadState()) : Path.Combine((context.InstallerOptions ?? throw new InvalidDataException("Missing bound installer options.")).InstallFolder, "AudioPilot.exe");

    public UpdateSelection Inspect()
    {
        MsiDeployment.RequireSandbox();
        if (context.Schema != 1 || context.Kind is not ("portable" or "msi") || !Guid.TryParseExact(context.DiagnosticId, "N", out _)) throw new InvalidDataException("Invalid local job context.");
        if (context.Kind == "msi" && MsiOptions.ReadInstalled() != context.InstallerOptions) throw new InvalidDataException("The registered installation changed while this job was pending.");
        string app = CurrentApp;
        OwnedWorkspace.CheckAncestors(app);
        using var input = File.OpenRead(app);
        using var pe = new PEReader(input);
        string architecture = pe.PEHeaders.CoffHeader.Machine switch
        {
            System.Reflection.PortableExecutable.Machine.Amd64 => "win-x64",
            System.Reflection.PortableExecutable.Machine.I386 => "win-x86",
            System.Reflection.PortableExecutable.Machine.Arm64 => "win-arm64",
            _ => throw new InvalidDataException("Unsupported application architecture.")
        };
        string version = FileVersionInfo.GetVersionInfo(Path.Combine(Path.GetDirectoryName(app)!, "AudioPilot.dll")).ProductVersion?.Split('+')[0] ?? throw new InvalidDataException("Missing installed version.");
        _ = UpdateTrustMetadata.ParseVersion(version);
        string runtime = File.Exists(Path.Combine(Path.GetDirectoryName(app)!, "coreclr.dll")) ? "self-contained" : "framework-dependent";
        string helper = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "AudioPilot.Updater.dll")).ProductVersion!.Split('+')[0];
        return new(context.Kind, architecture, runtime, version, helper);
    }

    public async Task<UpdateJobPreparation> PrepareAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = Inspect();
        if (_original == null)
        {
            _original = await SupervisedApp.LaunchAsync(CurrentApp, context.DiagnosticId, true, context.Visible);
            await _original.WaitStagedAsync();
            await _original.CommitAsync();
            if (context.ExerciseAutoSave)
            {
                await _original.SendAsync("exercise-autosave");
                if ((await _original.ReadAsync()).Kind != "edited") throw new IOException("The final-save probe was not accepted.");
            }
        }
        token.ThrowIfCancellationRequested();
        _original.ResumeOperationDeadline();
        await _original.SendAsync("prepare");
        var reply = await _original.ReadAsync();
        _prepared = reply.Kind == "prepared";
        token.ThrowIfCancellationRequested();
        if (!_prepared && reply.Kind != "deferred") throw new IOException("Unexpected preparation acknowledgement.");
        return new(_prepared, reply.Reason);
    }

    public async Task CancelPreparationAsync(CancellationToken token)
    {
        if (_original == null || _original.Process.HasExited) return;
        _original.ResumeOperationDeadline();
        await _original.SendAsync("cancel");
        if ((await _original.ReadAsync()).Kind != "cancelled") throw new IOException("Preparation cancellation was not acknowledged.");
        _prepared = false;
        _original.SuspendOperationDeadline();
    }

    public async Task<bool> DeployAsync(VerifiedLocalUpdate package)
    {
        if (!_prepared || _original == null) throw new InvalidOperationException("The original app did not finish its save/microphone barrier.");
        string previous = CurrentApp;
        _ = Inspect();
        var options = context.InstallerOptions;
        new DeploymentRecovery(workspace).Begin(context.DiagnosticId, context.DataRoot, previous, context.Visible, options, CandidateOwner.Capture(_original.Process, previous));
        await _original.SendAsync("shutdown");
        if ((await _original.ReadAsync()).Kind != "stopped") throw new IOException("The original app did not drain.");
        await _original.Process.WaitForExitAsync(_original.Token);
        if (_original.Process.ExitCode != 0) throw new IOException("The original app did not exit normally.");
        await _original.DisposeAsync(); _original = null; _prepared = false;
        return await DeploymentLifecycleSmoke.DeployVerifiedAsync(context.Kind, package, workspace, context.Mode);
    }

    public async Task<bool> RecoverAsync()
    {
        var recovery = new DeploymentRecovery(workspace);
        if (_original != null && !_original.Process.HasExited)
        {
            await CancelPreparationAsync(CancellationToken.None);
            if (recovery.IsPending && recovery.Read().Candidate == null && !recovery.Read().DeploymentCommitted) recovery.Complete();
            return false;
        }
        if (!recovery.IsPending)
        {
            await CancelPreparationAsync(CancellationToken.None);
            string? expected = OwnedWorkspace.ReadJson<UpdateJobState>(workspace.Resolve("job.json")).Version;
            return expected != null && Inspect().InstalledVersion == expected;
        }
        if (recovery.Read().Original is { } original)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await original.WaitForExitAsync(deadline.Token);
        }
        if (context.Kind == "portable") await DeploymentLifecycleSmoke.RunStableLauncherProbeAsync(workspace);
        else await DeploymentLifecycleSmoke.RecoverMsiAsync(workspace);
        string? version = OwnedWorkspace.ReadJson<UpdateJobState>(workspace.Resolve("job.json")).Version;
        return version != null && Inspect().InstalledVersion == version;
    }

    public async ValueTask DisposeAsync()
    {
        if (_original != null)
        {
            var original = _original;
            try { await CancelPreparationAsync(CancellationToken.None); }
            finally
            {
                // Disconnect releases reversible admission. An original app is never killed to finish this job.
                await original.DisposeAsync(); _original = null;
            }
        }
    }
}

using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

internal sealed record InstalledUpdateBinding(int Schema, string Kind, string Root, string Identity, MsiOptions? Options, CandidateOwner? Owner = null, string? OwnerSession = null, string? Context = null);

/// <summary>Discovers a real installation and binds replacement to its authenticated runtime and installer options.</summary>
internal sealed class InstalledUpdateRuntime : ILocalUpdateRuntime, IAsyncDisposable
{
    private readonly OwnedWorkspace _workspace;
    private readonly InstalledUpdateBinding _binding;
    private SupervisedApp? _original;
    private UpdateRuntimeInfo? _facts;
    private bool _prepared;
    private bool _visible;
    internal bool IsPrepared => _prepared;
    internal Func<string, Task>? BeforeCandidateStart { get; set; }
    public string Context { get; }
    public string Kind => _binding.Kind;
    public bool RecoveryPending => new DeploymentRecovery(_workspace).IsPending;
    private string AppPath => _binding.Kind == "portable" ? PortableLauncher.CurrentApp(_workspace, new PortableDeployment(_workspace).ReadState()) : Path.Combine(_binding.Root, "AudioPilot.exe");

    public static InstalledUpdateRuntime Discover(OwnedWorkspace workspace, string application, CandidateOwner? owner = null, string? session = null)
    {
        MsiDeployment.RequireRuntime();
        application = Path.GetFullPath(application);
        OwnedWorkspace.CheckAncestors(application);
        if (owner != null && !owner.Path.Equals(application, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The supplied process does not own the selected application.");
        InstalledUpdateBinding binding;
        string manifest = workspace.Resolve(PortableLayout.FileName);
        if (File.Exists(manifest) && PortableLauncher.CurrentApp(workspace, new PortableDeployment(workspace).ReadState()).Equals(application, StringComparison.OrdinalIgnoreCase))
        {
            var layout = OwnedWorkspace.ReadJson<PortableLayoutDescriptor>(manifest);
            if (layout.Schema != 1 || !layout.Managed || !Guid.TryParseExact(layout.DiagnosticId, "N", out var id) || id == Guid.Empty) throw new InvalidDataException("Missing managed portable identity.");
            binding = new(1, "portable", workspace.Root, layout.DiagnosticId, null, owner);
        }
        else
        {
            var options = MsiOptions.ReadInstalled();
            if (!Path.Combine(options.InstallFolder, "AudioPilot.exe").Equals(application, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The executable is not the registered installation.");
            binding = new(1, "msi", options.InstallFolder, session ?? Guid.NewGuid().ToString("N"), options, owner);
        }
        if (File.Exists(workspace.Resolve("installation.json"))) throw new InvalidOperationException("This workspace is already bound to an installation.");
        var runtime = new InstalledUpdateRuntime(workspace, binding);
        _ = runtime.Inspect();
        OwnedWorkspace.WriteJson(workspace.Resolve("installation.json"), binding);
        if (binding.Kind == "msi") workspace.RecordInstallation(application);
        return runtime;
    }
    public static InstalledUpdateRuntime Open(OwnedWorkspace workspace) => new(workspace, OwnedWorkspace.ReadJson<InstalledUpdateBinding>(workspace.Resolve("installation.json")));
    internal static InstalledUpdateRuntime BindPilot(OwnedWorkspace workspace, string application, CandidateOwner owner, string session)
    {
        UpdateClientPolicy.RequireConfigured();
        using var gate = new FileStream(workspace.Resolve("job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!Guid.TryParseExact(session, "N", out var id) || id == Guid.Empty || !owner.Path.Equals(Path.GetFullPath(application), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid pilot process binding.");
        if (new DeploymentRecovery(workspace).IsPending)
            throw new InvalidOperationException("A previous update needs recovery. Close AudioPilot and reopen the existing updater workspace before starting another update.");
        var runtime = File.Exists(workspace.Resolve("installation.json")) ? Open(workspace) : Discover(workspace, application, owner);
        if (!runtime.AppPath.Equals(application, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The pilot workspace belongs to another installation.");
        var binding = RefreshBinding(workspace, runtime._binding, application,
            runtime.Kind == "msi" ? MsiOptions.ReadInstalled() : null) with
        { Owner = owner, OwnerSession = session };
        OwnedWorkspace.WriteJson(workspace.Resolve("installation.json"), binding);
        if (binding.Kind == "msi") workspace.RecordInstallation(application);
        return new(workspace, binding);
    }
    private InstalledUpdateRuntime(OwnedWorkspace workspace, InstalledUpdateBinding binding)
    {
        MsiDeployment.RequireRuntime();
        if (binding.Schema != 1 || binding.Kind is not ("portable" or "msi") || !Path.IsPathFullyQualified(binding.Root) || !Guid.TryParseExact(binding.Identity, "N", out _) ||
            binding.Context != null && !UpdateTrustMetadata.IsDigest(binding.Context)) throw new InvalidDataException("Invalid installation binding.");
        _workspace = workspace; _binding = binding;
        Context = ContextFor(binding);
    }

    private static string ContextFor(InstalledUpdateBinding binding) => binding.Context ??
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Root + "|" + binding.Kind + "|" + binding.Identity + "|" + binding.Options?.ToProperties())));

    internal static InstalledUpdateBinding RefreshBinding(OwnedWorkspace workspace, InstalledUpdateBinding previous, string application, MsiOptions? installed)
    {
        application = Path.GetFullPath(application);
        OwnedWorkspace.CheckAncestors(application);
        if (new DeploymentRecovery(workspace).IsPending || File.Exists(workspace.Resolve("job.json.new")) ||
            File.Exists(workspace.Resolve("job.json")) && OwnedWorkspace.ReadJson<UpdateJobState>(workspace.Resolve("job.json")).Phase is
                UpdateJobPhase.Installing or UpdateJobPhase.Preparing or UpdateJobPhase.Recovering or UpdateJobPhase.RecoveryRequired)
            throw new InvalidOperationException("Recover the interrupted update before rebinding its installation.");
        string root;
        if (previous.Kind == "portable")
        {
            var layout = OwnedWorkspace.ReadJson<PortableLayoutDescriptor>(workspace.Resolve(PortableLayout.FileName));
            var state = new PortableDeployment(workspace).ReadState();
            if (layout.Schema != 1 || !layout.Managed || layout.DiagnosticId != previous.Identity || state.PendingHealth ||
                !PortableLauncher.CurrentApp(workspace, state).Equals(application, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The portable workspace belongs to another installation or needs recovery.");
            root = workspace.Root;
        }
        else
        {
            if (previous.Kind != "msi" || installed == null ||
                !Path.Combine(previous.Root, "AudioPilot.exe").Equals(application, StringComparison.OrdinalIgnoreCase) ||
                !Path.Combine(installed.InstallFolder, "AudioPilot.exe").Equals(application, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The registered application belongs to another installation.");
            root = installed.InstallFolder;
        }
        // Job and trust floors retain their original installation identity across an idle relocation or Modify operation.
        return previous with { Root = root, Options = installed, Context = ContextFor(previous) };
    }
    public UpdateSelection Inspect()
    {
        if (_binding.Kind == "msi" && MsiOptions.ReadInstalled() != _binding.Options) throw new InvalidDataException("The registered installation changed.");
        if (_binding.Kind == "portable" && !_binding.Root.Equals(_workspace.Root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Portable binding moved.");
        string app = AppPath;
        OwnedWorkspace.CheckAncestors(app);
        using var input = File.OpenRead(app);
        using var pe = new PEReader(input);
        string architecture = pe.PEHeaders.CoffHeader.Machine switch
        {
            System.Reflection.PortableExecutable.Machine.Amd64 => "win-x64",
            System.Reflection.PortableExecutable.Machine.I386 => "win-x86",
            System.Reflection.PortableExecutable.Machine.Arm64 => "win-arm64",
            _ => throw new InvalidDataException("Unsupported architecture.")
        };
        string runtime = File.Exists(Path.Combine(Path.GetDirectoryName(app)!, "coreclr.dll")) ? "self-contained" : "framework-dependent";
        string helper = VersionOf(AppContext.BaseDirectory, "AudioPilot.Updater.Core.dll");
        return new(_binding.Kind, architecture, runtime, VersionOf(Path.GetDirectoryName(app)!, "AudioPilot.dll"), helper);
    }
    internal UpdateSelection InspectForSession()
    {
        var recovery = new DeploymentRecovery(_workspace);
        if (!recovery.IsPending) return Inspect();
        return ReadRecoveryInstallation(_workspace, _binding, VersionOf(AppContext.BaseDirectory, "AudioPilot.Updater.Core.dll"));
    }
    internal static UpdateSelection ReadRecoveryInstallation(OwnedWorkspace workspace, InstalledUpdateBinding binding, string helperVersion)
    {
        _ = UpdateTrustMetadata.ParseVersion(helperVersion);
        var record = new DeploymentRecovery(workspace).Read();
        if (binding.Schema != 1 || record.Completed || !record.Managed || record.DiagnosticId != binding.Identity ||
            record.PreviousInstallation is not { } previous || previous.Kind != binding.Kind)
            throw new InvalidDataException("Recovery lacks matching installation facts.");
        if (binding.Kind == "portable")
        {
            if (!binding.Root.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Portable binding moved.");
            var state = new PortableDeployment(workspace).ReadState();
            bool matches = new[] { state.Current, state.Previous }.Where(generation => generation != null).Any(generation =>
                workspace.Resolve("versions/" + generation + "/AudioPilot.exe").Equals(record.PreviousApp, StringComparison.OrdinalIgnoreCase));
            if (!matches) throw new InvalidDataException("The previous application is outside the recorded generations.");
        }
        else if (binding.Kind != "msi" || !Path.Combine(binding.Root, "AudioPilot.exe").Equals(record.PreviousApp, StringComparison.OrdinalIgnoreCase) || record.InstallerOptions != binding.Options)
            throw new InvalidDataException("Recovery belongs to another installation.");
        return previous with { HelperVersion = helperVersion };
    }
    private static string VersionOf(string directory, string file) => Version.TryParse(FileVersionInfo.GetVersionInfo(Path.Combine(directory, file)).FileVersion, out var version) && version.Build >= 0
        ? version.ToString(3) : throw new InvalidDataException("Missing numeric application version.");

    public async Task<UpdateJobPreparation> PrepareAsync(CancellationToken token)
    {
        _ = Inspect(); token.ThrowIfCancellationRequested();
        if (_binding.Kind == "msi")
        {
            try { using var availability = MsiDeployment.Reserve(); }
            catch (MsiUpdateException error) when (error.NativeErrorCode == 1618) { return new(false, error.Reason); }
            token.ThrowIfCancellationRequested();
        }
        if (_original == null)
        {
            CandidateOwner? owner = _binding.Owner;
            string ownerSession = _binding.OwnerSession ?? _binding.Identity;
            if (File.Exists(_workspace.Resolve("recovery.json")) && new DeploymentRecovery(_workspace).Read() is { Completed: true, Candidate: { } candidate })
            {
                bool previousExited = owner == null;
                if (owner != null)
                {
                    try { using var previous = Process.GetProcessById(owner.Pid); previousExited = previous.HasExited || previous.StartTime.ToUniversalTime().Ticks != owner.StartedUtcTicks; }
                    catch (ArgumentException) { previousExited = true; }
                }
                if (previousExited) { owner = candidate; ownerSession = _binding.Identity; }
            }
            if (owner != null)
            {
                if (!owner.Path.Equals(AppPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The original process belongs to another installation.");
                using var probe = Process.GetProcessById(owner.Pid);
                if (probe.HasExited || probe.StartTime.ToUniversalTime().Ticks != owner.StartedUtcTicks) throw new InvalidDataException("The selected application exited. Rebind explicitly before restarting it.");
                _original = await SupervisedApp.ConnectAsync(owner, ownerSession);
            }
            else
            {
                _original = await SupervisedApp.LaunchAsync(AppPath, _binding.Identity, true, false, managed: true);
                await _original.WaitStagedAsync(); await _original.CommitAsync();
            }
        }
        _original.ResumeOperationDeadline();
        await _original.SendAsync("prepare");
        var reply = await _original.ReadAsync();
        _prepared = reply.Kind == "prepared";
        _visible = reply.Visible;
        if (!_prepared && reply.Kind != "deferred") throw new IOException("Unexpected preparation acknowledgement.");
        if (!_prepared) return new(false, reply.Reason);
        await _original.SendAsync("inspect");
        reply = await _original.ReadAsync();
        _facts = reply.Kind == "inspected" ? reply.Runtime : null;
        if (_facts == null || !Path.TrimEndingDirectorySeparator(_facts.ApplicationDirectory).Equals(Path.GetDirectoryName(AppPath), StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(_facts.SettingsPath) || Path.GetFileName(_facts.SettingsPath) != "settings.json" || !Path.IsPathFullyQualified(_facts.LocalStateRoot))
            throw new InvalidDataException("Runtime locations do not match the authenticated application.");
        token.ThrowIfCancellationRequested();
        UpdateLog.Write(_workspace.Diagnostics, new(UpdateLogKind.AppPrepared));
        return new(true);
    }
    public async Task CancelPreparationAsync(CancellationToken token)
    {
        if (_original == null || _original.Process.HasExited) return;
        _original.ResumeOperationDeadline(); await _original.SendAsync("cancel");
        if ((await _original.ReadAsync()).Kind != "cancelled") throw new IOException("Preparation cancellation was not acknowledged.");
        _prepared = false; _original.SuspendOperationDeadline();
    }
    internal async Task ExerciseAutoSaveAsync()
    {
        if (_original == null) throw new InvalidOperationException("No authenticated app.");
        _original.ResumeOperationDeadline(); await _original.SendAsync("exercise-autosave");
        if ((await _original.ReadAsync()).Kind != "edited") throw new IOException("The pending edit was not accepted.");
        _original.SuspendOperationDeadline();
    }
    public async Task<bool> DeployAsync(VerifiedLocalUpdate package)
    {
        using var lifecycle = new FileStream(_workspace.Resolve("lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!_prepared || _original == null || _facts == null) throw new InvalidOperationException("The app has not passed its save/microphone barrier.");
        await package.PrepareForDeploymentAsync(CancellationToken.None);
        var before = Inspect(); string originalPath = AppPath;
        using var installer = ReserveInstaller();
        var recovery = new DeploymentRecovery(_workspace);
        recovery.Begin(_binding.Identity, Path.GetDirectoryName(_facts.SettingsPath)!, originalPath, _visible, _binding.Options,
            CandidateOwner.Capture(_original.Process, originalPath), managed: true, previousVersion: before.InstalledVersion,
            targetVersion: package.Authorization.Release.Version, localStateRoot: _facts.LocalStateRoot, previousInstallation: before);
        await _original.StopNormallyAsync(); await _original.DisposeAsync(); _original = null; _prepared = false;
        SupervisedApp? candidate = null;
        async Task<bool> Health(string folder)
        {
            if (BeforeCandidateStart != null) await BeforeCandidateStart(folder);
            if (VersionOf(folder, "AudioPilot.dll") != package.Authorization.Release.Version) throw new InvalidDataException("Candidate version does not match its signed target.");
            try
            {
                candidate = await SupervisedApp.LaunchAsync(Path.Combine(folder, "AudioPilot.exe"), _binding.Identity, true, _visible, recovery.SetCandidate, managed: true);
                await candidate.WaitStagedAsync();
                UpdateLog.Write(_workspace.Diagnostics, new(UpdateLogKind.AppRestarted, Version: package.Authorization.Release.Version));
                return true;
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                UpdateLog.Write(_workspace.Diagnostics, new(UpdateLogKind.CandidateFailed, Error: error));
                return false;
            }
        }
        async Task Abort() { if (candidate != null) await candidate.AbortAsync(); }
        try
        {
            bool committed;
            if (_binding.Kind == "portable") committed = (await new PortableDeployment(_workspace).ApplyAsync(package, (folder, _) => Health(folder), CancellationToken.None, stopCandidateBeforeRollback: Abort)).Committed;
            else committed = MsiDeployment.Apply(package, _binding.Options!, () => Health(_binding.Root).GetAwaiter().GetResult(), _workspace.Resolve("install.log"), () => Abort().GetAwaiter().GetResult(), installer);
            if (committed)
            {
                recovery.MarkCommitted(); await candidate!.CommitAsync(); recovery.Complete();
                await DetachAsync(candidate);
            }
            return committed;
        }
        finally { if (candidate != null) await candidate.DisposeAsync(); }
    }
    private MsiTransaction? ReserveInstaller()
    {
        if (_binding.Kind != "msi") return null;
        try { return MsiDeployment.Reserve(); }
        catch (MsiUpdateException error) when (error.NativeErrorCode == 1618) { throw new UpdateDeploymentDeferredException(error.Reason); }
    }
    public async Task<bool> RecoverAsync()
    {
        using var lifecycle = new FileStream(_workspace.Resolve("lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var recovery = new DeploymentRecovery(_workspace);
        if (_original != null && !_original.Process.HasExited)
        {
            await CancelPreparationAsync(CancellationToken.None);
            if (recovery.IsPending)
            {
                var pending = recovery.Read();
                if (!pending.Managed || pending.DiagnosticId != _binding.Identity || pending.Candidate != null || pending.DeploymentCommitted)
                    throw new InvalidDataException("Recovery cannot settle while another deployment may be active.");
                recovery.Complete();
            }
            return false;
        }
        if (!recovery.IsPending)
        {
            string expected = (File.Exists(_workspace.Resolve("job.json")) ? OwnedWorkspace.ReadJson<UpdateJobState>(_workspace.Resolve("job.json")).Version : null)
                ?? throw new InvalidDataException("Recovery has no authenticated job target.");
            return Inspect().InstalledVersion == expected;
        }
        var record = recovery.Read();
        if (!record.Managed || record.DiagnosticId != _binding.Identity) throw new InvalidDataException("Recovery belongs to another installation.");
        if (record.Original != null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await record.Original.WaitForExitAsync(timeout.Token); }
        await recovery.WaitForCandidateExitAsync();
        if (_binding.Kind == "portable") _ = new PortableDeployment(_workspace).Recover();
        else { using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await MsiDeployment.WaitForInstallerIdleAsync(timeout.Token); }
        string version = Inspect().InstalledVersion;
        bool committed = version == record.TargetVersion;
        if (!committed && (version != record.PreviousVersion || record.DeploymentCommitted)) throw new InvalidDataException("Recovery does not match either authorized version.");
        if (!committed) recovery.RestoreConfiguration(record.DataRoot);
        await using var app = await SupervisedApp.LaunchAsync(AppPath, _binding.Identity, true, record.Visible, recovery.SetCandidate, managed: true);
        await app.WaitStagedAsync(); await app.CommitAsync(); recovery.Complete(); await DetachAsync(app);
        UpdateLog.Write(_workspace.Diagnostics, new(UpdateLogKind.RecoveryCompleted, Version: version));
        return committed;
    }
    private static async Task DetachAsync(SupervisedApp app)
    {
        app.ResumeOperationDeadline();
        await app.SendAsync("detach");
        if ((await app.ReadAsync()).Kind != "detached") throw new IOException("Restarted app did not release its update channel.");
    }
    internal async Task StopForSandboxCleanupAsync()
    {
        MsiDeployment.RequireSandbox();
        if (!(await PrepareAsync(CancellationToken.None)).Ready) throw new InvalidOperationException("The app cannot stop safely for Sandbox cleanup.");
        await _original!.StopNormallyAsync(); await _original.DisposeAsync(); _original = null; _prepared = false;
        if (_binding.Kind == "msi") Program.RunMsi("/x", MsiDeployment.GetSingleInstalledProduct(_binding.Root), "REBOOT=ReallySuppress", _workspace.Resolve("uninstall.log"));
    }
    public async ValueTask DisposeAsync()
    {
        if (_original == null) return;
        var app = _original;
        try { await CancelPreparationAsync(CancellationToken.None); await DetachAsync(app); }
        finally { await app.DisposeAsync(); _original = null; }
    }
    public IReadOnlyCollection<string>? ReadMsiRepairSources() => _binding.Kind == "msi" ? MsiDeployment.ReadRepairSourceDirectories() : null;
}

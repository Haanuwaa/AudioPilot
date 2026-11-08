using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

/// <summary>Local-package, real-app replacement proofs. All installer operations and launches require a disposable guest.</summary>
internal static class DeploymentLifecycleSmoke
{
    public static async Task PortableAsync(string baseline, string candidate, string fixture, OwnedWorkspace workspace)
    {
        MsiDeployment.RequireSandbox();
        string id = Guid.NewGuid().ToString("N");
        OwnedWorkspace.WriteJson(workspace.Resolve(PortableLayout.FileName), new PortableLayoutDescriptor(1, id));
        string data = workspace.Resolve("data");
        await RealAppLifecycleSmoke.PrepareSettingsAsync(fixture, data);
        var deployment = new PortableDeployment(workspace);
        await deployment.ApplyAsync(baseline, new FileInfo(baseline).Length, Hash(baseline), "1.0.0", (_, _) => Task.FromResult(true), CancellationToken.None);
        InstallLauncher(workspace);
        string original = PortableLauncher.CurrentApp(workspace, deployment.ReadState());
        string trustPin = await TufLocalTrustClient.CreateFixtureAsync(workspace, candidate, "portable", CancellationToken.None);
        using var preflight = await new TufLocalTrustClient(workspace, trustPin).OpenPackageAsync(candidate, new("portable", "win-x64", "self-contained", "1.0.0", "1.0.0"), CancellationToken.None);
        bool visible = await ExerciseOriginalAsync(original, id, data);
        string saved = File.ReadAllText(Path.Combine(data, "settings.json"));
        string placement = File.ReadAllText(Path.Combine(data, "window-placement.json"));
        var results = new List<object>();
        foreach (string mode in new[] { "startup-failure", "reject", "interrupt", "commit" })
        {
            var recovery = new DeploymentRecovery(workspace);
            recovery.Begin(id, data, original, visible);
            bool committed;
            if (mode == "interrupt")
            {
                await InterruptHelperAsync("portable", candidate, workspace, trustPin);
                Require(deployment.ReadState().PendingHealth, "Interruption did not retain a pending portable pointer.");
                committed = false;
            }
            else committed = await AttemptAsync("portable", candidate, workspace, mode, trustPin);
            if (!committed)
            {
                await RunStableLauncherProbeAsync(workspace);
                Require(deployment.ReadState().Current == Path.GetFileName(Path.GetDirectoryName(original)), "Portable rollback changed the previous generation.");
            }
            Require(committed == (mode == "commit"), "Portable deployment did not produce its expected outcome.");
            Require(File.ReadAllText(Path.Combine(data, "settings.json")) == saved, "Portable replacement lost saved configuration.");
            Require(File.ReadAllText(Path.Combine(data, "window-placement.json")) == placement, "Portable replacement changed window placement.");
            Require(!recovery.IsPending, "Portable recovery remained incomplete.");
            Require(!Directory.EnumerateFiles(Path.Combine(workspace.Root, "versions"), "settings.json", SearchOption.AllDirectories).Any(), "Portable data leaked into a payload generation.");
            results.Add(new { mode, committed, settingsPreserved = true, placementPreserved = true, stableLauncher = true, candidateExitedBeforeRecovery = true });
            OwnedWorkspace.WriteJson(workspace.Resolve("deployment-results.json"), results);
        }
        Require(VersionIs(Path.GetDirectoryName(PortableLauncher.CurrentApp(workspace, deployment.ReadState()))!, "1.0.1"), "Portable candidate version was not committed.");
        await RunStableLauncherProbeAsync(workspace);
    }

    public static async Task MsiAsync(string baseline, string candidate, string fixture, OwnedWorkspace workspace, int[] masks, bool startupFailureOnly = false)
    {
        MsiDeployment.RequireSandbox();
        var results = new List<object>();
        foreach (int mask in masks)
        {
            var operation = new OwnedWorkspace(workspace.Resolve("option-" + mask));
            string id = Guid.NewGuid().ToString("N");
            string data = Path.Combine(Path.GetTempPath(), "AudioPilot.Diagnostics", id);
            var options = new MsiOptions(operation.Resolve("application with spaces"), data,
                (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
            Program.RunMsi("/i", baseline, options.ToProperties(), operation.Resolve("baseline.log"));
            try
            {
                Program.VerifyOptions(options);
                await RealAppLifecycleSmoke.PrepareSettingsAsync(fixture, data);
                string original = Path.Combine(options.InstallFolder, "AudioPilot.exe");
                string trustPin = await TufLocalTrustClient.CreateFixtureAsync(operation, candidate, "msi", CancellationToken.None);
                using var preflight = await new TufLocalTrustClient(operation, trustPin).OpenPackageAsync(candidate, new("msi", "win-x64", "self-contained", "1.0.0", "1.0.0"), CancellationToken.None);
                bool visible = await ExerciseOriginalAsync(original, id, data);
                string saved = File.ReadAllText(Path.Combine(data, "settings.json"));
                string placement = File.ReadAllText(Path.Combine(data, "window-placement.json"));
                string[] modes = startupFailureOnly ? ["startup-failure"] : ["reject", "interrupt", "commit"];
                foreach (string mode in modes)
                {
                    var recovery = new DeploymentRecovery(operation);
                    recovery.Begin(id, data, original, visible, options);
                    bool committed;
                    if (mode == "interrupt")
                    {
                        await InterruptHelperAsync("msi", candidate, operation, trustPin);
                        await recovery.WaitForCandidateExitAsync();
                        committed = false;
                    }
                    else committed = await AttemptAsync("msi", candidate, operation, mode, trustPin);
                    if (!committed) await RecoverMsiAsync(operation);
                    Require(committed == (mode == "commit"), "MSI deployment did not produce its expected outcome.");
                    Program.VerifyOptions(options);
                    Require(File.ReadAllText(Path.Combine(data, "settings.json")) == saved, "MSI replacement lost saved configuration.");
                    Require(File.ReadAllText(Path.Combine(data, "window-placement.json")) == placement, "MSI replacement changed window placement.");
                    Require(VersionIs(options.InstallFolder, committed ? "1.0.1" : "1.0.0"), "MSI deployment version differs from its outcome.");
                    Require(!recovery.IsPending, "MSI recovery remained incomplete.");
                    results.Add(new { mask, mode, committed, optionsPreserved = true, settingsPreserved = true, placementPreserved = true, realAppHealth = true, candidateExitedBeforeRecovery = true });
                    OwnedWorkspace.WriteJson(workspace.Resolve("deployment-results.json"), results);
                }
            }
            finally
            {
                Program.RunMsi("/x", MsiDeployment.GetSingleInstalledProduct(options.InstallFolder), "REBOOT=ReallySuppress", operation.Resolve("uninstall.log"));
            }
        }
    }

    public static async Task<bool> AttemptAsync(string kind, string package, OwnedWorkspace workspace, string mode, string trustPin)
    {
        MsiDeployment.RequireSandbox();
        using var verifiedPackage = await new TufLocalTrustClient(workspace, trustPin).OpenPackageAsync(package, new(kind, "win-x64", "self-contained", "1.0.0", "1.0.0"), CancellationToken.None);
        return await DeployVerifiedAsync(kind, verifiedPackage, workspace, mode);
    }

    internal static async Task<bool> DeployVerifiedAsync(string kind, VerifiedLocalUpdate verifiedPackage, OwnedWorkspace workspace, string mode)
    {
        MsiDeployment.RequireSandbox();
        using var ownership = new FileStream(workspace.Resolve("lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var recovery = new DeploymentRecovery(workspace);
        var record = recovery.Read();
        SupervisedApp? candidate = null;
        async Task<bool> Health(string folder)
        {
            Require(VersionIs(folder, "1.0.1"), "The local candidate is not version 1.0.1.");
            if (mode == "startup-failure")
            {
                File.WriteAllText(Path.Combine(record.DataRoot, "settings.json"), "{ injected incomplete migration");
            }
            try
            {
                candidate = await SupervisedApp.LaunchAsync(Path.Combine(folder, "AudioPilot.exe"), record.DiagnosticId, true, record.Visible, recovery.SetCandidate);
                await candidate.WaitStagedAsync();
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                Console.WriteLine("candidate-startup-unavailable | error=" + error.GetType().Name);
                return false;
            }
            Require(mode != "startup-failure", "The incompatible configuration unexpectedly passed startup validation.");
            if (mode != "commit")
            {
                // Simulate a failed candidate's future configuration migration after the durable backup exists.
                File.WriteAllText(Path.Combine(record.DataRoot, "settings.json"), "injected incompatible candidate configuration");
                File.WriteAllText(Path.Combine(record.DataRoot, "window-placement.json"), "injected candidate placement");
                Directory.CreateDirectory(Path.Combine(record.DataRoot, "backups"));
                File.WriteAllText(Path.Combine(record.DataRoot, "backups", "settings.json.candidate"), "injected migration backup");
            }
            if (mode == "interrupt") Environment.Exit(86);
            return mode == "commit";
        }
        async Task StopCandidate()
        {
            if (candidate != null) await candidate.AbortAsync();
        }
        try
        {
            bool committed;
            if (kind == "portable")
            {
                var result = await new PortableDeployment(workspace).ApplyAsync(verifiedPackage,
                    (folder, _) => Health(folder), CancellationToken.None, stopCandidateBeforeRollback: StopCandidate);
                committed = result.Committed;
            }
            else if (kind == "msi")
            {
                var options = record.InstallerOptions ?? throw new InvalidDataException("Missing bound installer options.");
                Program.VerifyOptions(options);
                committed = MsiDeployment.Apply(verifiedPackage, options, () => Health(options.InstallFolder).GetAwaiter().GetResult(),
                    workspace.Resolve(mode + ".log"), () => StopCandidate().GetAwaiter().GetResult());
            }
            else throw new InvalidDataException("Unknown deployment kind.");
            if (committed)
            {
                recovery.MarkCommitted();
                await candidate!.CommitAsync();
                recovery.Complete();
                await candidate.StopNormallyAsync();
            }
            else Require(candidate == null || candidate.Process.HasExited, "Rollback returned while the candidate still ran.");
            return committed;
        }
        finally { if (candidate != null) await candidate.DisposeAsync(); }
    }

    public static async Task RecoverMsiAsync(OwnedWorkspace workspace)
    {
        MsiDeployment.RequireSandbox();
        var recovery = new DeploymentRecovery(workspace);
        var record = recovery.Read();
        var options = record.InstallerOptions ?? throw new InvalidDataException("MSI recovery options are missing.");
        await recovery.WaitForCandidateExitAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await MsiDeployment.WaitForInstallerIdleAsync(timeout.Token);
        Program.VerifyOptions(options);
        bool rolledBack = VersionIs(options.InstallFolder, "1.0.0");
        Require(rolledBack ? !record.DeploymentCommitted : VersionIs(options.InstallFolder, "1.0.1"), "MSI recovery has an unexpected installed version.");
        if (rolledBack)
        {
            recovery.RestoreConfiguration(options.DataFolder);
            Require(!File.Exists(Path.Combine(options.DataFolder, "backups", "settings.json.candidate")), "Candidate-only settings backup survived rollback.");
        }
        await using var previous = await SupervisedApp.LaunchAsync(record.PreviousApp, record.DiagnosticId, true, record.Visible, recovery.SetCandidate);
        await previous.WaitStagedAsync();
        await previous.CommitAsync();
        recovery.Complete();
        await previous.StopNormallyAsync();
    }

    private static async Task<bool> ExerciseOriginalAsync(string app, string id, string data)
    {
        await using var original = await SupervisedApp.LaunchAsync(app, id, false, false);
        var ready = await original.ReadAsync();
        Require(ready.Kind == "ready", "The baseline did not become ready.");
        await original.SendAsync("exercise-autosave");
        Require((await original.ReadAsync()).Kind == "edited", "The baseline edit was not accepted.");
        await original.StopNormallyAsync();
        Require(JsonNode.Parse(File.ReadAllText(Path.Combine(data, "settings.json")))?["Theme"]?.GetValue<string>() == "Light", "The final baseline autosave was lost.");
        Require(File.Exists(Path.Combine(data, "window-placement.json")), "The baseline did not save its real window placement.");
        return ready.Visible;
    }

    private static async Task InterruptHelperAsync(string kind, string package, OwnedWorkspace workspace, string trustPin)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AudioPilot.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "deployment-interrupt", kind, package, workspace.Root, trustPin }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Interruption helper did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await child.WaitForExitAsync(timeout.Token);
        Require(child.ExitCode == 86, "The helper did not stop at the injected interruption point.");
    }

    internal static void InstallLauncher(OwnedWorkspace workspace)
    {
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, workspace.Resolve(Path.GetFileName(file)), overwrite: false);
        File.Copy(workspace.Resolve("AudioPilot.Updater.exe"), workspace.Resolve("AudioPilot.exe"));
    }

    internal static async Task RunStableLauncherProbeAsync(OwnedWorkspace workspace)
    {
        var start = new ProcessStartInfo(workspace.Resolve("AudioPilot.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("portable-launch-probe");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Stable launcher did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await child.WaitForExitAsync(timeout.Token);
        Require(child.ExitCode == 0, "Stable launcher recovery failed.");
    }

    private static bool VersionIs(string folder, string version) => File.Exists(Path.Combine(folder, "AudioPilot.dll")) &&
        FileVersionInfo.GetVersionInfo(Path.Combine(folder, "AudioPilot.dll")).ProductVersion?.Split('+')[0] == version;
    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

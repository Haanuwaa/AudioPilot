using System.Diagnostics;
using System.Text.Json.Nodes;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater.Validation;

internal sealed record FixtureJobLaunch(FixtureJobContext Context, string BootstrapHash);

/// <summary>Local-package controller proof using the real app and installer; never runs on the host desktop.</summary>
internal static class LocalJobSmoke
{
    public static async Task RunAsync(string kind, string baseline, string candidate, string fixture, OwnedWorkspace workspace, int optionMask = 7)
    {
        MsiDeployment.RequireSandbox();
        if (optionMask is < 0 or > 7) throw new InvalidDataException("Select an installer option mask from 0 to 7.");
        string id = Guid.NewGuid().ToString("N");
        string data = kind == "portable" ? workspace.Resolve("data") : Path.Combine(Path.GetTempPath(), "AudioPilot.Diagnostics", id);
        MsiOptions? options = null;
        if (kind == "portable")
        {
            OwnedWorkspace.WriteJson(workspace.Resolve(PortableLayout.FileName), new PortableLayoutDescriptor(1, id));
            await new PortableDeployment(workspace).ApplyAsync(baseline, new FileInfo(baseline).Length, Hash(baseline), "1.0.0", (_, _) => Task.FromResult(true), CancellationToken.None);
            DeploymentLifecycleSmoke.InstallLauncher(workspace);
        }
        else if (kind == "msi")
        {
            options = new(workspace.Resolve("application with spaces"), data, (optionMask & 1) != 0, (optionMask & 2) != 0, (optionMask & 4) != 0);
            Program.RunMsi("/i", baseline, options.ToProperties(), workspace.Resolve("baseline.log"));
        }
        else throw new InvalidDataException("Unknown job deployment type.");
        var results = new List<object>();
        try
        {
            await RealAppLifecycleSmoke.PrepareSettingsAsync(fixture, data);
            OwnedWorkspace.WriteJson(Path.Combine(data, "window-placement.json"), new { Left = 60, Top = 60, Right = 522, Bottom = 500, Dpi = 96u, Maximized = false });
            string expectedPlacement = File.ReadAllText(Path.Combine(data, "window-placement.json"));
            string pin = await TufLocalTrustClient.CreateFixtureAsync(workspace, candidate, kind, CancellationToken.None);
            bool first = true;
            foreach (string mode in new[] { "startup-failure", "interrupt", "commit" })
            {
                string expectedTheme = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "settings.json")))?["Theme"]?.GetValue<string>() == "Dark" ? "Light" : "Dark";
                var context = new FixtureJobContext(1, kind, id, data, false, mode, options, ExerciseAutoSave: true);
                OwnedWorkspace.WriteJson(workspace.Resolve("job-fixture.json"), new FixtureJobLaunch(context, pin));
                if (first)
                {
                    var chunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using var pauseRuntime = new LocalFixtureJobRuntime(workspace, context);
                    await using var pausing = new LocalUpdateJob(workspace, new TufLocalTrustClient(workspace, pin), pauseRuntime,
                        async (_, token) => { chunk.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
                    var staging = pausing.StageAsync(candidate);
                    await chunk.Task.WaitAsync(TimeSpan.FromSeconds(60));
                    Require(pausing.Pause(), "Local staging did not accept pause.");
                    Require((await staging).Phase == UpdateJobPhase.Paused && pausing.Snapshot.Bytes > 0, "Paused staging was not retained.");
                    first = false;
                }
                UpdateJobState result;
                await using (var runtime = new LocalFixtureJobRuntime(workspace, context))
                await using (var job = new LocalUpdateJob(workspace, new TufLocalTrustClient(workspace, pin), runtime))
                {
                    Require((await job.StageAsync(candidate)).Phase == UpdateJobPhase.Ready, "The local package did not reach Ready.");
                    if (mode != "interrupt") result = await job.ApplyAsync();
                    else result = job.Snapshot;
                }
                if (mode == "interrupt")
                {
                    var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AudioPilot.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("job-apply"); start.ArgumentList.Add(workspace.Root);
                    using var child = Process.Start(start) ?? throw new IOException("The interruption worker did not start.");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                    await child.WaitForExitAsync(deadline.Token);
                    Require(child.ExitCode == 86, "The interruption worker did not reach the staged candidate.");
                    await using var runtime = new LocalFixtureJobRuntime(workspace, context);
                    await using var recovery = new LocalUpdateJob(workspace, new TufLocalTrustClient(workspace, pin), runtime);
                    Require(recovery.Snapshot.Phase == UpdateJobPhase.RecoveryRequired, "The interrupted controller did not require recovery.");
                    result = await recovery.RecoverAsync();
                }
                Require(result.Phase == (mode == "commit" ? UpdateJobPhase.Completed : UpdateJobPhase.RolledBack), "Unexpected job result: " + result.Phase + "/" + result.Reason);
                Require(JsonNode.Parse(File.ReadAllText(Path.Combine(data, "settings.json")))?["Theme"]?.GetValue<string>() == expectedTheme, "The final original autosave was lost.");
                Require(File.ReadAllText(Path.Combine(data, "window-placement.json")) == expectedPlacement, "Window placement changed.");
                Require(!new DeploymentRecovery(workspace).IsPending, "Recovery remained pending.");
                if (options != null) Program.VerifyOptions(options);
                results.Add(new { kind, mode, phase = result.Phase.ToString(), finalSavePreserved = true, placementPreserved = true, optionsPreserved = true, pauseResumedAcrossController = true });
                OwnedWorkspace.WriteJson(workspace.Resolve("job-results.json"), results);
            }
        }
        finally { if (options != null) Program.RunMsi("/x", MsiDeployment.GetSingleInstalledProduct(options.InstallFolder), "REBOOT=ReallySuppress", workspace.Resolve("uninstall.log")); }
    }

    public static async Task ApplyAsync(OwnedWorkspace workspace)
    {
        MsiDeployment.RequireSandbox();
        var fixture = OwnedWorkspace.ReadJson<FixtureJobLaunch>(workspace.Resolve("job-fixture.json"));
        await using var runtime = new LocalFixtureJobRuntime(workspace, fixture.Context);
        await using var job = new LocalUpdateJob(workspace, new TufLocalTrustClient(workspace, fixture.BootstrapHash), runtime);
        var result = await job.ApplyAsync();
        Require(result.Phase == UpdateJobPhase.Completed, "The controller did not commit: " + result.Reason);
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

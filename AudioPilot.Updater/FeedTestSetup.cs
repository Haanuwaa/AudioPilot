using System.Diagnostics;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

internal sealed record FeedJobLaunch(int Schema, string BootstrapHash, HttpsTestFeed Feed);
internal sealed record StagingJobLaunch(int Schema, string Target);

/// <summary>Sandbox test setup only. The runtime adapter derives its deployment identity from the installed application.</summary>
internal static class FeedTestSetup
{
    public static async Task PrepareAsync(string baseline, string candidate, string settings, OwnedWorkspace workspace, string? stagingBootstrap = null)
    {
        MsiDeployment.RequireSandbox();
        if (File.Exists(workspace.Resolve("installation.json"))) throw new InvalidOperationException("Use a fresh installation workspace.");
        string identity = Guid.NewGuid().ToString("N"), app, data;
        if (Path.GetExtension(baseline) == ".zip")
        {
            OwnedWorkspace.WriteJson(workspace.Resolve(PortableLayout.FileName), new PortableLayoutDescriptor(1, identity, Managed: true));
            using var input = File.OpenRead(baseline);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
            await new PortableDeployment(workspace).ApplyAsync(baseline, input.Length, hash, "1.0.0", (_, _) => Task.FromResult(true), CancellationToken.None);
            DeploymentLifecycleSmoke.InstallLauncher(workspace);
            app = PortableLauncher.CurrentApp(workspace, new PortableDeployment(workspace).ReadState()); data = workspace.Resolve("data");
        }
        else if (Path.GetExtension(baseline) == ".msi")
        {
            var options = new MsiOptions(workspace.Resolve("application with spaces"), workspace.Resolve("configuration"), true, true, true);
            Program.RunMsi("/i", baseline, options.ToProperties(), workspace.Resolve("baseline.log"));
            app = Path.Combine(options.InstallFolder, "AudioPilot.exe"); data = options.DataFolder;
        }
        else throw new InvalidDataException("Select a ZIP or MSI baseline.");
        await RealAppLifecycleSmoke.PrepareSettingsAsync(settings, data);
        string pin;
        if (stagingBootstrap == null) pin = await TufLocalTrustClient.CreateFixtureAsync(workspace, candidate, Path.GetExtension(baseline) == ".zip" ? "portable" : "msi", CancellationToken.None);
        else
        {
            OwnedWorkspace.CheckAncestors(stagingBootstrap);
            if (new FileInfo(stagingBootstrap).Length > 65536) throw new InvalidDataException("Oversized staging bootstrap.");
            byte[] root = await File.ReadAllBytesAsync(stagingBootstrap);
            pin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(root));
            if (pin != HttpsStagingFeed.BootstrapHash) throw new InvalidDataException("Staging bootstrap differs from its independent pin.");
            Directory.CreateDirectory(workspace.Resolve("tuf-repository"));
            await File.WriteAllBytesAsync(workspace.Resolve("tuf-repository/bootstrap.root.json"), root);
            OwnedWorkspace.WriteJson(workspace.Resolve("staging.json"), new StagingJobLaunch(1, candidate));
        }
        OwnedWorkspace.WriteJson(workspace.Resolve("feed-bootstrap.json"), pin);
        var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
        start.Environment.Remove("AUDIOPILOT_DIAGNOSTIC_SESSION"); start.Environment.Remove(UpdateLifecycleWire.EnvironmentVariable);
        start.Environment["AUDIOPILOT_UPDATE_TEST_SESSION"] = identity;
        using var process = Process.Start(start) ?? throw new IOException("The baseline app could not start.");
        await using var runtime = InstalledUpdateRuntime.Discover(workspace, app, CandidateOwner.Capture(process, app), identity);
        _ = runtime.Inspect();
    }
}

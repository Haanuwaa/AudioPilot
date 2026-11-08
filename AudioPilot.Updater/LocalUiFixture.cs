using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

/// <summary>Prepares an isolated baseline and public local trust metadata, without starting an update.</summary>
internal static class LocalUiFixture
{
    public static void CleanupMsi(OwnedWorkspace workspace)
    {
        MsiDeployment.RequireSandbox();
        var launch = OwnedWorkspace.ReadJson<FixtureJobLaunch>(workspace.Resolve("job-fixture.json"));
        var options = launch.Context.InstallerOptions ?? throw new InvalidDataException("Missing fixture installer options.");
        if (launch.Context.Schema != 1 || launch.Context.Kind != "msi" ||
            !options.InstallFolder.Equals(workspace.Resolve("application with spaces"), StringComparison.OrdinalIgnoreCase) ||
            options.DataFolder != launch.Context.DataRoot || MsiOptions.ReadInstalled() != options)
            throw new InvalidDataException("The installed product does not belong to this UI fixture.");
        Program.RunMsi("/x", MsiDeployment.GetSingleInstalledProduct(options.InstallFolder), "REBOOT=ReallySuppress", workspace.Resolve("uninstall.log"));
    }

    public static async Task PrepareAsync(string kind, string baseline, string candidate, string settings, OwnedWorkspace workspace, int optionsMask)
    {
        MsiDeployment.RequireSandbox();
        if (optionsMask is < 0 or > 7) throw new InvalidDataException("Select installer options from 0 to 7.");
        if (File.Exists(workspace.Resolve("job-fixture.json"))) throw new InvalidOperationException("Use a fresh UI fixture workspace.");
        string id = Guid.NewGuid().ToString("N");
        string data = kind == "portable" ? workspace.Resolve("data") : Path.Combine(Path.GetTempPath(), "AudioPilot.Diagnostics", id);
        MsiOptions? options = null;
        if (kind == "portable")
        {
            OwnedWorkspace.WriteJson(workspace.Resolve(PortableLayout.FileName), new PortableLayoutDescriptor(1, id));
            using var file = File.OpenRead(baseline);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
            await new PortableDeployment(workspace).ApplyAsync(baseline, file.Length, hash, "1.0.0", (_, _) => Task.FromResult(true), CancellationToken.None);
            DeploymentLifecycleSmoke.InstallLauncher(workspace);
        }
        else if (kind == "msi")
        {
            options = new(workspace.Resolve("application with spaces"), data, (optionsMask & 1) != 0, (optionsMask & 2) != 0, (optionsMask & 4) != 0);
            Program.RunMsi("/i", baseline, options.ToProperties(), workspace.Resolve("baseline.log"));
        }
        else throw new InvalidDataException("Select a portable or MSI fixture.");
        try
        {
            await RealAppLifecycleSmoke.PrepareSettingsAsync(settings, data);
            OwnedWorkspace.WriteJson(Path.Combine(data, "window-placement.json"), new { Left = 60, Top = 60, Right = 522, Bottom = 500, Dpi = 96u, Maximized = false });
            string pin = await TufLocalTrustClient.CreateFixtureAsync(workspace, candidate, kind, CancellationToken.None);
            OwnedWorkspace.WriteJson(workspace.Resolve("job-fixture.json"), new FixtureJobLaunch(new(1, kind, id, data, false, "commit", options), pin));
        }
        catch
        {
            if (options != null) Program.RunMsi("/x", MsiDeployment.GetSingleInstalledProduct(options.InstallFolder), "REBOOT=ReallySuppress", workspace.Resolve("prepare-cleanup.log"));
            throw;
        }
    }
}

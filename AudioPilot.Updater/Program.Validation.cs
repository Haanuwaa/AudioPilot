using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Validation;

namespace AudioPilot.Updater;

internal static partial class Program
{
    private static async Task<int?> TryRunValidationCommandAsync(string[] args)
    {
        switch (args)
        {
            case ["stable-prepare", var baseline, var candidate, var settings, var repository, var root]:
                await FeedTestSetup.PrepareAsync(baseline, candidate, settings, new OwnedWorkspace(root), stableRepository: repository);
                return 0;
            case ["staging-prepare", var baseline, var target, var settings, var bootstrap, var root]:
                await FeedTestSetup.PrepareAsync(baseline, target, settings, new OwnedWorkspace(root), bootstrap);
                return 0;
            case ["feed-prepare", var baseline, var candidate, var settings, var root]:
                await FeedTestSetup.PrepareAsync(baseline, candidate, settings, new OwnedWorkspace(root));
                return 0;
            case ["feed-cleanup", var root]:
                await using (var runtime = InstalledUpdateRuntime.Open(new OwnedWorkspace(root))) await runtime.StopForSandboxCleanupAsync();
                return 0;
            case ["job-ui-cleanup", var root]:
                LocalUiFixture.CleanupMsi(new OwnedWorkspace(root));
                return 0;
            case ["job-ui-prepare", var kind, var baseline, var candidate, var settings, var root, var mask]:
                await LocalUiFixture.PrepareAsync(kind, baseline, candidate, settings, new OwnedWorkspace(root), int.Parse(mask));
                return 0;
            case ["job-portable", var baseline, var candidate, var fixture, var root]:
                await LocalJobSmoke.RunAsync("portable", baseline, candidate, fixture, new OwnedWorkspace(root));
                return 0;
            case ["job-msi", var baseline, var candidate, var fixture, var root, var mask]:
                await LocalJobSmoke.RunAsync("msi", baseline, candidate, fixture, new OwnedWorkspace(root), int.Parse(mask));
                return 0;
            case ["job-apply", var root]:
                await LocalJobSmoke.ApplyAsync(new OwnedWorkspace(root));
                return 0;
            case ["tuf-smoke", var package, var root]:
                await TufIntegrationSmoke.RunAsync(package, new OwnedWorkspace(root));
                return 0;
            case [] when File.Exists(Path.Combine(AppContext.BaseDirectory, PortableLayout.AppDirectoryName, PortableLayout.FileName)):
                return await PortableLauncher.RunAsync(new OwnedWorkspace(Path.Combine(AppContext.BaseDirectory, PortableLayout.AppDirectoryName)));
            case [] when File.Exists(Path.Combine(AppContext.BaseDirectory, "portable-layout.json")):
                return await PortableLauncher.RunAsync(new OwnedWorkspace(AppContext.BaseDirectory));
            case ["portable-launch-probe"]:
                string probeRoot = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableLayout.AppDirectoryName, PortableLayout.FileName))
                    ? Path.Combine(AppContext.BaseDirectory, PortableLayout.AppDirectoryName) : AppContext.BaseDirectory;
                return await PortableLauncher.RunAsync(new OwnedWorkspace(probeRoot), probe: true);
            case ["deployment-portable", var baseline, var candidate, var fixture, var root]:
                await DeploymentLifecycleSmoke.PortableAsync(baseline, candidate, fixture, new OwnedWorkspace(root));
                return 0;
            case ["deployment-msi", var baseline, var candidate, var fixture, var root, var masks]:
                int[] selected = [.. masks.Split(',').Select(int.Parse)];
                if (selected.Length is < 1 or > 8 || selected.Any(mask => mask is < 0 or > 7) || selected.Distinct().Count() != selected.Length)
                    throw new InvalidDataException("Select unique MSI option masks from 0 to 7.");
                await DeploymentLifecycleSmoke.MsiAsync(baseline, candidate, fixture, new OwnedWorkspace(root), selected);
                return 0;
            case ["deployment-msi-failed-start", var baseline, var candidate, var fixture, var root]:
                await DeploymentLifecycleSmoke.MsiAsync(baseline, candidate, fixture, new OwnedWorkspace(root), [7], startupFailureOnly: true);
                return 0;
            case ["deployment-interrupt", var kind, var package, var root, var trustPin]:
                await DeploymentLifecycleSmoke.AttemptAsync(kind, package, new OwnedWorkspace(root), "interrupt", trustPin);
                return 1;
            case ["deployment-recover-msi", var root]:
                await DeploymentLifecycleSmoke.RecoverMsiAsync(new OwnedWorkspace(root));
                return 0;
            case ["health-child", var nonce]:
                if (File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "health-mode.txt")) != "healthy") return 17;
                Console.WriteLine(nonce);
                await Console.In.ReadLineAsync();
                return 0;
            case ["portable-smoke", var root]:
                await PortableSmokeAsync(new OwnedWorkspace(root));
                return 0;
            case ["real-app-smoke", var app, var fixture, var root]:
                await RealAppLifecycleSmoke.RunAsync(app, fixture, new OwnedWorkspace(root));
                return 0;
            case ["portable-interrupt", var root, var archive]:
                await new PortableDeployment(new OwnedWorkspace(root)).ApplyAsync(archive, new FileInfo(archive).Length,
                    HashArchive(archive), "1.0.3",
                    (_, _) => throw new InvalidOperationException("The interrupted helper must not reach health verification."),
                    CancellationToken.None, () => Environment.Exit(86));
                return 1;
            case ["msi-smoke", var baseline, var candidate, var root]:
                MsiSmoke(baseline, candidate, new OwnedWorkspace(root), [.. Enumerable.Range(0, 8)]);
                return 0;
            case ["msi-smoke", var baseline, var candidate, var root, var masks]:
                int[] options = [.. masks.Split(',').Select(int.Parse)];
                if (options.Length is < 1 or > 8 || options.Any(mask => mask is < 0 or > 7) || options.Distinct().Count() != options.Length)
                    throw new InvalidDataException("Select unique MSI option masks from 0 to 7.");
                MsiSmoke(baseline, candidate, new OwnedWorkspace(root), options);
                return 0;
            default:
                return null;
        }
    }

    private static async Task PortableSmokeAsync(OwnedWorkspace workspace)
    {
        var deployment = new PortableDeployment(workspace);
        Directory.CreateDirectory(workspace.Resolve("data"));
        File.WriteAllText(workspace.Resolve("data/settings.json"), "user settings survive replacement");
        var results = new List<object>();
        foreach (var (version, healthy, interrupt) in new[] { ("1.0.0", true, false), ("1.0.1", true, false), ("1.0.2", false, false), ("1.0.3", true, true) })
        {
            string archive = workspace.Resolve(version + ".zip");
            CreateFixture(archive, healthy);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (interrupt)
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AudioPilot.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { "portable-interrupt", workspace.Root, archive }) start.ArgumentList.Add(argument);
                using var helper = Process.Start(start) ?? throw new InvalidOperationException("Interrupted helper did not start.");
                try { await helper.WaitForExitAsync(timeout.Token); }
                finally
                {
                    if (!helper.HasExited) helper.Kill();
                    await helper.WaitForExitAsync(CancellationToken.None);
                }
                if (helper.ExitCode != 86) throw new InvalidOperationException("Helper did not exit at the injected interruption point.");
                if (!deployment.ReadState().PendingHealth) throw new InvalidOperationException("Interrupted switch was not journaled.");
                var recovered = new PortableDeployment(new OwnedWorkspace(workspace.Root)).Recover();
                if (recovered.Current?.StartsWith("1.0.1_", StringComparison.Ordinal) != true ||
                    !await ProbeFixtureAsync(workspace.Resolve(Path.Combine("versions", recovered.Current)), timeout.Token))
                    throw new InvalidOperationException("Interrupted helper did not recover a runnable previous version.");
                results.Add(new { version, recovered = true, interruptedHelperProcess = true });
                continue;
            }
            var result = await deployment.ApplyAsync(archive, new FileInfo(archive).Length,
                HashArchive(archive), version, ProbeFixtureAsync, timeout.Token);
            if (result.Committed != healthy || result.Recovered == healthy) throw new InvalidOperationException("Unexpected portable outcome.");
            if (result.Recovered && !await ProbeFixtureAsync(workspace.Resolve(Path.Combine("versions", result.Current!)), timeout.Token))
                throw new InvalidOperationException("Failed-start rollback did not recover a runnable previous version.");
            results.Add(new { version, result.Committed, result.Recovered, result.Current });
        }
        if (File.ReadAllText(workspace.Resolve("data/settings.json")) != "user settings survive replacement") throw new InvalidOperationException("Portable data changed.");
        OwnedWorkspace.WriteJson(workspace.Resolve("portable-results.json"), results);
        Console.WriteLine(JsonSerializer.Serialize(results));
    }

    private static void CreateFixture(string archive, bool healthy)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        string assembly = typeof(Program).Assembly.Location;
        string prefix = Path.GetFileNameWithoutExtension(assembly);
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(file, name, CompressionLevel.Fastest);
        }
        using var writer = new StreamWriter(zip.CreateEntry("health-mode.txt").Open());
        writer.Write(healthy ? "healthy" : "failed-start");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, prefix + ".exe")))
            throw new InvalidOperationException("Publish the independent helper before running process-level portable tests.");
    }

    private static string HashArchive(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<bool> ProbeFixtureAsync(string folder, CancellationToken token)
    {
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var start = new ProcessStartInfo(Path.Combine(folder, "AudioPilot.Updater.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            WorkingDirectory = folder,
        };
        start.ArgumentList.Add("health-child");
        start.ArgumentList.Add(nonce);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Health process did not start.");
        try
        {
            string? acknowledgement = await child.StandardOutput.ReadLineAsync(token);
            if (acknowledgement != nonce) return false;
            await child.StandardInput.WriteLineAsync("stop");
            await child.WaitForExitAsync(token);
            return child.ExitCode == 0;
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static void MsiSmoke(string baseline, string candidate, OwnedWorkspace workspace, int[] masks)
    {
        MsiDeployment.RequireSandbox();
        var results = new List<object>();
        foreach (int mask in masks)
        {
            string root = workspace.Resolve("option-" + mask);
            Directory.CreateDirectory(root);
            var options = new MsiOptions(Path.Combine(root, "application with spaces"), Path.Combine(root, "user data"),
                (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
            RunMsi("/i", baseline, options.ToProperties(), logPath: Path.Combine(root, "baseline.log"));
            try
            {
                VerifyOptions(options);
                Directory.CreateDirectory(options.DataFolder);
                string data = Path.Combine(options.DataFolder, "updater-user-data.txt");
                File.WriteAllText(data, "preserve me");
                bool observedCandidate = false;
                bool committed = MsiDeployment.Apply(candidate, MsiOptions.ReadInstalled(), () =>
                {
                    VerifyOptions(options);
                    if (!VersionIs(options.InstallFolder, "1.0.1") || !ProbeCli(options.InstallFolder)) throw new InvalidOperationException("Candidate health failed before injected startup failure.");
                    observedCandidate = true;
                    return false;
                }, Path.Combine(root, "failed-health.log"));
                if (committed || !observedCandidate || MsiOptions.ReadInstalled() != options || !ProbeCli(options.InstallFolder))
                    throw new InvalidOperationException("Failed-health rollback did not recover the baseline.");
                string baselineVersion = FileVersionInfo.GetVersionInfo(Path.Combine(options.InstallFolder, "AudioPilot.dll")).ProductVersion!;
                if (!baselineVersion.StartsWith("1.0.0", StringComparison.Ordinal)) throw new InvalidOperationException("Baseline version was not restored.");
                VerifyOptions(options);
                if (!MsiDeployment.Apply(candidate, MsiOptions.ReadInstalled(), () => ProbeCli(options.InstallFolder), Path.Combine(root, "success.log")))
                    throw new InvalidOperationException("Healthy candidate was not committed.");
                if (MsiOptions.ReadInstalled() != options || File.ReadAllText(data) != "preserve me") throw new InvalidOperationException("Upgrade changed options or data.");
                VerifyOptions(options);
                if (!VersionIs(options.InstallFolder, "1.0.1")) throw new InvalidOperationException("Committed version differs.");
                results.Add(new { mask, preserved = true, failedHealthRollback = true, committed = true });
                OwnedWorkspace.WriteJson(workspace.Resolve("msi-results.json"), results);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                throw;
            }
            finally
            {
                RunMsi("/x", MsiDeployment.GetSingleInstalledProduct(options.InstallFolder), "REBOOT=ReallySuppress", Path.Combine(root, "uninstall.log"));
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
    }

    private static bool VersionIs(string folder, string version) => FileVersionInfo.GetVersionInfo(Path.Combine(folder, "AudioPilot.dll")).ProductVersion?.Split('+')[0] == version;

    internal static void VerifyOptions(MsiOptions options)
    {
        if (MsiOptions.ReadInstalled() != options) throw new InvalidOperationException("Registered installer options changed.");
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AudioPilot.lnk");
        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AudioPilot", "AudioPilot.lnk");
        string menuFolder = Path.GetDirectoryName(menu)!;
        string path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        bool onPath = path.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(entry => Path.TrimEndingDirectorySeparator(entry.Trim()).Equals(options.InstallFolder, StringComparison.OrdinalIgnoreCase));
        if (File.Exists(desktop) != options.DesktopShortcut || File.Exists(menu) != options.StartMenuShortcut ||
            File.Exists(Path.Combine(menuFolder, "Change or uninstall AudioPilot.lnk")) != options.StartMenuShortcut ||
            File.Exists(Path.Combine(menuFolder, "Uninstall AudioPilot and delete settings.lnk")) != options.StartMenuShortcut || onPath != options.CliOnPath)
            throw new InvalidOperationException("Installer shortcut or CLI PATH behavior changed.");
    }

    private static bool ProbeCli(string folder)
    {
        var start = new ProcessStartInfo(Path.Combine(folder, "AudioPilot.Cli.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--help");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI startup failed.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(); process.WaitForExit(); return false; }
        Task.WaitAll(output, errors);
        return process.ExitCode == 0 && output.Result.Contains("AudioPilot", StringComparison.Ordinal);
    }

    internal static void RunMsi(string action, string path, string properties, string? logPath = null)
    {
        var start = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false, CreateNoWindow = true, Arguments = $"{action} \"{path}\" /qn /norestart {properties}" + (logPath == null ? "" : $" /l*vx! \"{logPath}\"") };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("MSI runner failed to start.");
        if (!process.WaitForExit(300000)) throw new TimeoutException("MSI operation remains active; do not terminate Windows Installer.");
        if (process.ExitCode != 0) throw new InvalidOperationException($"MSI {action} failed ({process.ExitCode}).");
    }

}

using System.IO;
using System.Windows;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater.Ui;

public partial class UpdaterApplication : Application
{
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        Environment.SetEnvironmentVariable("AUDIOPILOT_DIAGNOSTIC_SESSION", Guid.NewGuid().ToString("N"));
        UpdaterSession? session = null;
        HttpsFeedServer? feedServer = null;
        try
        {
            AppTheme? theme = null;
            string[] command = args.Args;
            if (command.Length >= 2 && command[^2] == "--theme")
            {
                theme = command[^1] switch { "dark" => AppTheme.Dark, "light" => AppTheme.Light, "system" => AppTheme.System, _ => throw new ArgumentException("Choose dark, light or system.") };
                command = command[..^2];
            }
            if (command is ["--smoke", var root, var package, var output])
            {
                await UpdaterUiSmoke.RunAsync(root, package, output);
                Logger.DisposeInstanceIfCreated();
                Shutdown(0);
                return;
            }
            if (command is ["--feed-smoke", var feedRoot, var feedPackage, var feedOutput])
            {
                await FeedUiSmoke.RunAsync(feedRoot, feedPackage, feedOutput);
                Logger.DisposeInstanceIfCreated(); Shutdown(0); return;
            }
            if (command is ["--staging-smoke", var stagingRoot, var stagingOutput])
            {
                await StagingUiSmoke.RunAsync(stagingRoot, stagingOutput);
                Logger.DisposeInstanceIfCreated(); Shutdown(0); return;
            }
            if (command is ["--render-preview", var destination])
            {
                await UpdaterUiSmoke.RenderPreviewAsync(destination);
                Logger.DisposeInstanceIfCreated();
                Shutdown(0);
                return;
            }
            if (command is ["--feed-demo", var demoRoot, var demoPackage])
            {
                MsiDeployment.RequireSandbox();
                var workspace = new OwnedWorkspace(demoRoot);
                feedServer = await Task.Run(() => new HttpsFeedServer(workspace, demoPackage));
                OwnedWorkspace.WriteJson(workspace.Resolve("feed.json"), new FeedJobLaunch(1, OwnedWorkspace.ReadJson<string>(workspace.Resolve("feed-bootstrap.json")), feedServer.Feed));
                session = await Task.Run(() => OpenFeedSession(demoRoot));
            }
            else if (command is ["--feed-ui", var feedPath]) session = await Task.Run(() => OpenFeedSession(feedPath));
            else if (command is ["--staging-ui", var stagingPath]) session = await Task.Run(() => OpenStagingSession(stagingPath));
            else if (command is ["--local-ui", var path])
            {
                MsiDeployment.RequireSandbox();
                session = await Task.Run(() => OpenSession(path));
            }
            else if (command is not ["--preview"]) throw new ArgumentException("Use --preview or --local-ui <prepared-workspace> [--theme dark|light|system].");
            var window = new UpdaterWindow(session, theme ?? session?.Theme ?? AppTheme.System);
            MainWindow = window;
            DialogWindowHelper.ShowOwnedDialog(window);
            await window.WindowClosed;
            if (session != null) await session.DisposeAsync();
            if (feedServer != null) await feedServer.DisposeAsync();
            Logger.DisposeInstanceIfCreated();
            Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (session != null) { try { await session.DisposeAsync(); } catch { } }
            if (feedServer != null) { try { await feedServer.DisposeAsync(); } catch { } }
            if (MainWindow == null && args.Args is not ["--smoke", ..] and not ["--feed-smoke", ..] and not ["--staging-smoke", ..] and not ["--render-preview", ..])
            {
                var failure = new UpdaterWindow(null, AppTheme.System);
                failure.ShowStartupFailure(error);
                MainWindow = failure;
                DialogWindowHelper.ShowOwnedDialog(failure);
                await failure.WindowClosed;
            }
            Logger.DisposeInstanceIfCreated();
            Shutdown(1);
        }
    }

    internal static UpdaterSession OpenSession(string path)
    {
        MsiDeployment.RequireSandbox();
        var workspace = new OwnedWorkspace(path);
        var fixture = OwnedWorkspace.ReadJson<FixtureJobLaunch>(workspace.Resolve("job-fixture.json"));
        var runtime = new LocalFixtureJobRuntime(workspace, fixture.Context);
        try
        {
            var installed = runtime.Inspect();
            var theme = ReadTheme(fixture.Context.DataRoot);
            var job = new LocalUpdateJob(workspace, new TufLocalTrustClient(workspace, fixture.BootstrapHash), runtime);
            return new(job, runtime, installed) { Theme = theme };
        }
        catch { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }
    internal static UpdaterSession OpenFeedSession(string path)
    {
        MsiDeployment.RequireSandbox();
        var workspace = new OwnedWorkspace(path);
        var launch = OwnedWorkspace.ReadJson<FeedJobLaunch>(workspace.Resolve("feed.json"));
        if (launch.Schema != 1) throw new InvalidDataException("Unsupported test feed schema.");
        var runtime = InstalledUpdateRuntime.Open(workspace);
        try
        {
            var binding = OwnedWorkspace.ReadJson<InstalledUpdateBinding>(workspace.Resolve("installation.json"));
            var theme = ReadTheme(binding.Options?.DataFolder ?? workspace.Resolve("data"));
            return UpdaterSession.CreateForFeed(workspace, new TufLocalTrustClient(workspace, launch.BootstrapHash, launch.Feed), runtime, launch.Feed, theme: theme);
        }
        catch { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }
    internal static UpdaterSession OpenStagingSession(string path)
    {
        MsiDeployment.RequireSandbox();
        var workspace = new OwnedWorkspace(path);
        var launch = OwnedWorkspace.ReadJson<StagingJobLaunch>(workspace.Resolve("staging.json"));
        if (launch.Schema != 1) throw new InvalidDataException("Unsupported staging schema.");
        var runtime = InstalledUpdateRuntime.Open(workspace);
        try
        {
            var binding = OwnedWorkspace.ReadJson<InstalledUpdateBinding>(workspace.Resolve("installation.json"));
            return UpdaterSession.CreateForFeed(workspace, new TufLocalTrustClient(workspace, HttpsStagingFeed.BootstrapHash, new HttpsStagingFeed(launch.Target)), runtime, new HttpsStagingFeed(launch.Target),
                theme: ReadTheme(binding.Options?.DataFolder ?? workspace.Resolve("data")));
        }
        catch { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    private static AppTheme ReadTheme(string dataRoot)
    {
        try
        {
            string path = System.IO.Path.Combine(dataRoot, "settings.json");
            OwnedWorkspace.CheckAncestors(path);
            using var input = System.IO.File.OpenRead(path);
            if (input.Length > 1024 * 1024) return AppTheme.System;
            using var document = System.Text.Json.JsonDocument.Parse(input);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && document.RootElement.TryGetProperty("Theme", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String &&
                Enum.TryParse<AppTheme>(value.GetString(), out var selected) && Enum.IsDefined(selected) ? selected : AppTheme.System;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return AppTheme.System; }
    }
}

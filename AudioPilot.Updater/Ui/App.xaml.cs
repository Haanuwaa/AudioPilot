using System.IO;
using System.Windows;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;
using AudioPilot.Updater.Ui.Validation;
using AudioPilot.Updater.Validation;

namespace AudioPilot.Updater.Ui;

public partial class UpdaterApplication : Application
{
    private FileStream? _pilotRuntimeLease;
    private UpdaterTemporaryDiagnostics? _temporaryDiagnostics;
    protected override void OnExit(ExitEventArgs args)
    {
        Logger.DisposeInstanceIfCreated();
        _temporaryDiagnostics?.Cleanup(Logger.InstanceShutdownComplete);
        _pilotRuntimeLease?.Dispose();
        base.OnExit(args);
    }
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        LocalizationService.Default.SetLanguage(Environment.GetEnvironmentVariable(UpdaterControlWire.LanguageEnvironmentVariable));
        Environment.SetEnvironmentVariable(UpdaterControlWire.LanguageEnvironmentVariable, null);
        UpdaterSession? session = null;
        HttpsFeedServer? feedServer = null;
        bool resumeRecovery = false;
        UpdaterControlServer? control = null;
        try
        {
            _temporaryDiagnostics = new();
            Environment.SetEnvironmentVariable("AUDIOPILOT_DIAGNOSTIC_SESSION", _temporaryDiagnostics.Id);
            AppTheme? theme = null;
            string[] command = args.Args;
            if (command.Length >= 2 && command[^2] == "--theme")
            {
                theme = command[^1] switch { "dark" => AppTheme.Dark, "light" => AppTheme.Light, "system" => AppTheme.System, _ => throw new ArgumentException("Choose dark, light or system.") };
                command = command[..^2];
            }
            if (command is ["--automation", ..])
            {
                int code = await UpdaterAutomation.RunAsync(command);
                Shutdown(code); return;
            }
            if (command is ["--smoke", var root, var package, var output])
            {
                await UpdaterUiSmoke.RunAsync(root, package, output);
                Shutdown(0);
                return;
            }
            if (command is ["--feed-smoke", var feedRoot, var feedPackage, var feedOutput])
            {
                await FeedUiSmoke.RunAsync(feedRoot, feedPackage, feedOutput);
                Shutdown(0); return;
            }
            if (command is ["--staging-smoke", var stagingRoot, var stagingOutput])
            {
                await StagingUiSmoke.RunAsync(stagingRoot, stagingOutput);
                Shutdown(0); return;
            }
            if (command is ["--stable-smoke", var stableRoot, var stablePackage, var stableOutput])
            {
                MsiDeployment.RequireSandbox();
                await StagingUiSmoke.RunAsync(stableRoot, stableOutput, stablePackage: stablePackage);
                Shutdown(0); return;
            }
            if (command is ["--pilot-smoke", var pilotSmokeRoot, var pilotSmokeOutput])
            {
                MsiDeployment.RequireSandbox(); RequirePilotRuntime();
                await StagingUiSmoke.RunAsync(pilotSmokeRoot, pilotSmokeOutput, pilot: true);
                Shutdown(0); return;
            }
            if (command is ["--render-preview", var destination])
            {
                await UpdaterUiSmoke.RenderPreviewAsync(destination);
                Shutdown(0);
                return;
            }
            if (command is ["--pilot-ui", var pilotApp, var pilotRoot, var pilotSession, var pilotPid, var pilotStarted])
                session = await Task.Run(() => OpenPilotSession(pilotApp, pilotRoot, pilotSession, pilotPid, pilotStarted));
            else if (command is ["--pilot-recover", var recoveryRoot])
            {
                session = await Task.Run(() => OpenPilotRecoverySession(recoveryRoot));
                resumeRecovery = true;
            }
            else if (command is ["--feed-demo", var demoRoot, var demoPackage])
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
            if (session != null && command is ["--pilot-ui", _, var controlRoot, ..])
                control = new UpdaterControlServer(session, new OwnedWorkspace(controlRoot), action => Dispatcher.InvokeAsync(() =>
                {
                    var reply = action();
                    if (MainWindow is UpdaterWindow active) active.Refresh(force: true);
                    return reply;
                }).Task, () => (MainWindow as UpdaterWindow)?.ShowOrActivate());
            var window = new UpdaterWindow(session, theme ?? session?.Theme ?? AppTheme.System);
            MainWindow = window;
            if (resumeRecovery) _ = Dispatcher.BeginInvoke(new Action(window.ResumePendingRecovery));
            DialogWindowHelper.ShowOwnedDialog(window);
            await window.WindowClosed;
            if (control != null) await control.DisposeAsync();
            if (session != null) await session.DisposeAsync();
            if (feedServer != null) await feedServer.DisposeAsync();
            Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.StartupFailed, Error: error)));
            if (control != null) { try { await control.DisposeAsync(); } catch { } }
            if (session != null) { try { await session.DisposeAsync(); } catch { } }
            if (feedServer != null) { try { await feedServer.DisposeAsync(); } catch { } }
            if (MainWindow == null && args.Args is not ["--smoke", ..] and not ["--feed-smoke", ..] and not ["--staging-smoke", ..] and not ["--pilot-smoke", ..] and not ["--render-preview", ..])
            {
                var failure = new UpdaterWindow(null, AppTheme.System);
                failure.ShowStartupFailure(error);
                MainWindow = failure;
                DialogWindowHelper.ShowOwnedDialog(failure);
                await failure.WindowClosed;
            }
            Shutdown(1);
        }
    }

    internal static UpdaterSession OpenSession(string path)
    {
        MsiDeployment.RequireSandbox();
        return OpenLoggedSession(path, nameof(OpenSession), workspace =>
        {
            var fixture = OwnedWorkspace.ReadJson<FixtureJobLaunch>(workspace.Resolve("job-fixture.json"));
            var runtime = new LocalFixtureJobRuntime(workspace, fixture.Context);
            try
            {
                var installed = runtime.Inspect();
                var theme = ReadTheme(fixture.Context.DataRoot);
                var job = new LocalUpdateJob(workspace, new TufLocalTrustClient(workspace, fixture.BootstrapHash), runtime);
                return new(job, runtime, installed) { Theme = theme };
            }
            catch { DisposeFailedRuntime(runtime, workspace); throw; }
        });
    }
    internal static UpdaterSession OpenFeedSession(string path)
    {
        MsiDeployment.RequireSandbox();
        return OpenLoggedSession(path, nameof(OpenFeedSession), workspace =>
        {
            var launch = OwnedWorkspace.ReadJson<FeedJobLaunch>(workspace.Resolve("feed.json"));
            if (launch.Schema != 1) throw new InvalidDataException("Unsupported test feed schema.");
            var runtime = InstalledUpdateRuntime.Open(workspace);
            try
            {
                var binding = OwnedWorkspace.ReadJson<InstalledUpdateBinding>(workspace.Resolve("installation.json"));
                var theme = ReadTheme(binding, workspace);
                return UpdaterSession.CreateForFeed(workspace, new TufLocalTrustClient(workspace, launch.BootstrapHash, launch.Feed), runtime, launch.Feed, theme: theme);
            }
            catch { DisposeFailedRuntime(runtime, workspace); throw; }
        });
    }
    internal static UpdaterSession OpenStagingSession(string path)
    {
        MsiDeployment.RequireSandbox();
        return OpenLoggedSession(path, nameof(OpenStagingSession), workspace =>
        {
            var launch = OwnedWorkspace.ReadJson<StagingJobLaunch>(workspace.Resolve("staging.json"));
            if (launch.Schema != 1) throw new InvalidDataException("Unsupported staging schema.");
            var runtime = InstalledUpdateRuntime.Open(workspace);
            try
            {
                var binding = OwnedWorkspace.ReadJson<InstalledUpdateBinding>(workspace.Resolve("installation.json"));
                return UpdaterSession.CreateForFeed(workspace, new TufLocalTrustClient(workspace, HttpsStagingFeed.BootstrapHash, new HttpsStagingFeed(launch.Target)), runtime, new HttpsStagingFeed(launch.Target),
                    theme: ReadTheme(binding, workspace));
            }
            catch { DisposeFailedRuntime(runtime, workspace); throw; }
        });
    }

    internal static UpdaterSession OpenPilotSession(string application, string path, string identity, string pidText, string startedText)
    {
        RequirePilotRuntime();
        return OpenLoggedSession(path, nameof(OpenPilotSession), workspace =>
        {
            if (!int.TryParse(pidText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
                !long.TryParse(startedText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ticks))
                throw new InvalidDataException("Invalid pilot process identity.");
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != ticks || !string.Equals(process.MainModule?.FileName, application, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pilot application identity changed.");
            var runtime = InstalledUpdateRuntime.BindPilot(workspace, application, CandidateOwner.Capture(process, application), identity);
            return CreatePilotSession(workspace, runtime);
        });
    }

    private static void RequirePilotRuntime()
    {
        UpdateClientPolicy.RequireConfigured();
        if (!UpdateClientPolicy.IsHelperPath(Environment.ProcessPath!)) throw new InvalidOperationException("Launch the packaged pilot from AudioPilot so its runtime is safely staged outside the installation.");
        if (Current is UpdaterApplication { _pilotRuntimeLease: null } app)
            app._pilotRuntimeLease = File.Open(Path.Combine(AppContext.BaseDirectory, "runtime.inuse"), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static UpdaterSession OpenPilotRecoverySession(string path)
    {
        RequirePilotRuntime();
        return OpenLoggedSession(path, nameof(OpenPilotRecoverySession), workspace =>
        {
            return CreatePilotSession(workspace, InstalledUpdateRuntime.Open(workspace));
        });
    }

    private static UpdaterSession CreatePilotSession(OwnedWorkspace workspace, InstalledUpdateRuntime runtime)
    {
        try
        {
            var policy = UpdateClientPolicy.Current!;
            string directory = workspace.Resolve("tuf-repository"); Directory.CreateDirectory(directory);
            string bootstrap = workspace.Resolve("tuf-repository/bootstrap.root.json");
            byte[] bytes = Convert.FromBase64String(policy.BootstrapRoot);
            if (!File.Exists(bootstrap)) File.WriteAllBytes(bootstrap, bytes);
            if (!File.ReadAllBytes(bootstrap).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("The pilot bootstrap changed.");
            IUpdateFeed feed = policy.Channel == "stable" ? new HttpsStableFeed(policy) : new HttpsStagingFeed(policy.Target(runtime.Kind), IsPilot: true);
            return UpdaterSession.CreateForFeed(workspace, new TufLocalTrustClient(workspace, policy.RootPin, feed), runtime, feed);
        }
        catch { DisposeFailedRuntime(runtime, workspace); throw; }
    }

    private static void DisposeFailedRuntime(IAsyncDisposable runtime, OwnedWorkspace workspace)
    {
        try { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception error) { UpdateLog.Write(workspace.Diagnostics, new(UpdateLogKind.CleanupFailed, Error: error)); }
    }

    private static UpdaterSession OpenLoggedSession(string path, string operation, Func<OwnedWorkspace, UpdaterSession> open)
    {
        var workspace = new OwnedWorkspace(path);
        UpdaterLog.Attach(workspace);
        try { return open(workspace); }
        catch (Exception error)
        {
            UpdateLog.Write(workspace.Diagnostics, new(UpdateLogKind.StartupFailed, Operation: operation, Error: error));
            try { workspace.Diagnostics?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { } // Keep the original startup failure if diagnostic cleanup also fails.
            throw;
        }
    }

    private static AppTheme ReadTheme(InstalledUpdateBinding binding, OwnedWorkspace workspace) =>
        ReadTheme(binding.Options?.DataFolder ?? PortableLayout.GetDataRoot(
            Path.GetDirectoryName(PortableLauncher.CurrentApp(workspace, new PortableDeployment(workspace).ReadState()))!, null)
            ?? throw new InvalidDataException("Missing portable settings location."));

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

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Platform;
using AudioPilot.Services.Configuration;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;
#if AUDIOPILOT_UPDATE_ENGINEERING
using AudioPilot.Updater.Ui.Validation;
using AudioPilot.Updater.Validation;
#endif

namespace AudioPilot.Updater.Ui;

public partial class UpdaterApplication : Application
{
    private const int WmEndSession = 0x0016;
    private static readonly TimeSpan SessionEndingTimeout = TimeSpan.FromMilliseconds(1500);
    private FileStream? _pilotRuntimeLease;
    private UpdaterTemporaryDiagnostics? _temporaryDiagnostics;
    private UpdaterControlServer? _control;
    private UpdaterSession? _session;
    private HwndSource? _sessionEndingSource;
    private volatile bool _sessionEnding;
    private int _sessionEndingStarted;
    private int _unhandledShutdownStarted;
    private bool _exceptionHandlersRegistered;

    internal void RegisterActiveSession(UpdaterSession? session) => Volatile.Write(ref _session, session);
    internal void RegisterActiveControl(UpdaterControlServer? control) => Volatile.Write(ref _control, control);
    internal UpdaterSession? TakeActiveSession() => Interlocked.Exchange(ref _session, null);
    internal UpdaterControlServer? TakeActiveControl() => Interlocked.Exchange(ref _control, null);

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel) return;
        // WPF schedules CriticalShutdown immediately after OnSessionEnding returns with Cancel == false.
        BeginSessionEnding();
    }

    private void AttachSessionEndingListener()
    {
        if (_sessionEndingSource != null) return;
        try
        {
            _sessionEndingSource = new HwndSource(new HwndSourceParameters("AudioPilotUpdaterSessionEnding")
            {
                ParentWindow = 0,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            });
            _sessionEndingSource.AddHook(OnSessionEndingWindowMessage);
        }
        catch
        {
            _sessionEndingSource?.Dispose();
            _sessionEndingSource = null;
        }
    }

    private nint OnSessionEndingWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmEndSession && wParam != 0)
        {
            BeginSessionEnding();
        }

        return 0;
    }

    private void BeginSessionEnding()
    {
        _sessionEnding = true;
        SessionEndingState.MarkEnding();
        if (Interlocked.Exchange(ref _sessionEndingStarted, 1) != 0) return;
        (MainWindow as UpdaterWindow)?.AllowCloseForSessionEnding();
        PrepareForSessionEnding();
    }

    private void PrepareForSessionEnding()
    {
        UpdaterControlServer? control = Interlocked.Exchange(ref _control, null);
        UpdaterSession? session = Interlocked.Exchange(ref _session, null);
        if (control == null && session == null) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        Task drain = DrainUpdaterResourcesAsync(control, session);
        if (drain.IsCompleted) return;
        var frame = new System.Windows.Threading.DispatcherFrame();
        using var stopWaiting = new CancellationTokenSource();
        Task deadline = Task.Delay(SessionEndingTimeout, stopWaiting.Token);
        _ = Task.WhenAny(drain, deadline).ContinueWith(_ => frame.Continue = false,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!drain.IsCompleted) System.Windows.Threading.Dispatcher.PushFrame(frame);
        stopWaiting.Cancel();
    }

    private static async Task DrainUpdaterResourcesAsync(UpdaterControlServer? control, UpdaterSession? session)
    {
        if (control != null)
        {
            try { await control.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.CleanupFailed, Error: error))); }
        }
        if (session != null)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.CleanupFailed, Error: error))); }
        }
    }

    private void AttachExceptionHandlers()
    {
        if (_exceptionHandlersRegistered) return;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        _exceptionHandlersRegistered = true;
    }

    private void DetachExceptionHandlers()
    {
        if (!_exceptionHandlersRegistered) return;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        _exceptionHandlersRegistered = false;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception error = e.ExceptionObject as Exception ?? new InvalidOperationException("Unhandled updater failure.");
        UpdateLogEvent entry = new(UpdateLogKind.OperationFailed, _session?.Snapshot.Phase, "unhandled-exception", Error: error);
        try
        {
            if (_session?.Diagnostics is UpdaterLog updaterLog) updaterLog.WriteFatalAndFlush(entry);
            else UpdateLog.Write(_session?.Diagnostics, entry);
        }
        catch { }
        try { Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.OperationFailed, Error: error))); }
        catch { }
    }

    private async void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try { UpdateLog.Write(_session?.Diagnostics, new(UpdateLogKind.OperationFailed, _session?.Snapshot.Phase, "dispatcher-unhandled-exception", Error: e.Exception)); }
        catch { }
        try { Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.OperationFailed, Error: e.Exception))); }
        catch { }

        if (Interlocked.Exchange(ref _unhandledShutdownStarted, 1) != 0) return;
        try
        {
            UpdaterControlServer? control = Interlocked.Exchange(ref _control, null);
            UpdaterSession? session = Interlocked.Exchange(ref _session, null);
            await DrainUpdaterResourcesAsync(control, session);
        }
        catch { }
        finally
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished) Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs args)
    {
        DetachExceptionHandlers();
        Interlocked.Exchange(ref _sessionEndingSource, null)?.Dispose();
        if (_sessionEnding) Logger.DisposeInstanceNonBlockingIfCreated();
        else Logger.DisposeInstanceIfCreated();
        _temporaryDiagnostics?.Cleanup(Logger.InstanceShutdownComplete);
        _pilotRuntimeLease?.Dispose();
        base.OnExit(args);
    }
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        AttachExceptionHandlers();
        AttachSessionEndingListener();
        LocalizationService.Default.SetLanguage(Environment.GetEnvironmentVariable(UpdaterControlWire.LanguageEnvironmentVariable));
        Environment.SetEnvironmentVariable(UpdaterControlWire.LanguageEnvironmentVariable, null);
        UpdaterSession? session = null;
#if AUDIOPILOT_UPDATE_ENGINEERING
        HttpsFeedServer? feedServer = null;
#endif
        bool resumeRecovery = false;
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
                int code = await UpdaterAutomation.RunAsync(command, theme);
                Shutdown(code); return;
            }
#if AUDIOPILOT_UPDATE_ENGINEERING
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
#endif
            if (command is ["--pilot-ui", var pilotApp, var pilotRoot, var pilotSession, var pilotPid, var pilotStarted])
                session = await Task.Run(() => OpenPilotSession(pilotApp, pilotRoot, pilotSession, pilotPid, pilotStarted));
            else if (command is ["--pilot-recover", var recoveryRoot])
            {
                session = await Task.Run(() => OpenPilotRecoverySession(recoveryRoot));
                resumeRecovery = true;
            }
#if AUDIOPILOT_UPDATE_ENGINEERING
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
#else
            else throw new ArgumentException("Open the packaged updater from AudioPilot Settings.");
#endif
            AppTheme windowTheme = theme ?? session?.Theme ?? AppTheme.System;
            UpdaterSession? activeWindowSession = session;
            Volatile.Write(ref _session, activeWindowSession);
            session = null;
            if (activeWindowSession != null && command is ["--pilot-ui", _, var controlRoot, ..])
                Volatile.Write(ref _control, new UpdaterControlServer(activeWindowSession, new OwnedWorkspace(controlRoot), action => Dispatcher.InvokeAsync(() =>
                {
                    var reply = action();
                    if (MainWindow is UpdaterWindow active) active.Refresh(force: true);
                    return reply;
                }).Task, _ =>
                {
                    (MainWindow as UpdaterWindow)?.ShowOrActivate();
                    return Task.FromResult(MainWindow is { IsVisible: true });
                }, setTheme: selected =>
                {
                    windowTheme = selected;
                    (MainWindow as UpdaterWindow)?.ApplyTheme(selected);
                }));
            var window = new UpdaterWindow(activeWindowSession, windowTheme);
            MainWindow = window;
            if (resumeRecovery) _ = Dispatcher.BeginInvoke(new Action(window.ResumePendingRecovery));
            DialogWindowHelper.ShowOwnedDialog(window);
            await window.WindowClosed;
            var activeControl = Interlocked.Exchange(ref _control, null);
            if (activeControl != null) await activeControl.DisposeAsync();
            var activeSession = Interlocked.Exchange(ref _session, null);
            if (activeSession != null) await activeSession.DisposeAsync();
#if AUDIOPILOT_UPDATE_ENGINEERING
            if (feedServer != null) await feedServer.DisposeAsync();
#endif
            Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.StartupFailed, Error: error)));
            var failedControl = Interlocked.Exchange(ref _control, null);
            if (failedControl != null) { try { await failedControl.DisposeAsync(); } catch { } }
            var failedSession = Interlocked.Exchange(ref _session, null) ?? session;
            session = null;
            if (failedSession != null) { try { await failedSession.DisposeAsync(); } catch { } }
#if AUDIOPILOT_UPDATE_ENGINEERING
            if (feedServer != null) { try { await feedServer.DisposeAsync(); } catch { } }
#endif
            if (!_sessionEnding && MainWindow == null && args.Args is not ["--automation", ..] and not ["--smoke", ..] and not ["--feed-smoke", ..] and not ["--staging-smoke", ..] and not ["--pilot-smoke", ..] and not ["--render-preview", ..])
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

#if AUDIOPILOT_UPDATE_ENGINEERING
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
#endif

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
        UpdateClientPolicy.RequireUnelevated();
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
            if (!File.Exists(bootstrap))
            {
                string temporary = bootstrap + ".new";
                using (var output = OwnedWorkspace.CreateScratchFile(temporary))
                {
                    output.Write(bytes);
                    output.Flush(true);
                }
                OwnedWorkspace.Promote(temporary, bootstrap);
            }
            using (var input = File.OpenRead(bootstrap))
            {
                if (input.Length != bytes.Length) throw new InvalidDataException("The pilot bootstrap changed.");
                byte[] actual = new byte[bytes.Length];
                input.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("The pilot bootstrap changed.");
            }
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

#if AUDIOPILOT_UPDATE_ENGINEERING
    private static AppTheme ReadTheme(InstalledUpdateBinding binding, OwnedWorkspace workspace) =>
        ReadTheme(binding.Options?.DataFolder ?? PortableLayout.GetDataRoot(
            Path.GetDirectoryName(PortableLauncher.CurrentApp(workspace, new PortableDeployment(workspace).ReadState()))!, null)
            ?? throw new InvalidDataException("Missing portable settings location."));
#endif

    private sealed record SettingsAppearance(AppSettings? App);

    internal static AppTheme ReadTheme(string dataRoot)
    {
        try
        {
            string path = Path.Combine(dataRoot, "settings.json");
            OwnedWorkspace.CheckAncestors(path);
            using var input = File.OpenRead(path);
            if (input.Length > SettingsStorageLimits.MaximumSettingsFileBytes) return AppTheme.System;
            return JsonSerializer.Deserialize<SettingsAppearance>(input, SettingsJson.Options)?.App?.Theme ?? AppTheme.System;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return AppTheme.System; }
    }
}

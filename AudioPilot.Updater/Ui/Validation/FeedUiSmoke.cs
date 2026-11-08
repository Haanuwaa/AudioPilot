using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using AudioPilot.Models;
using AudioPilot.Updater.Trust;
using AudioPilot.Updater.Validation;
using static AudioPilot.Updater.Ui.Validation.UpdaterUiSmoke;

namespace AudioPilot.Updater.Ui.Validation;

internal static class FeedUiSmoke
{
    public static async Task RunAsync(string root, string package, string output)
    {
        MsiDeployment.RequireSandbox();
        var workspace = new OwnedWorkspace(root); Directory.CreateDirectory(output);
        string pin = OwnedWorkspace.ReadJson<string>(workspace.Resolve("feed-bootstrap.json"));
        await using var server = new HttpsFeedServer(workspace, package);
        OwnedWorkspace.WriteJson(workspace.Resolve("feed.json"), new FeedJobLaunch(1, pin, server.Feed));
        var results = new List<object>();
        void Record(string name, UpdateJobState state)
        {
            results.Add(new { name, phase = state.Phase.ToString(), state.Reason });
            OwnedWorkspace.WriteJson(Path.Combine(output, "https-results.json"), results);
        }
        UpdaterSession Create(InstalledUpdateRuntime runtime, ILocalUpdateRuntime? operations = null) =>
            UpdaterSession.CreateForFeed(workspace, new TufLocalTrustClient(workspace, pin, server.Feed), runtime, server.Feed, operations);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); server.HoldPackage = hold;
        await using (var runtime = InstalledUpdateRuntime.Open(workspace))
        await using (var session = Create(runtime))
        {
            var window = new UpdaterWindow(session, AppTheme.Dark, confirm: static (_, _) => Task.FromResult(true)); window.Show();
            if (session.Snapshot.Source != server.Feed.PackageUri.AbsoluteUri && session.Snapshot.Phase is UpdateJobPhase.Failed or UpdateJobPhase.Paused)
                Click(window, "CancelButton");
            string metadata = workspace.Resolve("tuf-repository/metadata/1.targets.json");
            byte[] original = File.ReadAllBytes(metadata); File.AppendAllText(metadata, "tampered");
            try
            {
                Click(window, "VerifyButton"); await window.ActiveAction!.WaitAsync(TimeSpan.FromSeconds(60));
                Require(session.Snapshot.Phase == UpdateJobPhase.Failed && server.MetadataRequests > 0 && server.PackageRequests == 0, "The metadata tamper probe did not reach authenticated HTTPS verification.");
                Record("tampered-metadata-rejected", session.Snapshot);
            }
            finally { File.WriteAllBytes(metadata, original); }
            Click(window, "PauseButton");
            await Until(() => !session.IsBusy || session.Snapshot.Phase == UpdateJobPhase.Staging && session.Snapshot.Bytes > 0);
            Require(session.Snapshot.Phase == UpdateJobPhase.Staging && session.Snapshot.Bytes > 0, "HTTPS transfer did not start: " + session.Snapshot.Phase + "/" + session.Snapshot.Reason);
            window.Refresh(true); Click(window, "PauseButton"); await window.ActiveAction!.WaitAsync(TimeSpan.FromSeconds(30));
            Require(session.Snapshot.Phase == UpdateJobPhase.Paused && session.Snapshot.Bytes > 0, "HTTPS pause did not retain bytes.");
            Record("https-pause", session.Snapshot);
            await RenderAsync(window, Path.Combine(output, "paused.png")); window.Close(); await window.WindowClosed;
        }
        hold.TrySetResult(); server.HoldPackage = null; server.DropNextPackage = true;
        await using var active = InstalledUpdateRuntime.Open(workspace);
        var dispatch = new DeploymentGate(active);
        await using var update = Create(active, dispatch);
        var view = new UpdaterWindow(update, AppTheme.Dark, confirm: static (_, _) => Task.FromResult(true)); view.Show();
        Click(view, "PauseButton"); await view.ActiveAction!.WaitAsync(TimeSpan.FromSeconds(90));
        Require(update.Snapshot.Phase == UpdateJobPhase.Failed, "Interrupted HTTPS transfer did not retain retry state.");
        Record("connection-drop", update.Snapshot);
        await update.VerifyAsync(server.Feed.PackageUri.AbsoluteUri).WaitAsync(TimeSpan.FromSeconds(90)); view.Refresh(true);
        Require(update.Snapshot.Phase == UpdateJobPhase.Ready && server.ResumedRequests >= 2 && server.MetadataRequests > 0, "HTTPS range retry or maintained TUF verification was bypassed.");
        Record("https-resume-verified", update.Snapshot);
        Click(view, "CancelButton"); Require(update.Snapshot.Phase == UpdateJobPhase.Cancelled, "Cancellation was not acknowledged.");
        Require(!File.Exists(workspace.Resolve("job-download/package.partial")), "Cancelled transfer retained managed partial bytes.");
        Record("cancel-before-preparation", update.Snapshot);
        await update.VerifyAsync(server.Feed.PackageUri.AbsoluteUri).WaitAsync(TimeSpan.FromSeconds(90)); view.Refresh(true);
        Require(update.Snapshot.Phase == UpdateJobPhase.Ready, "Redownload did not reach readiness.");
        Require((await active.PrepareAsync(CancellationToken.None)).Ready, "Existing app could not prepare.");
        await active.CancelPreparationAsync(CancellationToken.None); await active.ExerciseAutoSaveAsync();
        active.BeforeCandidateStart = _ =>
        {
            File.WriteAllText(Path.Combine(new DeploymentRecovery(workspace).Read().DataRoot, "settings.json"), "{ interrupted candidate migration");
            return Task.CompletedTask;
        };
        Click(view, "InstallButton"); await view.ActiveAction!.WaitAsync(TimeSpan.FromMinutes(7));
        Require(update.Snapshot.Phase == UpdateJobPhase.RolledBack, "Failed-start recovery did not restore the application: " + update.Snapshot.Reason);
        AssertSavedSnapshot(workspace);
        Require(active.Inspect().InstalledVersion == "1.0.0", "Rollback selected the wrong version.");
        Record("installed-adapter-startup-rollback", update.Snapshot);
        await RenderAsync(view, Path.Combine(output, "rollback.png"));
        active.BeforeCandidateStart = null;
        await update.VerifyAsync(server.Feed.PackageUri.AbsoluteUri).WaitAsync(TimeSpan.FromSeconds(90)); view.Refresh(true);
        Require(update.Snapshot.Phase == UpdateJobPhase.Ready, "The recovered app could not retry.");
        var oldRecord = new DeploymentRecovery(workspace).Read();
        OwnedWorkspace.WriteJson(workspace.Resolve("recovery.json"), oldRecord with { TargetVersion = active.Inspect().InstalledVersion, DeploymentCommitted = true });
        dispatch.LockSnapshotPath = Path.Combine(oldRecord.DataRoot, "settings.json");
        Click(view, "InstallButton"); await view.ActiveAction!.WaitAsync(TimeSpan.FromSeconds(60));
        Require(update.Snapshot.Phase == UpdateJobPhase.RolledBack && !active.IsPrepared, "A pre-journal failure claimed a newer commit or retained reversible admission.");
        Require(active.Inspect().InstalledVersion == "1.0.0", "Snapshot failure changed the installed application.");
        Record("snapshot-failure-releases-original", update.Snapshot);
        dispatch.LockSnapshotPath = null;
        await update.VerifyAsync(server.Feed.PackageUri.AbsoluteUri).WaitAsync(TimeSpan.FromSeconds(90)); view.Refresh(true);
        Require(update.Snapshot.Phase == UpdateJobPhase.Ready, "The original app could not retry after snapshot failure.");
        Require((await active.PrepareAsync(CancellationToken.None)).Ready, "Recovered app could not prepare.");
        await active.CancelPreparationAsync(CancellationToken.None); await active.ExerciseAutoSaveAsync();
        dispatch.Hold = true;
        Click(view, "InstallButton"); await dispatch.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        view.Close(); Require(!view.IsVisible && !view.WindowClosed.IsCompleted, "Closing stopped an armed update.");
        dispatch.Release.TrySetResult(); await view.WindowClosed.WaitAsync(TimeSpan.FromMinutes(7));
        Require(update.Snapshot.Phase == UpdateJobPhase.Completed && active.Inspect().InstalledVersion == "1.0.1", "The installed update did not commit.");
        AssertSavedSnapshot(workspace);
        var recovery = new DeploymentRecovery(workspace).Read();
        using var running = Process.GetProcessById(recovery.Candidate!.Pid);
        Require(!running.HasExited && running.StartTime.ToUniversalTime().Ticks == recovery.Candidate.StartedUtcTicks, "The committed app was not left running.");
        if (recovery.InstallerOptions != null) Program.VerifyOptions(recovery.InstallerOptions);
        Record("installed-adapter-commit-close", update.Snapshot);
        OwnedWorkspace.WriteJson(Path.Combine(output, "feed-summary.json"), new { server.MetadataRequests, server.PackageRequests, server.ResumedRequests, realAppKeptRunning = true, normalInstanceIdentity = true, finalAutosavePreserved = true, localStateSnapshot = recovery.LocalSnapshot != null });
    }
    internal static void AssertSavedSnapshot(OwnedWorkspace workspace)
    {
        var record = new DeploymentRecovery(workspace).Read();
        string saved = workspace.Resolve("snapshots/" + record.Snapshot + "/settings.json");
        Require(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(saved)), JsonNode.Parse(File.ReadAllText(Path.Combine(record.DataRoot, "settings.json")))), "The final autosave was not preserved.");
        if (record.LocalSnapshot != null)
        {
            string path = workspace.Resolve("snapshots/" + record.LocalSnapshot + "/window-placement.json");
            if (File.Exists(path)) Require(File.ReadAllText(path) == File.ReadAllText(Path.Combine(record.LocalStateRoot!, "window-placement.json")), "Local window placement was not preserved.");
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!ready()) await Task.Delay(10, deadline.Token);
    }
    internal sealed class DeploymentGate(InstalledUpdateRuntime runtime) : ILocalUpdateRuntime
    {
        public bool Hold;
        public string? LockSnapshotPath;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Context => runtime.Context;
        public bool RecoveryPending => runtime.RecoveryPending;
        public UpdateSelection Inspect() => runtime.Inspect();
        public Task<UpdateJobPreparation> PrepareAsync(CancellationToken token) => runtime.PrepareAsync(token);
        public Task CancelPreparationAsync(CancellationToken token) => runtime.CancelPreparationAsync(token);
        public Task<bool> RecoverAsync() => runtime.RecoverAsync();
        public async Task<bool> DeployAsync(VerifiedLocalUpdate package)
        {
            if (Hold) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            using var locked = LockSnapshotPath == null ? null : new FileStream(LockSnapshotPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return await runtime.DeployAsync(package);
        }
    }
}

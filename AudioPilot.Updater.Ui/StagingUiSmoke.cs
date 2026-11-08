using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Models;
using AudioPilot.Services.UI;
using AudioPilot.Updater.Trust;
using static AudioPilot.Updater.Ui.UpdaterUiSmoke;

namespace AudioPilot.Updater.Ui;

/// <summary>Explicit noninteractive consent driver around actual themed dialogs and public HTTPS, only in Sandbox.</summary>
internal static class StagingUiSmoke
{
    public static async Task RunAsync(string root, string output)
    {
        MsiDeployment.RequireSandbox();
        Directory.CreateDirectory(output);
        var workspace = new OwnedWorkspace(root);
        var launch = OwnedWorkspace.ReadJson<StagingJobLaunch>(workspace.Resolve("staging.json"));
        var feed = new HttpsStagingFeed(launch.Target);
        var results = new List<object>();
        void Record(string name, UpdateJobState state)
        {
            results.Add(new { name, phase = state.Phase.ToString(), state.Bytes, state.Reason });
            OwnedWorkspace.WriteJson(Path.Combine(output, "https-results.json"), results);
        }
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Hold(long bytes, CancellationToken token)
        {
            if (bytes < 131072) return;
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        UpdaterSession Create(InstalledUpdateRuntime runtime, ILocalUpdateRuntime? operations = null, bool hold = false) =>
            UpdaterSession.CreateForFeed(workspace, new TufLocalTrustClient(workspace, HttpsStagingFeed.BootstrapHash, feed), runtime, feed, operations, AppTheme.Dark, hold ? Hold : null);

        await using var dialogs = new AppDialogService(); dialogs.SetSoundsEnabled(false);
        UpdaterWindow? owner = null;
        int downloads = 0, installs = 0;
        async Task<bool> Consent(UpdateConsent consent, CancellationToken token)
        {
            bool accept = consent.Kind == UpdateConsentKind.Download ? ++downloads > 1 : ++installs > 1;
            var response = dialogs.ShowAsync(consent.CreateDialog(owner!), token);
            await Until(() => Application.Current.Windows.OfType<AppDialogWindow>().Any(w => w.IsVisible && w.Owner == owner));
            var dialog = Application.Current.Windows.OfType<AppDialogWindow>().Single(w => w.IsVisible && w.Owner == owner);
            var actions = ((Panel)dialog.FindName("ActionsPanel")).Children.OfType<Button>().ToArray();
            Require(actions.Single(b => b.IsDefault).Tag is AppDialogResult.Cancelled, "The update confirmation defaults to applying a change.");
            Require(actions.Single(b => b.Tag is AppDialogResult.Confirmed).IsDefault == false, "Install/download was the default action.");
            await RenderAsync(dialog, Path.Combine(output, $"{consent.Kind}-{(consent.Kind == UpdateConsentKind.Download ? downloads : installs)}.png"));
            actions.Single(b => b.Tag is AppDialogResult result && result == (accept ? AppDialogResult.Confirmed : AppDialogResult.Cancelled))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return await response == AppDialogResult.Confirmed;
        }
        await using (var runtime = InstalledUpdateRuntime.Open(workspace))
        await using (var session = Create(runtime, hold: true))
        {
            owner = new UpdaterWindow(session, AppTheme.Dark, confirm: Consent); owner.Show();
            Click(owner, "VerifyButton"); await owner.ActiveAction!.WaitAsync(TimeSpan.FromMinutes(2));
            Require(session.Snapshot.Phase == UpdateJobPhase.Available && session.Snapshot.Bytes == 0 && !Directory.Exists(workspace.Resolve("job-stage")) && !runtime.IsPrepared,
                "Declining the download did not leave an untouched available update: " + session.Snapshot.Phase + "/" + session.Snapshot.Reason);
            Record("download-declined-no-package-or-preparation", session.Snapshot);
            Click(owner, "VerifyButton"); await reached.Task.WaitAsync(TimeSpan.FromMinutes(2));
            owner.Refresh(true); Click(owner, "PauseButton"); await owner.ActiveAction!.WaitAsync(TimeSpan.FromSeconds(30));
            Require(session.Snapshot.Phase == UpdateJobPhase.Paused && session.Snapshot.Bytes >= 131072 && session.Snapshot.DownloadApproved,
                "HTTPS pause did not retain approved partial bytes.");
            Record("real-https-paused", session.Snapshot);
            owner.Close(); await owner.WindowClosed.WaitAsync(TimeSpan.FromSeconds(30));
        }
        var checkpoint = OwnedWorkspace.ReadJson<DownloadCheckpoint>(workspace.Resolve("job-download/download.json"));
        using (var probe = feed.CreateClient())
        using (var request = new HttpRequestMessage(HttpMethod.Get, checkpoint.Package.Url))
        {
            long offset = new FileInfo(workspace.Resolve("job-download/package.partial")).Length;
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (checkpoint.ETag != null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(checkpoint.ETag));
            using var response = await probe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Require(response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset,
                "The real release CDN did not honor the saved Range request.");
        }
        await using var active = InstalledUpdateRuntime.Open(workspace);
        var gate = new FeedUiSmoke.DeploymentGate(active);
        await using var update = Create(active, gate);
        owner = new UpdaterWindow(update, AppTheme.Dark, confirm: Consent); owner.Show();
        Click(owner, "PauseButton"); await owner.ActiveAction!.WaitAsync(TimeSpan.FromMinutes(3));
        Require(update.Snapshot.Phase == UpdateJobPhase.Ready && downloads == 2 && update.Snapshot.Source!.StartsWith(HttpsStagingFeed.Origin, StringComparison.Ordinal),
            "The approved download did not resume and verify without repeating its prompt.");
        Record("real-https-range-resume-after-reopen", update.Snapshot);
        Click(owner, "InstallButton"); await owner.ActiveAction!.WaitAsync(TimeSpan.FromSeconds(30));
        Require(update.Snapshot.Phase == UpdateJobPhase.Ready && !active.IsPrepared && active.Inspect().InstalledVersion == "1.0.0",
            "Declining installation changed or prepared the original app.");
        Record("install-declined-original-kept", update.Snapshot);
        active.BeforeCandidateStart = _ => throw new IOException("intentional staged-candidate startup failure");
        Click(owner, "InstallButton"); await owner.ActiveAction!.WaitAsync(TimeSpan.FromMinutes(7));
        Require(update.Snapshot.Phase == UpdateJobPhase.RolledBack && !active.IsPrepared && active.Inspect().InstalledVersion == "1.0.0",
            "Failed-start recovery did not restore the original app.");
        Record("real-https-failed-start-rollback", update.Snapshot);
        active.BeforeCandidateStart = null;
        Click(owner, "VerifyButton"); await owner.ActiveAction!.WaitAsync(TimeSpan.FromMinutes(3));
        Require(update.Snapshot.Phase == UpdateJobPhase.Ready, "The original app could not stage its retry.");
        Require((await active.PrepareAsync(CancellationToken.None)).Ready, "The recovered app could not prepare safely.");
        await active.CancelPreparationAsync(CancellationToken.None); await active.ExerciseAutoSaveAsync();
        gate.Hold = true;
        Click(owner, "InstallButton"); await gate.Entered.Task.WaitAsync(TimeSpan.FromMinutes(2));
        owner.Close(); Require(!owner.IsVisible && !owner.WindowClosed.IsCompleted, "Closing interrupted an armed installation.");
        gate.Release.TrySetResult(); await owner.WindowClosed.WaitAsync(TimeSpan.FromMinutes(7));
        Require(update.Snapshot.Phase == UpdateJobPhase.Completed && active.Inspect().InstalledVersion == "1.0.1", "The HTTPS update did not commit.");
        FeedUiSmoke.AssertSavedSnapshot(workspace);
        var recovery = new DeploymentRecovery(workspace).Read();
        using var running = Process.GetProcessById(recovery.Candidate!.Pid);
        Require(!running.HasExited && running.StartTime.ToUniversalTime().Ticks == recovery.Candidate.StartedUtcTicks, "The updated app was not left running.");
        if (recovery.InstallerOptions != null) Program.VerifyOptions(recovery.InstallerOptions);
        Record("approved-commit-close-autosave-options", update.Snapshot);
        OwnedWorkspace.WriteJson(Path.Combine(output, "staging-summary.json"), new
        {
            origin = HttpsStagingFeed.Origin,
            publicLiveUpdates = false,
            downloads,
            installs,
            range206 = true,
            normalTlsValidation = true,
            declinedActionsDidNotStageOrPrepare = true,
            autosavePreserved = true,
            realAppKeptRunning = true,
            rollbackPassed = true
        });
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
}

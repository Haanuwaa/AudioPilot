using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater.Validation;

internal static class DeltaFeedSmoke
{
    internal static async Task RunAsync(string baseline, string candidate, string settings, OwnedWorkspace output)
    {
        MsiDeployment.RequireSandbox();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        CancellationToken token = deadline.Token;
        string delta = output.Resolve(PortableDelta.Name(Path.GetFileName(candidate), "1.0.0"));
        await PortableDelta.CreateAsync(baseline, candidate, "1.0.0", delta, token);
        Require(new FileInfo(delta).Length < new FileInfo(candidate).Length * 0.75, "The fixture needs a useful delta.");
        string baselineSha;
        using (var input = File.OpenRead(baseline)) baselineSha = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        var results = new List<object>();
        foreach (string scenario in new[] { "resume-commit", "interrupted-staging", "missing-delta", "corrupt-baseline", "startup-failure" })
        {
            string diagnosticId = Guid.NewGuid().ToString("N");
            var workspace = new OwnedWorkspace(output.Resolve(scenario));
            OwnedWorkspace.WriteJson(workspace.Resolve(PortableLayout.FileName), new PortableLayoutDescriptor(1, diagnosticId));
            var portable = new PortableDeployment(workspace);
            var installed = await portable.ApplyAsync(baseline, new FileInfo(baseline).Length, baselineSha, "1.0.0", (_, _) => Task.FromResult(true), token);
            DeploymentLifecycleSmoke.InstallLauncher(workspace);
            string data = workspace.Resolve("data");
            await RealAppLifecycleSmoke.PrepareSettingsAsync(settings, data);
            if (scenario == "corrupt-baseline") File.AppendAllText(workspace.Resolve($"versions/{installed.Current}/licenses/LICENSE"), "changed baseline");
            string pin = await TufLocalTrustClient.CreateFixtureAsync(workspace, candidate, "portable", delta, token);
            await using var server = new HttpsFeedServer(workspace, candidate, scenario == "missing-delta" ? null : delta);
            using var client = server.Feed.CreateClient();
            var trust = new TufLocalTrustClient(workspace, pin, server.Feed);
            var context = new FixtureJobContext(1, "portable", diagnosticId, data, false,
                scenario == "startup-failure" ? "startup-failure" : "commit", ExerciseAutoSave: true);
            if (scenario == "resume-commit")
            {
                var chunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var pauseRuntime = new LocalFixtureJobRuntime(workspace, context);
                await using var pausing = new LocalUpdateJob(workspace, trust, pauseRuntime,
                    async (_, cancellation) => { chunk.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); });
                Require((await pausing.CheckAsync(server.Feed, token)).Phase == UpdateJobPhase.Available && server.PackageRequests == 0,
                    "Checking contacted the package endpoint.");
                var staged = pausing.DownloadAsync(server.Feed, client, token);
                await chunk.Task.WaitAsync(TimeSpan.FromSeconds(45), token);
                Require(pausing.Pause(), "Delta pause was not accepted.");
                var paused = await staged;
                Require(paused.Phase == UpdateJobPhase.Paused && paused.Delta != null && paused.Bytes > 0, "Delta pause was not durable.");
            }
            string? interruptedPath = null;
            if (scenario == "interrupted-staging")
            {
                UpdateJobState previous;
                await using var interruptedRuntime = new LocalFixtureJobRuntime(workspace, context);
                await using (var interrupted = new LocalUpdateJob(workspace, trust, interruptedRuntime))
                {
                    previous = await interrupted.CheckAsync(server.Feed, token);
                    Require(previous.Phase == UpdateJobPhase.Available, "Interrupted staging fixture could not authorize the release.");
                }
                var authorization = await trust.AuthorizeAsync(server.Feed.Target, interruptedRuntime.Inspect(), token);
                interruptedPath = UpdateStorage.CreateDirectory(workspace, Path.Combine(PortableDelta.DirectoryName, "1.0.1_" + Guid.NewGuid().ToString("N")), "generation");
                File.WriteAllText(Path.Combine(interruptedPath, "AudioPilot.exe"), "incomplete");
                File.Copy(delta, workspace.Resolve("job-delta/delta.zip"));
                OwnedWorkspace.WriteJson(workspace.Resolve("job.json"), previous with
                {
                    Phase = UpdateJobPhase.Verifying,
                    Bytes = authorization.Delta!.Length,
                    Delta = authorization.Delta
                });
            }
            await using var runtime = new LocalFixtureJobRuntime(workspace, context);
            await using var job = new LocalUpdateJob(workspace, trust, runtime);
            if (scenario == "startup-failure") await runtime.StartOriginalAsync();
            var ready = await job.DownloadAsync(server.Feed, client, token);
            Require(ready.Phase == UpdateJobPhase.Ready, "Delta job did not reach Ready: " + ready.Reason);
            bool usingDelta = scenario is not ("corrupt-baseline" or "missing-delta");
            Require((ready.PreparedGeneration != null) == usingDelta, "Job chose the wrong delivery path.");
            if (interruptedPath != null)
                Require(!Directory.Exists(interruptedPath) && server.PackageRequests == 0, "Interrupted staging did not reuse the verified download and replace its incomplete tree.");
            else if (scenario == "missing-delta")
                Require(server.PackageRequests == 1 && server.DeltaRequests == 0, "Missing delta did not fall back to the full package.");
            else
                Require(server.DeltaRequests > 0 && (server.PackageRequests == server.DeltaRequests) == usingDelta, "Unexpected full-package transfer.");
            Require(scenario != "resume-commit" || server.ResumedRequests > 0, "Delta resume did not use an HTTPS byte range.");
            Require(portable.ReadState().Current == installed.Current, "Preparation changed the active generation.");
            var result = await job.ApplyAsync(token);
            Require(result.Phase == (scenario == "startup-failure" ? UpdateJobPhase.RolledBack : UpdateJobPhase.Completed),
                "The real app did not settle the update: " + result.Phase + "/" + result.Reason);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "settings.json"), token));
            Require(saved?["App"]?["Theme"]?.GetValue<string>() == "Light" && !new DeploymentRecovery(workspace).IsPending, "Final autosave or recovery was lost.");
            results.Add(new { scenario, usingDelta, server.PackageRequests, server.ResumedRequests, phase = result.Phase.ToString(), autosave = true });
            OwnedWorkspace.WriteJson(output.Resolve("delta-feed-results.json"), results);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

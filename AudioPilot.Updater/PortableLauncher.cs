using AudioPilot.UpdateProtocol;

namespace AudioPilot.Updater;

/// <summary>Launches the managed portable application or its recovery helper through the stable entry point.</summary>
internal static class PortableLauncher
{
    public static async Task<int> RunAsync(OwnedWorkspace workspace, bool probe = false, string[]? arguments = null)
    {
        if (UpdateClientPolicy.Current != null) UpdateClientPolicy.RequirePackaged();
        else MsiDeployment.RequireSandbox();
        using var ownership = new FileStream(workspace.Resolve("lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var layout = OwnedWorkspace.ReadJson<PortableLayoutDescriptor>(workspace.Resolve(PortableLayout.FileName));
        if (layout.Schema != 1 || !Guid.TryParseExact(layout.DiagnosticId, "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid portable launch configuration.");
        var deployment = new PortableDeployment(workspace);
        PortableDocuments.Synchronize(workspace, deployment.ReadState(), onlyIfNeeded: true);
        var recovery = new DeploymentRecovery(workspace);
        if (layout.Managed && UpdateClientPolicy.Current != null)
        {
            string executable;
            string[] launchArguments;
            if (recovery.IsPending)
            {
                var record = recovery.Read();
                if (record.DiagnosticId != layout.DiagnosticId || !record.PreviousApp.StartsWith(workspace.Resolve("versions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Portable recovery belongs to another installation.");
                executable = UpdateRuntimeStager.Stage(Path.GetDirectoryName(record.PreviousApp)!, CancellationToken.None);
                launchArguments = ["--pilot-recover", workspace.Root];
            }
            else
            {
                var state = deployment.ReadState();
                if (state.PendingHealth) throw new InvalidDataException("The portable recovery journal is missing.");
                executable = CurrentApp(workspace, state); launchArguments = arguments ?? [];
            }
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (string argument in launchArguments) start.ArgumentList.Add(argument);
            start.Environment.Remove("AUDIOPILOT_DIAGNOSTIC_SESSION"); start.Environment.Remove("AUDIOPILOT_UPDATE_TEST_SESSION"); start.Environment.Remove(UpdateLifecycleWire.EnvironmentVariable);
            using var launched = System.Diagnostics.Process.Start(start) ?? throw new IOException("The portable pilot could not start.");
            return 0;
        }
        if (layout.Managed)
        {
            await using var runtime = InstalledUpdateRuntime.Open(workspace);
            if (recovery.IsPending) { ownership.Dispose(); _ = await runtime.RecoverAsync(); return 0; }
            await using var managed = await SupervisedApp.LaunchAsync(CurrentApp(workspace, deployment.ReadState()), layout.DiagnosticId, false, false, managed: true);
            if ((await managed.ReadAsync()).Kind != "ready") throw new IOException("The managed portable app did not become ready.");
            await managed.SendAsync("detach");
            if ((await managed.ReadAsync()).Kind != "detached") throw new IOException("Portable launch ownership was not released.");
            return 0;
        }
        if (recovery.IsPending)
        {
            var record = recovery.Read();
            if (record.DiagnosticId != layout.DiagnosticId) throw new InvalidDataException("Portable recovery identity mismatch.");
            await recovery.WaitForCandidateExitAsync();
            if (record.DeploymentCommitted && deployment.ReadState().PendingHealth)
                throw new InvalidDataException("Portable deployment and recovery journals disagree.");
            var state = deployment.Recover();
            string current = CurrentApp(workspace, state);
            bool rolledBack = string.Equals(current, record.PreviousApp, StringComparison.OrdinalIgnoreCase);
            if (rolledBack && record.DeploymentCommitted) throw new InvalidDataException("Committed portable generation unexpectedly changed.");
            if (rolledBack) recovery.RestoreConfiguration(workspace.Resolve("data"));
            // A durable committed pointer wins even if the helper died before recording its own acknowledgement.
            await using var resumed = await SupervisedApp.LaunchAsync(current, layout.DiagnosticId, true, record.Visible, recovery.SetCandidate);
            await resumed.WaitStagedAsync();
            await resumed.CommitAsync();
            recovery.Complete();
            ownership.Dispose();
            if (probe) await resumed.StopNormallyAsync();
            else await resumed.Process.WaitForExitAsync();
            return 0;
        }
        var settled = deployment.ReadState();
        if (settled.PendingHealth) throw new InvalidDataException("Portable recovery journal is missing; refusing an uncertain launch.");
        await using var app = await SupervisedApp.LaunchAsync(CurrentApp(workspace, settled), layout.DiagnosticId, false, false);
        if ((await app.ReadAsync()).Kind != "ready") throw new InvalidOperationException("Portable app did not finish normal startup.");
        ownership.Dispose();
        if (probe) await app.StopNormallyAsync();
        else await app.Process.WaitForExitAsync();
        return 0;
    }

    public static string CurrentApp(OwnedWorkspace workspace, PortableState state) =>
        state.Current == null ? throw new InvalidOperationException("No installed portable generation.") :
        workspace.Resolve(System.IO.Path.Combine("versions", state.Current, "AudioPilot.exe"));
}

using System.IO;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.UpdateProtocol;
using AudioPilot.ViewModels;

namespace AudioPilot.Services.Updates;

internal static class UpdateCommandService
{
    internal static async Task<CliExecutionResult> ExecuteAsync(CliCommand command, UpdateCheckViewModel? checks, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (command.Key is not ("status" or "check"))
            {
                if (command.Key is "apply" or "resume" && !command.ApproveUpdate)
                    return Error(command, "update-approval-required", "An exact version and --approve are required.");
                if (!AppUpdateLauncher.IsAvailable)
                    return Error(command, "public-updates-disabled", "Integrated updates are disabled in this build. Use the manual release link.");
                var control = await AppUpdateLauncher.ControlAsync(command.Key!, command.Value, command.ApproveUpdate, checks != null, token);
                return new(control.Accepted ? 0 : 3, command.JsonOutput ? CliOutputFormatter.SerializeCliJson(control)
                    : control.Accepted ? $"Update {command.Key} accepted. Phase: {control.Phase}. Use update status to inspect the result."
                    : $"[diag-code:{control.Error}] Update {command.Key} was not accepted ({control.Phase}).");
            }

            UpdateCheckSnapshot? snapshot;
            if (checks != null) snapshot = command.Key == "check" ? await checks.CheckNowAsync(token) : checks.Snapshot;
            else
            {
                snapshot = UpdateCheckSnapshotStore.Read();
                if (command.Key == "check") snapshot = await CheckHeadlessAsync(snapshot, token);
            }
            string installed = VersionOfApp();
            string releaseUrl = snapshot?.LatestVersion is { } latest ? AppConstants.Links.RepositoryUrl + "/releases/tag/v" + Uri.EscapeDataString(latest)
                : AppConstants.Links.RepositoryUrl + "/releases/latest";
            bool available = snapshot?.LatestVersion is { } candidate && Version.TryParse(candidate, out var version) &&
                (version > Version.Parse(installed) || IsPreview() && version == Version.Parse(installed));
            UpdaterControlReply? job = null;
            bool activeJob = false;
            string? jobError = null;
            try
            {
                if (AppUpdateLauncher.IsAvailable && UpdaterControlWire.ReadEndpoint(UpdaterControlWire.Workspace(AppContext.BaseDirectory)) is { } endpoint)
                {
                    job = await UpdaterControlWire.SendAsync(endpoint, new(1, "status"), token);
                    activeJob = true;
                }
                else if (AppUpdateLauncher.IsAvailable) job = UpdaterControlWire.ReadLastResult(UpdaterControlWire.Workspace(AppContext.BaseDirectory));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            { jobError = "update-control-unavailable"; }
            var output = new
            {
                InstalledVersion = installed,
                snapshot?.LatestVersion,
                ReleaseAvailable = available,
                ReleaseUrl = releaseUrl,
                snapshot?.LastCheckedUtc,
                snapshot?.RetryAfterUtc,
                snapshot?.ErrorCode,
                PublicUpdatesEnabled = UpdateClientPolicy.Current?.PublicUpdatesEnabled == true,
                IntegratedUpdaterAvailable = AppUpdateLauncher.IsAvailable,
                PackageEligibility = activeJob && job?.Phase is "Available" or "Paused" or "Ready" or "Deferred" ? "verified-for-job" : "not-checked",
                ActiveJob = activeJob,
                Job = job,
                JobError = jobError
            };
            int code = command.Key == "check" && (snapshot?.ErrorCode != null || snapshot?.RetryAfterUtc > DateTimeOffset.UtcNow) ? 3 : 0;
            string text = $"Installed: {installed}\nLatest release: {snapshot?.LatestVersion ?? "not checked"}\n" +
                $"Release available: {available}\nIntegrated updates: {(AppUpdateLauncher.IsAvailable ? UpdateClientPolicy.Current?.PublicUpdatesEnabled == true ? "enabled, approval required" : "opt-in" : "disabled")}\n" +
                $"Last checked: {snapshot?.LastCheckedUtc?.ToString("u") ?? "not checked"}\nManual release: {releaseUrl}" +
                (snapshot?.ErrorCode != null ? "\nCheck error: " + snapshot.ErrorCode : "") +
                (jobError != null ? "\nUpdate job: unavailable (retry status before another mutation)." : "") +
                (job != null ? $"\n{(activeJob ? "Active" : "Last")} update job: {job.Phase}; version={job.Version ?? "none"}; bytes={job.Bytes}; reason={job.Error ?? job.Reason ?? "none"}" : "");
            return new(code, command.JsonOutput ? CliOutputFormatter.SerializeCliJson(output) : text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            Logger.Instance.Warning("UpdateCheck", () => $"update-cli-unavailable | operation={command.Key} error={error.GetType().Name}");
            return Error(command, "update-control-unavailable", "The updater command could not be confirmed. Check status before retrying.");
        }
    }

    internal static async Task<UpdateCheckSnapshot> CheckHeadlessAsync(UpdateCheckSnapshot? previous, CancellationToken token,
        Func<CancellationToken, Task<PublishedRelease?>>? fetch = null, Func<bool>? automaticChecks = null,
        Action<UpdateCheckSnapshot>? save = null, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        if (previous?.RetryAfterUtc > clock.GetUtcNow()) return previous;
        string installed = VersionOfApp();
        bool automatic = automaticChecks?.Invoke() ?? new SettingsService().LoadSettingsReadOnly().Miscellaneous.CheckForUpdates;
        UpdateCheckSnapshot snapshot;
        try
        {
            var release = await (fetch ?? new GitHubReleaseService(timeProvider: clock).GetLatestAsync)(token).ConfigureAwait(false);
            snapshot = new(installed, release?.Version.ToString(), release?.Url.AbsoluteUri ?? AppConstants.Links.RepositoryUrl + "/releases/latest",
                (release?.Version > Version.Parse(installed) || IsPreview() && release?.Version == Version.Parse(installed)), automatic, clock.GetUtcNow(), null, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {
            TimeSpan? cooldown = error is ReleaseCheckException rate ? rate.RetryAfter : null;
            snapshot = new(installed, previous?.LatestVersion, previous?.ReleaseUrl ?? AppConstants.Links.RepositoryUrl + "/releases/latest",
                previous?.UpdateAvailable ?? false, automatic, previous?.LastCheckedUtc, "update-check-failed",
                cooldown > TimeSpan.Zero ? clock.GetUtcNow() + TimeSpan.FromTicks(Math.Min(cooldown.Value.Ticks, (DateTimeOffset.MaxValue - clock.GetUtcNow()).Ticks)) : null);
        }
        if (save != null) save(snapshot);
        else UpdateCheckSnapshotStore.Save(snapshot, Logger.Instance);
        return snapshot;
    }
    private static bool IsPreview() => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(UpdateCommandService).Assembly)?.InformationalVersion.Split('+')[0].Contains('-') == true;
    private static string VersionOfApp()
    {
        var version = typeof(UpdateCommandService).Assembly.GetName().Version!;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build)).ToString();
    }
    private static CliExecutionResult Error(CliCommand command, string code, string message) =>
        CliCommandExecutor.BuildExecutionFailureResult(3, code, message, command.JsonOutput);
}

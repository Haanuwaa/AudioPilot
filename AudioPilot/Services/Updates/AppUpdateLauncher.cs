using System.Diagnostics;
using System.IO;
using AudioPilot.Localization;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Services.Updates;

/// <summary>Stages the packaged updater outside the replaceable installation and joins its authenticated control channel.</summary>
public static class AppUpdateLauncher
{
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);
    public static bool IsAvailable => UpdateClientPolicy.Current?.IsEnabled == true;
    public static LocalizedText AvailabilityLabel => new(IsAvailable ? "Update_IntegrationAvailable" : "Update_IntegrationDisabled");

    internal static async Task LaunchAsync(string session, string theme, CancellationToken token)
    {
        var endpoint = await GetEndpointAsync(true, session, theme, token).ConfigureAwait(false);
        var reply = await UpdaterControlWire.SendAsync(endpoint!, new(1, "show"), token).ConfigureAwait(false);
        if (!reply.Accepted) throw new IOException("The active updater window could not be opened.");
    }

    internal static async Task RefreshLanguageAsync(string language, CancellationToken token)
    {
        var endpoint = await GetEndpointAsync(false, null, null, token).ConfigureAwait(false);
        if (endpoint != null)
            await UpdaterControlWire.SendAsync(endpoint, new(1, "language", Language: language), token).ConfigureAwait(false);
    }

    internal static async Task<UpdaterControlReply> ControlAsync(string operation, string? version, bool approved, bool canStart, CancellationToken token)
    {
        var endpoint = await GetEndpointAsync(canStart && operation is "apply" or "resume", null, null, token).ConfigureAwait(false);
        return endpoint == null ? new(1, false, "Idle", Error: "update-not-running")
            : await UpdaterControlWire.SendAsync(endpoint, new(1, operation, version, approved), token).ConfigureAwait(false);
    }

    private static async Task<UpdaterEndpoint?> GetEndpointAsync(bool startIfNeeded, string? session, string? theme, CancellationToken token)
    {
        UpdateClientPolicy.RequireConfigured();
        await LaunchGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string workspace = UpdaterControlWire.Workspace(AppContext.BaseDirectory);
            if (UpdaterControlWire.ReadEndpoint(workspace) is { } running) return running;
            if (!startIfNeeded) return null;
            session ??= AppUpdateLifecycleSession.AttachmentSession ?? throw new InvalidOperationException("A running AudioPilot instance is required to begin an update.");
            string helper = await Task.Run(() => UpdateRuntimeStager.Stage(AppContext.BaseDirectory, token), token).ConfigureAwait(false);
            using var self = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
            foreach (string arg in new[] { theme == null ? "--automation" : "--pilot-ui", Path.Combine(AppContext.BaseDirectory, "AudioPilot.exe"), workspace, session,
                self.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), self.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            if (theme != null) { start.ArgumentList.Add("--theme"); start.ArgumentList.Add(theme); }
            start.Environment.Remove(UpdateLifecycleWire.EnvironmentVariable);
            start.Environment[UpdaterControlWire.LanguageEnvironmentVariable] = LocalizationService.Default.Language;
            start.Environment.Remove("AUDIOPILOT_DIAGNOSTIC_SESSION"); start.Environment.Remove("AUDIOPILOT_UPDATE_TEST_SESSION");
            token.ThrowIfCancellationRequested();
            using var child = Process.Start(start) ?? throw new IOException("The updater did not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            UpdaterEndpoint? endpoint;
            while ((endpoint = UpdaterControlWire.ReadEndpoint(workspace)) == null)
            {
                if (child.HasExited) throw new IOException("The updater exited before its control channel was ready.");
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            return endpoint;
        }
        finally { LaunchGate.Release(); }
    }
}

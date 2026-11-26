using System.Diagnostics;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Services.Updates;

/// <summary>Stages the packaged updater outside the replaceable installation and joins its authenticated control channel.</summary>
public static class AppUpdateLauncher
{
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);
    public static bool IsAvailable => UpdateClientPolicy.Current?.IsEnabled == true;
    public static LocalizedText AvailabilityLabel => new(IsAvailable ? "Update_IntegrationAvailable" : "Update_IntegrationDisabled");

    internal static async Task<bool> TryShowExistingAsync(AppTheme theme, CancellationToken token)
    {
        var connection = await Task.Run(() => GetEndpointAsync(false, null, null, null, token), token).ConfigureAwait(false);
        if (connection == null) return false;
        var reply = await UpdaterControlWire.SendAsync(connection.Value.Endpoint, new(1, "show", Theme: theme.ToString()), token).ConfigureAwait(false);
        if (!reply.Accepted) throw new IOException("The active updater window could not be opened.");
        return true;
    }

    internal static async Task LaunchAsync(string session, AppTheme theme, Func<Task<UpdaterWindowPlacement?>> placement, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string name = UpdaterControlWire.StartupSignalPrefix + Guid.NewGuid().ToString("N");
        using var abandoned = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var cancellation = token.Register(() => abandoned.Set());
        Connection? connection = null;
        bool presented = false;
        try
        {
            connection = await GetEndpointAsync(true, session, theme.ToString().ToLowerInvariant(), name, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var bounds = await placement().ConfigureAwait(false);
            var reply = await UpdaterControlWire.SendAsync(connection!.Value.Endpoint, new(1, "show", Window: bounds, Theme: theme.ToString()), token).ConfigureAwait(false);
            if (!reply.Accepted) throw new IOException("The active updater window could not be opened.");
            presented = true;
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally
        {
            if (!presented)
            {
                abandoned.Set();
                if (connection is { Started: true } started)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await UpdaterControlWire.SendAsync(started.Endpoint, new(1, "abandon-startup"), cleanup.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException) { }
                }
            }
        }
    }

    internal static async Task RefreshPresentationAsync(string language, AppTheme theme, CancellationToken token)
    {
        var connection = await Task.Run(() => GetEndpointAsync(false, null, null, null, token), token).ConfigureAwait(false);
        if (connection != null)
            await UpdaterControlWire.SendAsync(connection.Value.Endpoint, new(1, "presentation", Language: language, Theme: theme.ToString()), token).ConfigureAwait(false);
    }

    internal static async Task<UpdaterControlReply> ControlAsync(string operation, string? version, bool approved, bool canStart, CancellationToken token)
    {
        var connection = await GetEndpointAsync(canStart && operation is "apply" or "resume", null, null, null, token).ConfigureAwait(false);
        return connection == null ? new(1, false, "Idle", Error: "update-not-running")
            : await UpdaterControlWire.SendAsync(connection.Value.Endpoint, new(1, operation, version, approved), token).ConfigureAwait(false);
    }

    private static Func<string?>? _attachmentSessionResolver;

    internal static void RegisterAttachmentSessionResolver(Func<string?> resolver) => _attachmentSessionResolver = resolver;

    private readonly record struct Connection(UpdaterEndpoint Endpoint, bool Started);

    private static async Task<Connection?> GetEndpointAsync(bool startIfNeeded, string? session, string? theme, string? startupSignal, CancellationToken token)
    {
        UpdateClientPolicy.RequireConfigured();
        UpdateClientPolicy.RequireUnelevated();
        await LaunchGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string workspace = UpdaterControlWire.Workspace(AppContext.BaseDirectory);
            if (UpdaterControlWire.ReadEndpoint(workspace) is { } running) return new(running, false);
            if (!startIfNeeded) return null;
            session ??= _attachmentSessionResolver?.Invoke() ?? throw new InvalidOperationException("A running AudioPilot instance is required to begin an update.");
            string helper = await Task.Run(() => UpdateRuntimeStager.Stage(AppContext.BaseDirectory, token), token).ConfigureAwait(false);
            using var self = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
            foreach (string arg in new[] { "--automation", Path.Combine(AppContext.BaseDirectory, "AudioPilot.exe"), workspace, session,
                self.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), self.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            if (startupSignal != null) { start.ArgumentList.Add("--startup-event"); start.ArgumentList.Add(startupSignal); }
            if (theme != null) { start.ArgumentList.Add("--theme"); start.ArgumentList.Add(theme); }
            start.Environment.Remove(UpdateLifecycleWire.EnvironmentVariable);
            start.Environment[UpdaterControlWire.LanguageEnvironmentVariable] = LocalizationService.Default.Language;
            start.Environment.Remove("AUDIOPILOT_DIAGNOSTIC_SESSION"); start.Environment.Remove("AUDIOPILOT_UPDATE_TEST_SESSION");
            token.ThrowIfCancellationRequested();
            using var child = Process.Start(start) ?? throw new IOException("The updater did not start.");
            // GUI opening joins readiness to retire its own helper; headless commands retain prompt cancellation.
            using var deadline = startupSignal == null ? CancellationTokenSource.CreateLinkedTokenSource(token) : new CancellationTokenSource();
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            UpdaterEndpoint? endpoint;
            while ((endpoint = UpdaterControlWire.ReadEndpoint(workspace)) == null)
            {
                if (child.HasExited) throw new IOException("The updater exited before its control channel was ready.");
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            return new(endpoint, endpoint.Pid == child.Id && endpoint.Started == child.StartTime.ToUniversalTime().Ticks);
        }
        finally { LaunchGate.Release(); }
    }
}

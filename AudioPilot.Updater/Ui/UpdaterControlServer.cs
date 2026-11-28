using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Updater.Ui;

/// <summary>Serializes control admission; transfer and installation stay owned by the existing session.</summary>
internal sealed class UpdaterControlServer : IAsyncDisposable
{
    private readonly UpdaterSession _session;
    private readonly OwnedWorkspace _workspace;
    private readonly Func<Func<UpdaterControlReply>, Task<UpdaterControlReply>> _dispatch;
    private readonly CancellationTokenSource _stop = new();
    private readonly UpdaterEndpoint _endpoint;
    private readonly Task _server;
    private readonly Func<UpdaterWindowPlacement?, Task<bool>>? _show;
    private readonly Action? _abandonStartup;
    private Task<bool>? _presentation;
    private readonly Action<string> _setLanguage;
    private readonly Action<AppTheme>? _setTheme;
    private Task<UpdateJobState>? _approved;
    private readonly Lock _approvalSync = new();
    private CancellationTokenSource? _approvalCancellation;
    private string? _commandError;
    private Task? _dispose;
    private bool _stopping;
    internal Task? ActiveApproval => _approved;
    internal bool StartupAbandoned { get; private set; }

    internal UpdaterControlServer(UpdaterSession session, OwnedWorkspace workspace,
        Func<Func<UpdaterControlReply>, Task<UpdaterControlReply>>? dispatch = null,
        Func<UpdaterWindowPlacement?, Task<bool>>? show = null,
        Action<string>? setLanguage = null, Action? abandonStartup = null, Action<AppTheme>? setTheme = null)
    {
        _session = session; _workspace = workspace; _show = show; _abandonStartup = abandonStartup;
        _setLanguage = setLanguage ?? LocalizationService.Default.SetLanguage;
        _setTheme = setTheme;
        _dispatch = dispatch ?? (action => Task.FromResult(action()));
        using var self = Process.GetCurrentProcess();
        _endpoint = new(1, "AudioPilot.Updater." + Guid.NewGuid().ToString("N"), self.Id, self.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
        OwnedWorkspace.WriteJson(UpdaterControlWire.EndpointPath(workspace.Root), _endpoint);
        _server = ServeAsync();
    }

    internal UpdaterControlReply Handle(UpdaterControlRequest request)
    {
        var reply = HandleCore(request);
        if (request.Operation is not ("status" or "language" or "presentation"))
            UpdateLog.Write(_session.Diagnostics, new(reply.Accepted ? UpdateLogKind.ControlAccepted : UpdateLogKind.ControlRejected,
                _session.Snapshot.Phase, request.Operation, request.Version, reply.Error));
        return reply;
    }

    private UpdaterControlReply HandleCore(UpdaterControlRequest request)
    {
        if (request.Schema != 1 || request.Operation is not ("status" or "apply" or "resume" or "pause" or "cancel" or "show" or "language" or "presentation" or "abandon-startup")
            || (request.Operation is "language" or "presentation") != (request.Language != null) ||
            (request.Operation != "show" && (request.Operation == "presentation") != (request.Theme != null)) ||
            request.Window != null && (request.Operation != "show" || !request.Window.IsValid))
            return Reply(false, "invalid-update-command");
        if (request.Operation != "status" && (_stopping || _session.IsClosing || _session.IsClosed)) return Reply(false, "update-closing");
        bool mutation = request.Operation is "apply" or "resume";
        if (mutation ? !request.Approve || !Version.TryParse(request.Version, out var version) || version.Build < 0 || version.Revision != -1 || version.ToString(3) != request.Version
            : request.Approve || request.Version != null) return Reply(false, "update-approval-required");
        AppTheme? theme = request.Theme switch
        {
            nameof(AppTheme.Dark) => AppTheme.Dark,
            nameof(AppTheme.Light) => AppTheme.Light,
            nameof(AppTheme.System) => AppTheme.System,
            _ => null
        };
        if (request.Theme != null && (!theme.HasValue || _setTheme == null))
            return Reply(false, "invalid-update-theme");
        switch (request.Operation)
        {
            case "language":
            case "presentation":
                if (request.Language is not { } language || !LocalizationService.Languages.Any(option => option.Code == language))
                    return Reply(false, "invalid-update-language");
                if (theme is { } presentationTheme) _setTheme!(presentationTheme);
                _setLanguage(language);
                return Reply(true);
            case "show":
                if (_show == null) return Reply(false, "update-window-unavailable");
                if (theme is { } showTheme) _setTheme!(showTheme);
                _presentation = PresentAsync(request.Window); return Reply(true);
            case "abandon-startup":
                if (_abandonStartup == null || _session.IsBusy || _approved is { IsCompleted: false }) return Reply(false, "update-busy");
                _stopping = true;
                StartupAbandoned = true;
                _abandonStartup();
                return Reply(true);
            case "status": return Reply(true, _commandError);
            case "pause":
                return Reply(_session.Pause(), "update-not-pausable");
            case "cancel":
                bool revoked = RevokeApproval();
                return Reply(_session.Cancel() || revoked && _session.Snapshot.Phase == UpdateJobPhase.Cancelled, "update-not-cancellable");
            default:
                if (_session.IsBusy || _approved is { IsCompleted: false }) return Reply(false, "update-busy");
                _commandError = null;
                _approved = ApplyApprovedAsync(request.Version!);
                return Reply(true);
        }
    }

    private async Task<bool> PresentAsync(UpdaterWindowPlacement? placement)
    {
        try { return await _show!(placement).ConfigureAwait(false); }
        catch (Exception error)
        {
            UpdateLog.Write(_session.Diagnostics, new(UpdateLogKind.OperationFailed, _session.Snapshot.Phase, "show", Error: error));
            return false;
        }
    }

    private async Task<UpdateJobState> ApplyApprovedAsync(string version)
    {
        using var consent = new CancellationTokenSource();
        lock (_approvalSync) _approvalCancellation = consent;
        try
        {
            var state = _session.Snapshot;
            bool checkedFeed = false;
            if (state.Phase is not (UpdateJobPhase.Available or UpdateJobPhase.Paused or UpdateJobPhase.Ready or UpdateJobPhase.Deferred or UpdateJobPhase.Failed) || state.Artifact == null)
            {
                state = await _session.CheckAsync(consent.Token);
                checkedFeed = true;
            }
            consent.Token.ThrowIfCancellationRequested();
            if (state.Phase == UpdateJobPhase.Cancelled) { _commandError = "update-cancelled"; return state; }
            if (state.Phase == UpdateJobPhase.Unavailable || (state.Phase == UpdateJobPhase.Failed && (checkedFeed || state.Artifact == null)))
            { _commandError = state.Phase == UpdateJobPhase.Failed ? "update-check-failed" : "update-package-unavailable"; return state; }
            if (state.Version != version || state.Artifact == null)
            { _commandError = "requested-version-unavailable"; return state; }
            var result = await _session.UpdateAndRestartAsync(state, consent.Token);
            if (result.Phase == UpdateJobPhase.Cancelled) _commandError = "update-cancelled";
            return result;
        }
        catch (Exception error)
        {
            UpdateLog.Write(_session.Diagnostics, new(UpdateLogKind.OperationFailed, _session.Snapshot.Phase, "apply", version, Error: error));
            _commandError = error is OperationCanceledException ? "update-cancelled" : "update-command-failed";
            return _session.Snapshot;
        }
        finally
        {
            lock (_approvalSync) _approvalCancellation = null;
            SaveResult();
        }
    }

    private bool RevokeApproval()
    {
        lock (_approvalSync)
        {
            if (_approvalCancellation == null) return false;
            _approvalCancellation.Cancel();
            return true;
        }
    }

    private UpdaterControlReply Reply(bool accepted, string? error = null)
    {
        var state = _session.Snapshot;
        return new(1, accepted, state.Phase.ToString(), state.Version, state.Bytes, state.DownloadLength,
            accepted ? _commandError : error, state.Reason);
    }
    private async Task ServeAsync()
    {
        await using var pipe = new NamedPipeServerStream(_endpoint.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        byte[] drainBuffer = new byte[1];
        while (!_stop.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                connected = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                var request = await UpdaterControlWire.ReadAsync<UpdaterControlRequest>(pipe, deadline.Token).ConfigureAwait(false);
                var reply = await _dispatch(() => deadline.IsCancellationRequested ? Reply(false, "update-request-expired") : Handle(request)).ConfigureAwait(false);
                if (request.Operation == "show" && reply.Accepted && _presentation is { } presentation)
                {
                    if (!await presentation.WaitAsync(deadline.Token).ConfigureAwait(false))
                        reply = reply with { Accepted = false, Error = "update-window-closed" };
                }
                await UpdaterControlWire.WriteAsync(pipe, reply, deadline.Token).ConfigureAwait(false);
                // Let the client finish reading the reply and close its end before Disconnect() discards the kernel buffer.
                using var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                drainTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try { _ = await pipe.ReadAsync(drainBuffer, drainTimeout.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
            { UpdateLog.Write(_session.Diagnostics, new(UpdateLogKind.ControlRejected, Error: error)); }
            finally
            {
                if (connected)
                {
                    try { pipe.Disconnect(); }
                    catch (Exception error) when (error is IOException or InvalidOperationException) { }
                }
            }
        }
    }
    private void SaveResult()
    {
        try { OwnedWorkspace.WriteJson(_workspace.Resolve("control-result.json"), Reply(true)); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { UpdateLog.Write(_session.Diagnostics, new(UpdateLogKind.CleanupFailed, Error: error)); }
    }

    public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _stopping = true;
        _stop.Cancel();
        try
        {
            if (_approved is { IsCompleted: false })
            {
                if (_session.Snapshot.Phase is UpdateJobPhase.Staging or UpdateJobPhase.Verifying) _session.Pause();
                else { RevokeApproval(); if (_session.Snapshot.Phase == UpdateJobPhase.Checking) _session.Cancel(); }
                await _approved.ConfigureAwait(false);
            }
            await _server.ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
            SaveResult();
            try
            {
                string path = _workspace.Resolve("control.json");
                if (File.Exists(path) && OwnedWorkspace.ReadJson<UpdaterEndpoint>(path) == _endpoint) File.Delete(path);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { UpdateLog.Write(_session.Diagnostics, new(UpdateLogKind.CleanupFailed, Error: error)); }
        }
    }
}

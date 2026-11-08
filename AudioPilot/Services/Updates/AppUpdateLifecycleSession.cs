using System.IO;
using System.IO.Pipes;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Services.Updates;

internal interface IUpdateStartupGate
{
    bool Restart { get; }
    bool Visible { get; }
    Task WaitForCommitAsync(CancellationToken token);
}

/// <summary>Opt-in outbound control channel. Normal launches neither open a pipe nor change lifecycle behavior.</summary>
internal sealed partial class AppUpdateLifecycleSession : IUpdateStartupGate, IDisposable
{
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(30);
    private readonly UpdateLaunch _launch;
    private NamedPipeClientStream? _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private int _stopped;
    private volatile bool _committed;
    internal static AppUpdateLifecycleSession? Current { get; private set; }
    public bool Restart => _launch.Restart;
    public bool Visible => _launch.Visible;
    internal bool StartupBlocked => Restart && !_committed;

    private AppUpdateLifecycleSession(UpdateLaunch launch) => _launch = launch;

    internal static void InitializeFromEnvironment()
    {
        string? descriptor = Environment.GetEnvironmentVariable(UpdateLifecycleWire.EnvironmentVariable);
        if (descriptor == null) return;
        Current = new(UpdateLifecycleWire.ParseLaunch(descriptor, DiagnosticSession.Id));
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (_pipe != null) return;
        var pipe = new NamedPipeClientStream(".", _launch.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(token).ConfigureAwait(false);
            UpdateLifecycleWire.VerifyServer(pipe, _launch);
            _pipe = pipe;
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal async Task AuthenticateAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        await ConnectAsync(deadline.Token);
    }

    public async Task WaitForCommitAsync(CancellationToken token)
    {
        if (!Restart) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        await ConnectAsync(deadline.Token);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle, deadline.Token);
        if (_launch.Visible && !System.Windows.Application.Current.Windows.OfType<MainWindow>().Any(static window => window.IsVisible))
            throw new InvalidOperationException("The requested main window was not presented.");
        await SendAsync("staged-ready", deadline.Token);
        var decision = await UpdateLifecycleWire.ReadAsync(_pipe!, _launch, _launch.HelperPid, deadline.Token);
        if (decision.Kind != "commit") throw new OperationCanceledException("Update candidate was not committed.");
        _committed = true;
    }

    internal async Task RunAsync(AppRuntimeHost host, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                await ConnectAsync(deadline.Token);
                await SendAsync("ready", deadline.Token, visible: host.WindowManager.IsVisible);
            }
            while (true)
            {
                using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if (host.IsUpdateAdmissionBlocked) commandDeadline.CancelAfter(PreparationTimeout);
                var request = await UpdateLifecycleWire.ReadAsync(_pipe!, _launch, _launch.HelperPid, commandDeadline.Token);
                if (request.Kind == "prepare")
                {
                    commandDeadline.CancelAfter(PreparationTimeout);
                    string? rejection = await host.PrepareForUpdateAsync(commandDeadline.Token);
                    await SendAsync(rejection == null ? "prepared" : "deferred", lifetime.Token, rejection, host.WindowManager.IsVisible);
                }
                else if (request.Kind == "cancel")
                {
                    host.CancelUpdatePreparation();
                    await SendAsync("cancelled", lifetime.Token);
                }
                else if (request.Kind == "shutdown")
                {
                    bool stopped = await host.ShutdownForUpdateAsync(commandDeadline.Token);
                    if (stopped)
                    {
                        try { await SendAsync("stopped", lifetime.Token); }
                        finally { System.Windows.Application.Current.Shutdown(); }
                    }
                    else await SendAsync("not-stopped", lifetime.Token);
                    return;
                }
                else if (request.Kind == "exercise-autosave" && !host.IsUpdateAdmissionBlocked)
                {
                    host.AppViewModel.Theme = host.AppViewModel.Theme == Models.AppTheme.Dark ? Models.AppTheme.Light : Models.AppTheme.Dark;
                    await SendAsync("edited", lifetime.Token);
                }
                else if (request.Kind == "inspect")
                {
                    await UpdateLifecycleWire.WriteAsync(_pipe!, UpdateLifecycleWire.Message(_launch, "inspected") with
                    { Version = typeof(App).Assembly.GetName().Version?.ToString(), Runtime = host.GetUpdateRuntimeInfo() }, lifetime.Token);
                }
                else if (request.Kind == "detach" && !host.IsUpdateAdmissionBlocked)
                {
                    await SendAsync("detached", lifetime.Token);
                    return;
                }
                else throw new InvalidDataException("Unsupported lifecycle command.");
            }
        }
        finally
        {
            host.CancelUpdatePreparation();
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    private Task SendAsync(string kind, CancellationToken token, string? reason = null, bool visible = false) =>
        UpdateLifecycleWire.WriteAsync(_pipe!, UpdateLifecycleWire.Message(_launch, kind, reason, visible)
            with
        { Version = typeof(App).Assembly.GetName().Version?.ToString() }, token);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _lifetime.Cancel();
        _pipe?.Dispose();
        _lifetime.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

}

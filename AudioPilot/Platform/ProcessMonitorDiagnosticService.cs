using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;

namespace AudioPilot.Platform;

internal sealed record ProcessMonitorDiagnosticResult(
    bool Success,
    string Outcome,
    bool SubscriptionStarted,
    bool StartEventReceived,
    bool StopEventReceived,
    bool IsAdministrator,
    string? FailureReason,
    double ElapsedMs)
{
    public string DiagCode => $"diagnostics-process-monitor-{Outcome}";
}

internal readonly record struct ProcessMonitorProbeProcess(int Id, IDisposable Resource);

/// <summary>Verifies temporary WMI subscriptions and event delivery without changing runtime monitoring.</summary>
internal static class ProcessMonitorDiagnosticService
{
    internal static async Task<ProcessMonitorDiagnosticResult> RunAsync(
        Func<IRawProcessLifecycleEventSource>? createSource = null,
        Func<ProcessMonitorProbeProcess>? launchProcess = null,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8), timeProvider ?? TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        CancellationToken token = cancellation.Token;
        int subscriptionStarted = 0;
        int startReceived = 0;
        int stopReceived = 0;
        Task<ProcessMonitorDiagnosticResult> work = Task.Run(async () =>
        {
            bool subscribed = false;
            int probeId = 0;
            var sync = new Lock();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interrupted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using IRawProcessLifecycleEventSource source = (createSource ?? (() => new WmiProcessLifecycleEventSource()))();
            void OnStarted(int id) { lock (sync) { if (probeId > 0 && id == probeId) { Volatile.Write(ref startReceived, 1); started.TrySetResult(); } } }
            void OnStopped(int id) { lock (sync) { if (probeId > 0 && id == probeId) { Volatile.Write(ref stopReceived, 1); stopped.TrySetResult(); } } }
            void OnFailure(string reason) => interrupted.TrySetResult(reason);
            source.ProcessStarted += OnStarted;
            source.ProcessStopped += OnStopped;
            if (source is IProcessLifecycleFailureSource failureSource) failureSource.MonitoringFailed += OnFailure;
            try
            {
                token.ThrowIfCancellationRequested();
                source.Start();
                subscribed = true;
                Volatile.Write(ref subscriptionStarted, 1);
                token.ThrowIfCancellationRequested();
                if (interrupted.Task.IsCompleted) return Result("subscription-interrupted", interrupted.Task.Result);
                ProcessMonitorProbeProcess probe;
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    probe = (launchProcess ?? LaunchProcess)();
                    probeId = probe.Id;
                }
                using (probe.Resource)
                {
                    Task delivered = Task.WhenAll(started.Task, stopped.Task);
                    Task completed = await Task.WhenAny(delivered, interrupted.Task).WaitAsync(token).ConfigureAwait(false);
                    if (completed == interrupted.Task)
                        return Result("subscription-interrupted", interrupted.Task.Result);
                    await delivered.ConfigureAwait(false);
                    return Result("event-delivery-confirmed", null);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Result(subscribed ? "event-delivery-timeout" : "subscription-timeout", null);
            }
            catch (Exception ex)
            {
                return Result(ex is ManagementException { ErrorCode: ManagementStatus.AccessDenied } or UnauthorizedAccessException
                    ? "access-denied" : subscribed ? "probe-process-failed" : "subscription-failed",
                    ex is ManagementException management ? $"wmi-{management.ErrorCode}" : ex.GetType().Name);
            }
            finally
            {
                source.ProcessStarted -= OnStarted;
                source.ProcessStopped -= OnStopped;
                if (source is IProcessLifecycleFailureSource failures) failures.MonitoringFailed -= OnFailure;
            }

            ProcessMonitorDiagnosticResult Result(string outcome, string? reason) => new(
                outcome == "event-delivery-confirmed", outcome, subscribed,
                started.Task.IsCompletedSuccessfully, stopped.Task.IsCompletedSuccessfully,
                isAdministrator, reason, elapsed.Elapsed.TotalMilliseconds);
        }, token);
        try
        {
            return await work.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _ = ObserveCompletionAsync(work);
            bool subscription = Volatile.Read(ref subscriptionStarted) != 0;
            bool start = Volatile.Read(ref startReceived) != 0;
            bool stop = Volatile.Read(ref stopReceived) != 0;
            return new(false, subscription ? start && stop ? "cleanup-timeout" : "event-delivery-timeout" : "subscription-timeout",
                subscription, start, stop, isAdministrator, null, elapsed.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _ = ObserveCompletionAsync(work);
            throw;
        }
        catch (Exception ex)
        {
            bool subscription = Volatile.Read(ref subscriptionStarted) != 0;
            return new(false, subscription ? "cleanup-failed" : "subscription-failed", subscription,
                Volatile.Read(ref startReceived) != 0, Volatile.Read(ref stopReceived) != 0, isAdministrator,
                ex.GetType().Name, elapsed.Elapsed.TotalMilliseconds);
        }
    }

    private static ProcessMonitorProbeProcess LaunchProcess()
    {
        Process process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("The diagnostic process did not start.");
        return new(process.Id, process);
    }

    private static async Task ObserveCompletionAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { }
    }
}

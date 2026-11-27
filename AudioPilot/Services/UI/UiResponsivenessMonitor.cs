using AudioPilot.Logging;
using Microsoft.Win32;

namespace AudioPilot.Services.UI;

/// <summary>One cancellable dispatcher probe, sampled off the UI thread. No window or native audio ownership.</summary>
internal sealed class UiResponsivenessMonitor(
    Func<CancellationToken, Task> postHeartbeat,
    Func<bool> isSuppressed,
    Action<TimeSpan, CancellationToken> offerRecovery,
    Logger logger,
    TimeProvider? timeProvider = null,
    Func<Action<PowerModes>, IDisposable>? subscribePowerChanges = null,
    Func<Action, CancellationToken, Task>? queueRecovery = null) : IAsyncDisposable
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan LaunchRetryDelay = TimeSpan.FromSeconds(10);
    internal const int MaximumLaunchAttempts = 3;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Func<Action, CancellationToken, Task> _queueRecovery = queueRecovery ?? Task.Run;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lifetime = new();
    private Task? _worker;
    private TaskCompletionSource? _stopped;
    private long _powerRevision;
    private int _suspended;

    internal void Start()
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_stopped != null, this);
            _worker ??= Task.Run(RunAsync);
        }
    }

    internal void NotifyPowerModeChanged(PowerModes mode)
    {
        if (mode is not (PowerModes.Suspend or PowerModes.Resume)) return;
        Volatile.Write(ref _suspended, mode == PowerModes.Suspend ? 1 : 0);
        Interlocked.Increment(ref _powerRevision);
    }

    private async Task RunAsync()
    {
        IDisposable? powerSubscription = null;
        CancellationTokenSource? probeLifetime = null;
        Task? probe = null;
        CancellationTokenSource? launchLifetime = null;
        Task? launch = null;
        long generation = 0, launchGeneration = 0;
        bool reported = false, offered = false;
        int attempts = 0;
        long probeStarted = 0, failedAt = 0;
        long previousSample = _clock.GetTimestamp();
        long powerRevision = Volatile.Read(ref _powerRevision);
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            try { powerSubscription = subscribePowerChanges?.Invoke(NotifyPowerModeChanged); }
            catch (Exception error) { logger.Warning("UiResponsiveness", "ui-power-notifications-unavailable | fallback=window-and-sampling", nameof(RunAsync), error); }
            while (true)
            {
                await Task.Delay(SampleInterval, _clock, _stop.Token).ConfigureAwait(false);
                long now = _clock.GetTimestamp();
                long currentPowerRevision = Volatile.Read(ref _powerRevision);
                bool interrupted = _clock.GetElapsedTime(previousSample, now) > SampleInterval * 5
                    || currentPowerRevision != powerRevision;
                previousSample = now;
                powerRevision = currentPowerRevision;
                if (isSuppressed() || Volatile.Read(ref _suspended) != 0 || interrupted)
                {
                    await ResetProbeAsync().ConfigureAwait(false);
                    continue;
                }
                if (probe?.IsCompleted == true)
                {
                    await probe.ConfigureAwait(false);
                    if (reported)
                        logger.Info("UiResponsiveness", () => $"ui-stall-recovered | elapsedMs={_clock.GetElapsedTime(probeStarted, now).TotalMilliseconds:F0}");
                    await ResetProbeAsync().ConfigureAwait(false);
                }
                if (launch?.IsCompleted == true)
                {
                    try
                    {
                        await launch.ConfigureAwait(false);
                        if (launchGeneration == generation) offered = true;
                    }
                    catch (OperationCanceledException)
                    {
                        if (launchGeneration == generation) await ResetProbeAsync().ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        if (!launchLifetime!.IsCancellationRequested && launchGeneration == generation)
                        {
                            failedAt = now;
                            logger.Warning("UiResponsiveness", () => $"ui-stall-recovery-launch-failed | attempt={attempts}", nameof(RunAsync), error);
                        }
                    }
                    launchLifetime!.Dispose();
                    launchLifetime = null;
                    launch = null;
                }
                if (probe == null)
                {
                    probeLifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    probeStarted = now;
                    probe = postHeartbeat(probeLifetime.Token);
                }
                else if (_clock.GetElapsedTime(probeStarted, now) >= StallThreshold)
                {
                    TimeSpan duration = _clock.GetElapsedTime(probeStarted, now);
                    if (!reported)
                    {
                        reported = true;
                        logger.Warning("UiResponsiveness", () => $"ui-stall-detected | elapsedMs={duration.TotalMilliseconds:F0} recovery=independent-process");
                    }
                    if (launch == null && !offered && attempts < MaximumLaunchAttempts
                        && (attempts == 0 || _clock.GetElapsedTime(failedAt, now) >= LaunchRetryDelay))
                    {
                        attempts++;
                        launchGeneration = generation;
                        launchLifetime = CancellationTokenSource.CreateLinkedTokenSource(probeLifetime!.Token);
                        CancellationToken token = launchLifetime.Token;
                        long expectedPowerRevision = powerRevision;
                        // Process.Start can block in Windows/antivirus. The sampling worker never joins that call.
                        launch = _queueRecovery(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            if (Volatile.Read(ref _suspended) != 0 || Volatile.Read(ref _powerRevision) != expectedPowerRevision || isSuppressed())
                                throw new OperationCanceledException(token);
                            offerRecovery(duration, token);
                        }, token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { logger.Warning("UiResponsiveness", "ui-stall-monitor-failed", nameof(RunAsync), error); }
        finally
        {
            await ResetProbeAsync().ConfigureAwait(false);
            if (launch != null)
            {
                // Observe late failure and keep its token source alive until the uncancellable native call returns.
                _ = launch.ContinueWith(static (task, state) =>
                {
                    _ = task.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                }, launchLifetime!, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            try { powerSubscription?.Dispose(); }
            catch (Exception error) { logger.Warning("UiResponsiveness", "ui-power-notifications-stop-failed", nameof(RunAsync), error); }
        }

        async Task ResetProbeAsync()
        {
            generation++;
            reported = offered = false;
            attempts = 0;
            failedAt = 0;
            launchLifetime?.Cancel();
            if (probeLifetime == null) return;
            probeLifetime.Cancel();
            // Abort queued work without waiting on a blocked dispatcher.
            try { if (probe != null) await probe.WaitAsync(probeLifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (probeLifetime.IsCancellationRequested) { }
            catch (Exception error) { logger.Debug("UiResponsiveness", () => $"ui-heartbeat-ended | error={error.GetType().Name}"); }
            if (probe is { IsCompletedSuccessfully: false }) _ = probe.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            probeLifetime.Dispose();
            probeLifetime = null;
            probe = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null;
        Task stopped;
        lock (_lifetime)
        {
            if (_stopped == null) completion = _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopped = _stopped.Task;
        }
        if (completion != null) _ = StopAsync(completion);
        return new(stopped);
    }

    private async Task StopAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { _stop.Cancel(); }
            catch (Exception error) { failure = error; }
            if (_worker != null) await _worker.ConfigureAwait(false);
        }
        catch (Exception error) { failure ??= error; }
        finally { _stop.Dispose(); }
        if (failure == null) completion.TrySetResult();
        else completion.TrySetException(failure);
    }
}

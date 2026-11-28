using System.Windows;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Platform;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Updater.Ui;

internal static class UpdaterAutomation
{
    internal static async Task<int> RunAsync(string[] args, AppTheme? theme = null)
    {
        string? signalName = null;
        string? readySignalName = null;
        while (args.Length >= 2)
        {
            if (args[^2] == "--startup-event" && signalName == null)
            {
                string signal = args[^1];
                if (!signal.StartsWith(UpdaterControlWire.StartupSignalPrefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(signal[UpdaterControlWire.StartupSignalPrefix.Length..], "N", out _)) return 2;
                signalName = signal;
                args = args[..^2];
            }
            else if (args[^2] == "--ready-event" && readySignalName == null)
            {
                string signal = args[^1];
                if (!signal.StartsWith(UpdaterControlWire.ReadySignalPrefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(signal[UpdaterControlWire.ReadySignalPrefix.Length..], "N", out _)) return 2;
                readySignalName = signal;
                args = args[..^2];
            }
            else break;
        }
        if (args is not ["--automation", var app, var workspacePath, var identity, var pid, var started]) return 2;
        UpdaterSession? session = null;
        UpdaterControlServer? control = null;
        UpdaterWindow? window = null;
        UpdaterApplication? updaterApp = Application.Current as UpdaterApplication;
        EventWaitHandle? startupSignal = null;
        EventWaitHandle? readySignal = null;
        RegisteredWaitHandle? startupWait = null;
        bool presented = false;
        bool shuttingDown = false;
        bool StartupCancelled() => !presented && (shuttingDown || startupSignal?.WaitOne(0) == true);
        try
        {
            if (signalName != null) startupSignal = EventWaitHandle.OpenExisting(signalName);
            if (StartupCancelled()) return 0;
            if (readySignalName != null)
            {
                try { readySignal = EventWaitHandle.OpenExisting(readySignalName); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            UpdateClientPolicy.RequireConfigured();
            if (!UpdateClientPolicy.IsHelperPath(Environment.ProcessPath!)) throw new InvalidOperationException("Use AudioPilot's staged updater.");
            Task<UpdaterSession> sessionTask = Task.Run(() => UpdaterApplication.OpenPilotSession(app, workspacePath, identity, pid, started));
            var application = Application.Current;
            if (theme.HasValue)
            {
                WindowThemeResolver.SetApplicationThemeProvider(() => theme.Value);
                WindowThemeHelper.ApplyApplicationThemeResources(theme.Value);
            }
            session = await sessionTask;
            updaterApp?.RegisterActiveSession(session);
            if (StartupCancelled()) return 0;
            AppTheme windowTheme = theme ?? session.Theme;
            if (startupSignal != null)
                startupWait = ThreadPool.RegisterWaitForSingleObject(startupSignal, (_, _) =>
                    application.Dispatcher.BeginInvoke(new Action(() => { if (!presented) window?.Close(); })),
                    null, Timeout.InfiniteTimeSpan, executeOnlyOnce: true);
            Task<bool>? presentation = null;
            Task<bool> Show(UpdaterWindowPlacement? placement)
            {
                if (presentation != null)
                {
                    window?.ShowOrActivate();
                    return presentation.IsCompletedSuccessfully ? Task.FromResult(window?.IsVisible == true) : presentation;
                }
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                presentation = ready.Task;
                application.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (StartupCancelled()) { ready.TrySetResult(false); return; }
                        window = new UpdaterWindow(session, windowTheme);
                        application.MainWindow = window;
                        DialogWindowHelper.ShowOwnedDialog(window, onPresented: () =>
                        {
                            presented = true;
                            ready.TrySetResult(true);
                        }, positionBeforeReveal: () =>
                        {
                            if (StartupCancelled()) throw new OperationCanceledException("Updater opening cancelled.");
                            bool positioned = placement != null && DialogWindowHelper.PositionBeforeReveal(window,
                                new(placement.Left, placement.Top, placement.Left + placement.Width, placement.Top + placement.Height));
                            if (!positioned) positioned = DialogWindowHelper.TryCenterBeforeReveal(window, null);
                            if (placement?.Maximized == true) window.WindowState = WindowState.Maximized;
                            return positioned;
                        });
                        ready.TrySetResult(false);
                    }
                    catch (OperationCanceledException) when (StartupCancelled()) { ready.TrySetResult(false); }
                    catch (ObjectDisposedException) when (shuttingDown) { ready.TrySetResult(false); }
                    catch (Exception error) { ready.TrySetException(error); }
                }));
                return ready.Task;
            }
            control = new UpdaterControlServer(session, new OwnedWorkspace(workspacePath),
                action => application.Dispatcher.InvokeAsync(() =>
                {
                    var reply = action();
                    window?.Refresh(force: true);
                    return reply;
                }).Task, Show, abandonStartup: () => window?.Close(), setTheme: selected =>
                {
                    windowTheme = selected;
                    window?.ApplyTheme(selected);
                });
            updaterApp?.RegisterActiveControl(control);
            readySignal?.Set();
            var idle = System.Diagnostics.Stopwatch.StartNew();
            while (idle.Elapsed < TimeSpan.FromMinutes(5))
            {
                if (window != null) { await window.WindowClosed; break; }
                if (control.StartupAbandoned || StartupCancelled()) break;
                if (session.IsBusy || control.ActiveApproval is { IsCompleted: false }) idle.Restart();
                if (control.ActiveApproval is { IsCompleted: true } && session.Snapshot.Phase is UpdateJobPhase.Completed or UpdateJobPhase.RolledBack or UpdateJobPhase.Cancelled) break;
                await Task.Delay(1000);
            }
            shuttingDown = true;
            if (window != null) await window.WindowClosed;
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.StartupFailed, Error: error))); return 1; }
        finally
        {
            shuttingDown = true;
            UpdaterControlServer? activeControl = updaterApp != null ? updaterApp.TakeActiveControl() : control;
            UpdaterSession? activeSession = updaterApp != null ? updaterApp.TakeActiveSession() : session;
            try
            {
                if (activeControl != null)
                {
                    try { await activeControl.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.CleanupFailed, Error: error))); }
                }
                if (activeSession != null) await activeSession.DisposeAsync().ConfigureAwait(false);
            }
            finally { startupWait?.Unregister(null); startupSignal?.Dispose(); readySignal?.Dispose(); }
        }
    }
}

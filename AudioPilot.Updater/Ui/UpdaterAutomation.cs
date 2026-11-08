using System.Windows;
using AudioPilot.Helpers;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Updater.Ui;

internal static class UpdaterAutomation
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["--automation", var app, var workspacePath, var identity, var pid, var started]) return 2;
        UpdaterSession? session = null;
        UpdaterWindow? window = null;
        try
        {
            UpdateClientPolicy.RequireConfigured();
            if (!UpdateClientPolicy.IsHelperPath(Environment.ProcessPath!)) throw new InvalidOperationException("Use AudioPilot's staged updater.");
            session = UpdaterApplication.OpenPilotSession(app, workspacePath, identity, pid, started);
            var application = Application.Current;
            void Show()
            {
                application.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (window != null) { window.ShowOrActivate(); return; }
                    window = new UpdaterWindow(session, session.Theme);
                    application.MainWindow = window;
                    DialogWindowHelper.ShowOwnedDialog(window);
                }));
            }
            await using var control = new UpdaterControlServer(session, new OwnedWorkspace(workspacePath),
                action => application.Dispatcher.InvokeAsync(() =>
                {
                    var reply = action();
                    window?.Refresh(force: true);
                    return reply;
                }).Task, Show);
            var idle = System.Diagnostics.Stopwatch.StartNew();
            while (idle.Elapsed < TimeSpan.FromMinutes(5))
            {
                if (session.IsBusy || control.ActiveApproval is { IsCompleted: false }) idle.Restart();
                if (window != null) { await window.WindowClosed; break; }
                if (control.ActiveApproval is { IsCompleted: true } && session.Snapshot.Phase is UpdateJobPhase.Completed or UpdateJobPhase.RolledBack or UpdateJobPhase.Cancelled) break;
                await Task.Delay(1000);
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(UpdaterLog.Format(new(UpdateLogKind.StartupFailed, Error: error))); return 1; }
        finally
        {
            if (session != null) await session.DisposeAsync().ConfigureAwait(false);

        }
    }
}

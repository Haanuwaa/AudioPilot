using AudioPilot.Logging;

namespace AudioPilot.Services.Internal;

/// <summary>Quiesces a failed runtime before presenting one fatal dialog and always requests final cleanup.</summary>
internal sealed class FatalErrorCoordinator(
    Func<Task> quiesce,
    Func<Exception, Task<FatalLogWriteResult>> report,
    Func<string, Task> showError,
    Func<Task> shutdown,
    Action<string, Exception> reportSecondaryFailure)
{
    private Task? _handling;

    internal Task HandleAsync(Exception exception)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? existing = Interlocked.CompareExchange(ref _handling, completion.Task, null);
        if (existing != null) return existing;
        _ = CompleteAsync(exception, completion);
        return completion.Task;
    }

    private async Task CompleteAsync(Exception exception, TaskCompletionSource completion)
    {
        try
        {
            try { await quiesce(); }
            catch (Exception ex) { ReportSecondary("quiesce", ex); }
            FatalLogWriteResult result;
            try { result = await report(exception); }
            catch (Exception ex) { ReportSecondary("report", ex); result = FatalLogWriteResult.Unavailable; }
            try { await showError(BuildMessage(result)); }
            catch (Exception ex) { ReportSecondary("dialog", ex); }
        }
        finally
        {
            try { await shutdown(); completion.TrySetResult(); }
            catch (Exception ex) { ReportSecondary("shutdown", ex); completion.TrySetException(ex); }
        }
    }

    private void ReportSecondary(string stage, Exception exception)
    {
        try { reportSecondaryFailure(stage, exception); }
        catch (Exception) { }
    }

    internal static string BuildMessage(FatalLogWriteResult result) => "A fatal error occurred and AudioPilot must close. " + (result switch
    {
        FatalLogWriteResult.Written => "Details were written to AudioPilot.log.",
        FatalLogWriteResult.Disabled => "File logging is disabled.",
        _ => "The error details could not be confirmed in the log.",
    });
}

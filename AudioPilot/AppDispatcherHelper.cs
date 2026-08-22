using System.Windows.Threading;
using AudioPilot.Logging;

namespace AudioPilot
{
    internal static class AppDispatcherHelper
    {
        /// <summary>
        /// Returns <see langword="true"/> when UI work can no longer be safely dispatched because the dispatcher
        /// is missing or already shutting down.
        /// </summary>
        public static bool IsDispatcherUnavailable(Dispatcher? dispatcher)
        {
            return dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished;
        }

        /// <summary>
        /// Queues synchronous UI work onto the dispatcher and converts action failures into logged errors.
        /// </summary>
        /// <remarks>
        /// Dispatcher shutdown races are treated as expected during teardown; they produce warnings instead of
        /// surfacing exceptions back to global hotkey handlers.
        /// </remarks>
        public static void Dispatch(
            Dispatcher dispatcher,
            Logger logger,
            Action action,
            string errorMessage,
            string methodName)
        {
            if (IsDispatcherUnavailable(dispatcher))
            {
                return;
            }

            try
            {
                _ = dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        logger.Error("AppDispatcherHelper", errorMessage, methodName, ex);
                    }
                });
            }
            catch (InvalidOperationException ex) when (IsDispatcherUnavailable(dispatcher))
            {
                logger.Warning("AppDispatcherHelper", "Skipping dispatcher action because shutdown is in progress", methodName, ex);
            }
        }

        /// <summary>
        /// Fire-and-forget wrapper for <see cref="InvokeAsync"/> used by hotkey entry points that do not need to
        /// await dispatcher completion.
        /// </summary>
        public static void DispatchAsync(
            Dispatcher dispatcher,
            Logger logger,
            Func<Task> action,
            string errorMessage,
            string methodName,
            DispatcherPriority priority = DispatcherPriority.Normal)
        {
            _ = InvokeAsync(dispatcher, logger, action, errorMessage, methodName, priority);
        }

        /// <summary>
        /// Runs asynchronous UI work on the dispatcher and returns a task that completes when the dispatched work
        /// finishes or is skipped because shutdown has already started.
        /// </summary>
        public static Task InvokeAsync(
            Dispatcher dispatcher,
            Logger logger,
            Func<Task> action,
            string errorMessage,
            string methodName,
            DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (IsDispatcherUnavailable(dispatcher))
            {
                return Task.CompletedTask;
            }

            try
            {
                return dispatcher.InvokeAsync(
                    async () =>
                    {
                        if (IsDispatcherUnavailable(dispatcher))
                        {
                            return;
                        }

                        try
                        {
                            await action();
                        }
                        catch (Exception ex)
                        {
                            logger.Error("AppDispatcherHelper", errorMessage, methodName, ex);
                        }
                    },
                    priority).Task.Unwrap();
            }
            catch (InvalidOperationException ex) when (IsDispatcherUnavailable(dispatcher))
            {
                logger.Warning("AppDispatcherHelper", "Skipping dispatcher async action because shutdown is in progress", methodName, ex);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Executes an asynchronous operation, logs failures, and attempts to surface a user-facing error dialog
        /// only while the dispatcher is still available.
        /// </summary>
        public static async Task ExecuteAsync(
            Func<Task> action,
            Logger logger,
            Dispatcher dispatcher,
            IAppDialogService dialogs,
            string logMessage,
            string userMessage,
            string methodName)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                logger.Error("AppDispatcherHelper", logMessage, methodName, ex);

                if (IsDispatcherUnavailable(dispatcher))
                {
                    return;
                }

                try
                {
                    await dialogs.ShowErrorAsync(userMessage);
                }
                catch (InvalidOperationException invokeEx) when (IsDispatcherUnavailable(dispatcher))
                {
                    logger.Warning("AppDispatcherHelper", "Skipping error dialog because dispatcher shutdown is in progress", methodName, invokeEx);
                }
            }
        }
    }
}

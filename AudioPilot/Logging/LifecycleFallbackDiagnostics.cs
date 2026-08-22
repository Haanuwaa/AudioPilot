using System.Diagnostics;
using System.IO;

namespace AudioPilot.Logging
{
    internal static class LifecycleFallbackDiagnostics
    {
        internal static string Format(
            string category,
            string message,
            string operation,
            Exception exception,
            Exception? loggingException = null)
        {
            string diagnostic =
                $"{category} lifecycle fallback | operation={operation} message={message} exceptionType={exception.GetType().Name} exceptionMessage={Logger.SanitizeExceptionMessage(exception.Message)}";

            if (loggingException != null)
            {
                diagnostic +=
                    $" loggingExceptionType={loggingException.GetType().Name} loggingExceptionMessage={Logger.SanitizeExceptionMessage(loggingException.Message)}";
            }

            return diagnostic;
        }

        internal static void Write(
            string category,
            string message,
            string operation,
            Exception exception,
            Exception? loggingException = null,
            TextWriter? errorWriter = null)
        {
            string diagnostic = Format(category, message, operation, exception, loggingException);

            try
            {
                Trace.TraceWarning(diagnostic);
            }
            catch
            {
            }

            try
            {
                (errorWriter ?? Console.Error).WriteLine(diagnostic);
            }
            catch
            {
            }
        }
    }
}

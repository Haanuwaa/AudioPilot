using AudioPilot.Cli;

namespace AudioPilot.CliHost
{
    internal static class CliHostProcessBoundary
    {
        internal const int UnexpectedFailureExitCode = 70;
        private const string UnexpectedFailureCode = "internal-error";
        private const string UnexpectedFailureMessage = "AudioPilot CLI failed unexpectedly.";

        internal static int Execute(
            string[] args,
            TextWriter standardError,
            Func<int> execute,
            Action<string>? logFailureMetadata = null)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(standardError);
            ArgumentNullException.ThrowIfNull(execute);

            try
            {
                return execute();
            }
            catch (Exception ex)
            {
                TryLogFailure(logFailureMetadata, ex);
                TryWriteFailure(args, standardError);
                return UnexpectedFailureExitCode;
            }
        }

        private static void TryLogFailure(Action<string>? logFailureMetadata, Exception exception)
        {
            try
            {
                logFailureMetadata?.Invoke($"unhandled-cli-failure | exceptionType={exception.GetType().Name}");
            }
            catch (Exception)
            {
            }
        }

        private static void TryWriteFailure(string[] args, TextWriter standardError)
        {
            try
            {
                CliHostUtilities.WriteCliError(
                    standardError,
                    UnexpectedFailureExitCode,
                    UnexpectedFailureCode,
                    UnexpectedFailureMessage,
                    CliHostUtilities.PrefersJson(args),
                    includeUsage: false);
            }
            catch (Exception)
            {
                try
                {
                    standardError.WriteLine(CliHostUtilities.FormatTextError(UnexpectedFailureCode, UnexpectedFailureMessage));
                }
                catch (Exception)
                {
                }
            }
        }
    }
}

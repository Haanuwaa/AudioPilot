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
            Action<string>? logFailureMetadata = null,
            TextWriter? standardOutput = null)
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
                TryWriteFailure(args, standardOutput ?? standardError, standardError);
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

        private static void TryWriteFailure(string[] args, TextWriter standardOutput, TextWriter standardError)
        {
            bool prefersJson = CliHostUtilities.PrefersJson(args);
            try
            {
                CliHostUtilities.WriteCliError(
                    prefersJson ? standardOutput : standardError,
                    UnexpectedFailureExitCode,
                    UnexpectedFailureCode,
                    UnexpectedFailureMessage,
                    prefersJson,
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

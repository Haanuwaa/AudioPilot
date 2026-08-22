using System.Text;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Logging;

public sealed class LifecycleFallbackDiagnosticsTests
{
    [Fact]
    public void Format_SanitizesPrimaryAndLoggingExceptionDetails()
    {
        const string primaryPath = @"C:\Users\ExampleUser\secret.txt";
        const string loggingPath = @"\\server\share\hidden.txt";

        string diagnostic = LifecycleFallbackDiagnostics.Format(
            "App",
            "Failed to detach lifetime event handlers during app shutdown",
            "OnExit",
            new InvalidOperationException($"primary-boom {primaryPath}"),
            new IOException($"logger-boom {loggingPath}"));

        Assert.Contains("App lifecycle fallback", diagnostic, StringComparison.Ordinal);
        Assert.Contains("operation=OnExit", diagnostic, StringComparison.Ordinal);
        Assert.Contains("message=Failed to detach lifetime event handlers during app shutdown", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exceptionType=InvalidOperationException", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exceptionMessage=primary-boom <path>", diagnostic, StringComparison.Ordinal);
        Assert.Contains("loggingExceptionType=IOException", diagnostic, StringComparison.Ordinal);
        Assert.Contains("loggingExceptionMessage=logger-boom <path>", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(primaryPath, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(loggingPath, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_WritesSanitizedDiagnosticToProvidedErrorWriter()
    {
        const string primaryPath = @"C:\Users\ExampleUser\dialog.txt";
        const string loggingPath = @"\\server\share\logger.txt";
        var writer = new StringWriter(new StringBuilder());

        LifecycleFallbackDiagnostics.Write(
            "MainWindow",
            "Failed to show fatal error dialog",
            "ShowFatalErrorDialogOnce",
            new InvalidOperationException($"dialog-boom {primaryPath}"),
            new IOException($"logger-boom {loggingPath}"),
            writer);

        string output = writer.ToString();
        Assert.Contains("MainWindow lifecycle fallback", output, StringComparison.Ordinal);
        Assert.Contains("operation=ShowFatalErrorDialogOnce", output, StringComparison.Ordinal);
        Assert.Contains("exceptionMessage=dialog-boom <path>", output, StringComparison.Ordinal);
        Assert.Contains("loggingExceptionMessage=logger-boom <path>", output, StringComparison.Ordinal);
        Assert.DoesNotContain(primaryPath, output, StringComparison.Ordinal);
        Assert.DoesNotContain(loggingPath, output, StringComparison.Ordinal);
    }
}

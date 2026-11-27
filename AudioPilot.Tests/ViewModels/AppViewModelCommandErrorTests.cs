using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelCommandErrorTests
{
    [Theory]
    [InlineData("enabled", "Check the AudioPilot log")]
    [InlineData("disabled", "File logging is off")]
    [InlineData("unwritable", "reported a write failure")]
    public Task CommandErrorsOfferGuidanceMatchingFileLogging(string state, string expected) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelCommandErrorTests));
        using var logger = TestLoggerScope.CreateFileBacked(nameof(AppViewModelCommandErrorTests), "command-error.log");
        if (state == "unwritable") Directory.CreateDirectory(logger.LogPath);
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, logger: logger.Logger, allowBackgroundWork: true);
        harness.ViewModel.Localization.SetLanguage("en");
        if (state == "disabled") logger.Logger.MinimumLevel = LogLevel.None;
        if (state == "unwritable")
        {
            logger.Logger.Info("Test", "Observe the failed write before testing guidance.");
            await TestExecutionGuards.WaitUntilAsync(() => logger.Logger.CaptureHealth().LastWriteSucceeded == false,
                "The controlled log-write failure was not observed.", TimeSpan.FromSeconds(10));
        }
        TestPrivateAccess.InvokeNonPublic(harness.ViewModel, "HandleAsyncCommandException", "fixture-command", new IOException("Fixture failure"));
        await harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync();
        var (message, _) = Assert.Single(harness.Messages.ErrorMessages);
        Assert.Contains(expected, message, StringComparison.Ordinal);
        if (state != "enabled") Assert.DoesNotContain("Check the AudioPilot log", message, StringComparison.Ordinal);
        harness.ViewModel.Localization.SetLanguage("es");
        string spanish = Assert.Single(harness.Messages.Requests).Localize(harness.ViewModel.Localization).Message;
        Assert.Contains("No se pudo completar la operación", spanish, StringComparison.Ordinal);
        Assert.Contains(state == "enabled" ? "Consulta el registro" : state == "disabled" ? "está desactivado" : "error al escribir el registro", spanish, StringComparison.Ordinal);
        harness.ViewModel.BeginRuntimeShutdown();
        TestPrivateAccess.InvokeNonPublic(harness.ViewModel, "HandleAsyncCommandException", "late-command", new IOException("Late fixture failure"));
        await harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync();
        Assert.Single(harness.Messages.ErrorMessages);
    });
}

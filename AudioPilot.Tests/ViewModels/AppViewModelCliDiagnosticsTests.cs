using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using Windows.Media.Control;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class AppViewModelCliDiagnosticsTests
{
    [Fact]
    public async Task LogExportUsesTheInjectedLoggersDirectoryAndRedactsItsContent()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(LogExportUsesTheInjectedLoggersDirectoryAndRedactsItsContent));
            using var logs = TestLoggerScope.CreateFileBacked(nameof(AppViewModelCliDiagnosticsTests), AppConstants.Files.LogFileName);
            File.WriteAllText(logs.LogPath, @"Fixture log: C:\Users\ExampleUser\Private\track.mp3");
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, logger: logs.Logger);
            string destination = Path.Combine(workspace.Root, "logs.zip");
            var (Success, Output) = harness.ViewModel.ExportLogsFromCli(destination, true, CliDiagnosticsExportDetailLevel.Summary, true, redactOutput: true);
            Assert.True(Success, Output);
            using var archive = ZipFile.OpenRead(destination);
            using var reader = new StreamReader(archive.GetEntry(AppConstants.Files.LogFileName)!.Open());
            string content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Fixture log:", content, StringComparison.Ordinal);
            Assert.DoesNotContain("ExampleUser", content, StringComparison.Ordinal);
            await harness.ViewModel.CleanupAsync();
        });
    }

    [Theory]
    [InlineData("playing")]
    [InlineData("idle")]
    [InlineData("failed")]
    [InlineData("faulted")]
    public async Task CaptureDiagnosticBundleMediaStatusAsync_DistinguishesIdleFromFailedCollectionAfterYielding(string state)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var engine = new MediaOverlayEngine(
                currentSnapshotOverride: async (_, _, cancellationToken) =>
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (state == "failed") throw new OperationCanceledException(@"Private fixture C:\Users\ExampleUser\private-file");
                    if (state == "faulted") throw new System.Runtime.InteropServices.COMException(@"Private fixture C:\Users\ExampleUser\private-file");
                    if (state == "idle") return MediaOverlaySessionSnapshot.Empty;
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Dispatcher test title",
                        "Dispatcher test artist",
                        null,
                        "dispatcher-test-source",
                        12);
                },
                snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
                sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelCliDiagnosticsTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher,
                allowBackgroundWork: true, mediaOverlayService: new MediaOverlayCommandService(engine));
            AppViewModel viewModel = harness.ViewModel;

            try
            {
                var (json, issue) = await viewModel.CaptureDiagnosticBundleMediaStatusAsync(redactOutput: true);

                if (state is "failed" or "faulted")
                {
                    Assert.NotNull(issue);
                    Assert.Equal(state == "failed" ? "OperationCanceledException" : "COMException", issue.ErrorType);
                    Assert.Equal("diagnostics/media-status.json", issue.ArchiveEntry);
                    Assert.Contains(issue.DiagCode, json, StringComparison.Ordinal);
                    Assert.DoesNotContain("Private fixture", json, StringComparison.Ordinal);
                    Assert.DoesNotContain("ExampleUser", json, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Null(issue);
                    Assert.Equal(state == "playing", JsonNode.Parse(json)!["data"]!["hasSession"]!.GetValue<bool>());
                }
            }
            finally
            {
                await viewModel.CleanupAsync();
            }
        });
    }
}

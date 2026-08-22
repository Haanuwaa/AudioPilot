using System.Text.Json.Nodes;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;
using Windows.Media.Control;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class AppViewModelCliDiagnosticsTests
{
    [Theory]
    [InlineData("playing")]
    [InlineData("idle")]
    [InlineData("failed")]
    public async Task CaptureDiagnosticBundleMediaStatusAsync_DistinguishesIdleFromFailedCollectionAfterYielding(string state)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var presenter = new RecordingOverlayPresenter();
            using var overlay = new OverlayService(action => action(), _ => presenter);
            using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
            var engine = new MediaOverlayEngine(
                currentSnapshotOverride: async (_, _, cancellationToken) =>
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (state == "failed") throw new OperationCanceledException(@"Private fixture C:\Users\ExampleUser\private-file");
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
            var coordinator = new AppCliOverlayCoordinator(
                audio,
                overlay,
                new MediaOverlayCommandService(engine),
                Logger.Instance,
                static () => null);
            AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell();
            TestPrivateAccess.SetField(viewModel, "_cliOverlayCoordinator", coordinator);

            try
            {
                var (json, issue) = await viewModel.CaptureDiagnosticBundleMediaStatusAsync(redactOutput: true);

                if (state == "failed")
                {
                    Assert.NotNull(issue);
                    Assert.Equal("OperationCanceledException", issue.ErrorType);
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
                await coordinator.ShutdownAsync();
            }
        });
    }
}

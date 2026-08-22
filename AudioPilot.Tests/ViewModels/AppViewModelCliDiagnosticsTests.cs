using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Services.UI.MediaOverlay;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;
using Windows.Media.Control;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class AppViewModelCliDiagnosticsTests
{
    [Fact]
    public async Task GetDiagnosticBundleMediaStatusJsonAsync_CompletesAfterYieldingOnUiDispatcher()
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
                string json = await viewModel.GetDiagnosticBundleMediaStatusJsonAsync(redactOutput: true);

                Assert.Contains("hasSession", json, StringComparison.Ordinal);
                Assert.Contains("true", json, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await coordinator.ShutdownAsync();
            }
        });
    }
}

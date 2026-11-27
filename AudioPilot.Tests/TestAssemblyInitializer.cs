using System.Runtime.CompilerServices;
using AudioPilot.Logging;

namespace AudioPilot.Tests;

internal static class TestAssemblyInitializer
{
    /// <summary>Initializes the shared logger before path tests replace the data-root providers with temporary directories.</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        string testDataRoot = Path.Combine(
            Path.GetTempPath(),
            "AudioPilot.Tests",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        AppDataPaths.UserDataRootProviderOverride = () => Path.Combine(testDataRoot, "appdata");
        AppDataPaths.BaseDirectoryProviderOverride = () => Path.Combine(testDataRoot, "portable");
        AppDataPaths.InstallerRegistrationProviderOverride = () => (null, null);
        _ = Logger.Instance;
        AppDialogService.SetDefaultPresenterForTests(new NonInteractiveAppDialogPresenter());
    }

    private sealed class NonInteractiveAppDialogPresenter : IAppDialogPresenter
    {
        public Task<AppDialogResult> PresentAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken,
            Action<AppDialogKind>? onPresented = null) =>
            Task.FromResult(request.SafeCloseResult);

        public bool TryUpdateAcknowledgement(AppDialogRequest request, int repetitionCount) => true;

        public void CloseActive(AppDialogResult result)
        {
        }
    }
}

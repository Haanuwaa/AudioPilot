using System.Runtime.CompilerServices;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

internal static class TestAssemblyInitializer
{
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
        // Initialize the process-wide logger while the assembly-stable data root is active.
        // Individual path tests temporarily replace these delegates and remove their roots.
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

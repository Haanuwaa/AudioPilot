using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

public sealed class AppDataPathsTests
{
    [Fact]
    public void GetPrimaryDataRoot_UsesRegisteredDataRoot_WhenCurrentBaseMatchesInstallFolder()
    {
        using var scope = new TestScopedDirectory(nameof(GetPrimaryDataRoot_UsesRegisteredDataRoot_WhenCurrentBaseMatchesInstallFolder));
        string installRoot = Path.Combine(scope.Root, "install");
        string dataRoot = Path.Combine(scope.Root, "data");

        WithPathOverrides(
            userDataRoot: Path.Combine(scope.Root, "fallback"),
            baseDirectory: installRoot + Path.DirectorySeparatorChar,
            installerRegistration: () => (installRoot, dataRoot),
            action: () => Assert.Equal(dataRoot, AppDataPaths.GetPrimaryDataRoot()));
    }

    [Fact]
    public void GetPrimaryDataRoot_UsesPortableBase_WhenInstallerRegistrationDoesNotMatch()
    {
        using var scope = new TestScopedDirectory(nameof(GetPrimaryDataRoot_UsesPortableBase_WhenInstallerRegistrationDoesNotMatch));
        string portableRoot = Path.Combine(scope.Root, "portable");
        string installedRoot = Path.Combine(scope.Root, "installed");

        WithPathOverrides(
            userDataRoot: Path.Combine(scope.Root, "fallback"),
            baseDirectory: portableRoot,
            installerRegistration: () => (installedRoot, Path.Combine(scope.Root, "data")),
            action: () => Assert.Equal(portableRoot, AppDataPaths.GetPrimaryDataRoot()));
    }

    [Fact]
    public void GetWritableDataRoot_FallsBackToUserDataRoot_WhenPrimaryCannotBeCreated()
    {
        using var scope = new TestScopedDirectory(nameof(GetWritableDataRoot_FallsBackToUserDataRoot_WhenPrimaryCannotBeCreated));
        string blockedPrimary = Path.Combine(scope.Root, "blocked-primary");
        string fallbackRoot = Path.Combine(scope.Root, "fallback");
        File.WriteAllText(blockedPrimary, "not-a-directory");

        WithPathOverrides(
            userDataRoot: fallbackRoot,
            baseDirectory: blockedPrimary,
            installerRegistration: () => (null, null),
            action: () => Assert.Equal(fallbackRoot, AppDataPaths.GetWritableDataRoot()));
    }

    private static void WithPathOverrides(
        string userDataRoot,
        string baseDirectory,
        Func<(string? InstallFolder, string? DataFolder)> installerRegistration,
        Action action)
    {
        Func<string>? originalUserDataRootProvider = AppDataPaths.UserDataRootProviderOverride;
        Func<string>? originalBaseDirectoryProvider = AppDataPaths.BaseDirectoryProviderOverride;
        Func<(string? InstallFolder, string? DataFolder)>? originalInstallerRegistrationProvider = AppDataPaths.InstallerRegistrationProviderOverride;

        try
        {
            AppDataPaths.UserDataRootProviderOverride = () => userDataRoot;
            AppDataPaths.BaseDirectoryProviderOverride = () => baseDirectory;
            AppDataPaths.InstallerRegistrationProviderOverride = installerRegistration;
            action();
        }
        finally
        {
            AppDataPaths.UserDataRootProviderOverride = originalUserDataRootProvider;
            AppDataPaths.BaseDirectoryProviderOverride = originalBaseDirectoryProvider;
            AppDataPaths.InstallerRegistrationProviderOverride = originalInstallerRegistrationProvider;
        }
    }
}

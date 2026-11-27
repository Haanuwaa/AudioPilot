using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

[Collection("LoggerFileIsolation")]
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

    [Theory]
    [InlineData("installed")]
    [InlineData("portable")]
    [InlineData("portable-alongside-installed")]
    [InlineData("portable-unwritable")]
    public void WindowPlacement_UsesLocalAppDataOnlyForTheRegisteredInstallation(string deployment)
    {
        using var scope = new TestScopedDirectory(nameof(WindowPlacement_UsesLocalAppDataOnlyForTheRegisteredInstallation));
        string installRoot = Path.Combine(scope.Root, "installed");
        string portableRoot = Path.Combine(scope.Root, "portable");
        string dataRoot = Path.Combine(scope.Root, "roaming");
        string fallbackRoot = Path.Combine(scope.Root, "fallback");
        string localRoot = Path.Combine(scope.Root, "local");
        if (deployment == "portable-unwritable") File.WriteAllText(portableRoot, "not-a-directory");
        WithPathOverrides(fallbackRoot, deployment == "installed" ? installRoot : portableRoot,
            () => deployment == "portable" ? (null, null) : (installRoot, dataRoot), () =>
            {
                string expectedRoot = deployment switch
                {
                    "installed" => Path.Combine(localRoot, "AudioPilot"),
                    "portable-unwritable" => fallbackRoot,
                    _ => portableRoot,
                };
                Assert.Equal(Path.Combine(expectedRoot, "window-placement.json"), AppDataPaths.GetWindowPlacementPath(localRoot));
                Assert.Equal(expectedRoot, AppDataPaths.GetLocalStateRoot(localRoot));
                Assert.False(Directory.Exists(localRoot));
            });
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

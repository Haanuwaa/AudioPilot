using AudioPilot.Tests.Helpers;
using AudioPilot.Updater;

namespace AudioPilot.Tests.Services;

public sealed class PortableLauncherTests
{
    [Fact]
    public void CurrentApp_ResolvesExecutableUnderCurrentGenerationAndRejectsMissingGeneration()
    {
        using var directory = new TestScopedDirectory(nameof(PortableLauncherTests));
        var workspace = new OwnedWorkspace(directory.Root);

        string resolved = PortableLauncher.CurrentApp(workspace, new PortableState(1, Current: "1.0.1", Previous: "1.0.0", PendingHealth: false));
        Assert.Equal(workspace.Resolve(Path.Combine("versions", "1.0.1", "AudioPilot.exe")), resolved);

        Assert.Throws<InvalidOperationException>(() =>
            PortableLauncher.CurrentApp(workspace, new PortableState(1, Current: null, Previous: null, PendingHealth: false)));
    }

    [Fact]
    public void ValidatePreviousApp_AcceptsRecordedGenerationsCaseInsensitively()
    {
        using var directory = new TestScopedDirectory(nameof(PortableLauncherTests));
        var workspace = new OwnedWorkspace(directory.Root);
        var state = new PortableState(1, Current: "1.0.1", Previous: "1.0.0", PendingHealth: false);

        string currentPath = workspace.Resolve(Path.Combine("versions", "1.0.1", "AudioPilot.exe"));
        string previousPath = workspace.Resolve(Path.Combine("versions", "1.0.0", "AudioPilot.exe"));

        PortableLauncher.ValidatePreviousApp(workspace, state, currentPath);
        PortableLauncher.ValidatePreviousApp(workspace, state, previousPath.ToUpperInvariant());
    }

    [Fact]
    public void ValidatePreviousApp_RejectsExecutablesOutsideRecordedGenerations()
    {
        using var directory = new TestScopedDirectory(nameof(PortableLauncherTests));
        var workspace = new OwnedWorkspace(directory.Root);
        var state = new PortableState(1, Current: "1.0.1", Previous: "1.0.0", PendingHealth: false);

        string rogueGeneration = workspace.Resolve(Path.Combine("versions", "9.9.9", "AudioPilot.exe"));
        string outsideWorkspace = Path.Combine(directory.Root, "AudioPilot.exe");

        Assert.Throws<InvalidDataException>(() => PortableLauncher.ValidatePreviousApp(workspace, state, rogueGeneration));
        Assert.Throws<InvalidDataException>(() => PortableLauncher.ValidatePreviousApp(workspace, state, outsideWorkspace));
        Assert.Throws<InvalidDataException>(() => PortableLauncher.ValidatePreviousApp(workspace, new PortableState(1, Current: null, Previous: null, PendingHealth: false), rogueGeneration));
    }
}

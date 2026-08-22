using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppPostSaveCoordinatorTests
{
    [Fact]
    public void BuildMuteApplication_UsesDeafenAsSharedOverride()
    {
        PostSaveMuteApplication result = AppPostSaveCoordinator.BuildMuteApplication(
            currentDeafen: true,
            currentMuteMic: false,
            currentMuteSound: false);

        Assert.True(result.MuteMicrophone);
        Assert.True(result.MutePlayback);
    }
}

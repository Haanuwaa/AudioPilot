namespace AudioPilot.Tests.Platform;

public sealed class UiStallRecoveryLaunchTests
{
    [Fact]
    public void RecoveryLaunchCarriesOnlyExactIdentityAndDoesNotInheritUpdateControl()
    {
        var owner = new SingleInstanceOwnerIdentity(42, DateTime.UtcNow.Ticks);
        var info = UiStallRecoveryLaunch.CreateStartInfo(@"C:\AudioPilot\app\AudioPilot.exe", owner);
        Assert.False(info.UseShellExecute);
        Assert.Equal(@"C:\AudioPilot\app\AudioPilot.exe", info.FileName);
        Assert.False(info.Environment.ContainsKey(AudioPilot.UpdateProtocol.UpdateLifecycleWire.EnvironmentVariable));
        Assert.False(info.Environment.ContainsKey(AudioPilot.Services.Updates.AppUpdateLifecycleSession.AttachmentEnvironment));
        Assert.False(info.Environment.ContainsKey("AUDIOPILOT_DIAGNOSTIC_SESSION"));
        Assert.True(UiStallRecoveryLaunch.TryParse([.. info.ArgumentList], out var parsed));
        Assert.Equal(owner, parsed);
        Assert.False(UiStallRecoveryLaunch.TryParse([.. info.ArgumentList, "--exit"], out _));
        Assert.False(UiStallRecoveryLaunch.TryParse([info.ArgumentList[0], "0", info.ArgumentList[2]], out _));
        Assert.False(UiStallRecoveryLaunch.TryParse([info.ArgumentList[0], "42", long.MaxValue.ToString()], out _));
    }
}

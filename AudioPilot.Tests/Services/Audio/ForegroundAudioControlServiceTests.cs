namespace AudioPilot.Tests.Services.Audio;

public sealed class ForegroundAudioControlServiceTests
{
    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("Shell_SecondaryTrayWnd", true)]
    [InlineData("CabinetWClass", false)]
    [InlineData("MozillaWindowClass", false)]
    public void ShellSurfaces_AreExcludedWithoutExcludingExplorerOrBrowsers(string windowClass, bool excluded)
    {
        Assert.Equal(excluded, ForegroundAudioControlService.IsShellSurface(windowClass));
    }

    [Fact]
    public void MissingForegroundTarget_ReturnsClearStatusWithoutTouchingAudio()
    {
        var service = new ForegroundAudioControlService(AudioPilot.Logging.Logger.Instance);
        var result = service.Apply(null, 5, TestContext.Current.CancellationToken);
        Assert.Equal("no-foreground-app", result.Code);
        Assert.Empty(result.Name);
        Assert.Equal(0, result.Changed);
    }

    [Fact]
    public void ShellProcessTarget_DoesNotMatchApplicationsItLaunched()
    {
        var target = new ForegroundProcessTarget(10, 100, "Explorer", IncludeDescendants: false);
        Assert.True(ForegroundAudioControlService.BelongsToTarget(10, target, _ => 0, _ => 100));
        Assert.False(ForegroundAudioControlService.BelongsToTarget(11, target, _ => 10, pid => pid == 10 ? 100 : 200));
    }

    [Fact]
    public void CancelledForegroundCommand_DoesNotTouchAudio()
    {
        var service = new ForegroundAudioControlService(AudioPilot.Logging.Logger.Instance);
        var result = service.Apply(new(42, 100, "App"), 5, new CancellationToken(canceled: true));
        Assert.Equal("cancelled", result.Code);
        Assert.Equal(0, result.Changed);
    }

    [Fact]
    public void TargetResolution_AcceptsDescendantsButRejectsOtherAppsAndReusedParentIds()
    {
        var target = new ForegroundProcessTarget(10, 100, "browser");
        var parents = new Dictionary<int, int> { [12] = 11, [11] = 10, [20] = 1, [30] = 31, [31] = 30 };
        var started = new Dictionary<int, long> { [10] = 100, [11] = 110, [12] = 120, [20] = 200, [1] = 1, [30] = 300, [31] = 300 };
        bool Matches(int pid) => ForegroundAudioControlService.BelongsToTarget(pid, target,
            id => parents.GetValueOrDefault(id), id => started.TryGetValue(id, out long value) ? value : null);
        Assert.True(Matches(10));
        Assert.True(Matches(12));
        Assert.False(Matches(20));
        Assert.False(Matches(30));
        Assert.False(Matches(404));
        started[10] = 130;
        Assert.False(Matches(12));
        Assert.False(Matches(10));
    }

    [Theory]
    [InlineData(0.98f, 5, 1f)]
    [InlineData(0.02f, -5, 0f)]
    [InlineData(0.3f, 5, 0.35f)]
    [InlineData(0.8f, -5, 0.75f)]
    public void RelativeVolume_PreservesPerSessionLevelsAndClamps(float before, int delta, float expected)
    {
        Assert.Equal(expected, ForegroundAudioControlService.AdjustVolume(before, delta), 4);
    }
}

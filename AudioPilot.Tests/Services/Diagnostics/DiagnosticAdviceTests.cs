using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot.Tests.Services.Diagnostics;

public sealed class DiagnosticAdviceTests
{
    [Theory]
    [InlineData("Registered", false)]
    [InlineData("Fallback", false)]
    [InlineData("ExternalConflict", true)]
    [InlineData("MouseHookUnavailable", true)]
    public void WorkingHotkeysDoNotSuggestRepairs_AndFailuresNavigateToTheirTab(string outcome, bool issue)
    {
        var health = new RuntimeHealthSnapshot(DateTimeOffset.UtcNow, "gui", "inactive", null,
            new(true, false, false, 1, true, true, true, [new(AppConstants.Hotkeys.InputSwitchHotkeyId, outcome, null, 0)]), null, 0);
        var advice = DiagnosticAdvice.ForHealth(health);
        if (issue) Assert.Equal(new DiagnosticReviewTarget(1), Assert.Single(advice).Target);
        else Assert.Empty(advice);
    }

    [Fact]
    public void UnconfirmedMediaWarnsAgainstBlindRetry_AndDoesNotOfferASettingChange()
    {
        var advice = DiagnosticAdvice.ForOperation("media-command-unconfirmed", "media", "next-track", true);
        Assert.NotNull(advice);
        Assert.False(advice.CanReview);
        Assert.Contains("already have taken effect", advice.NextStep, StringComparison.Ordinal);
    }
}

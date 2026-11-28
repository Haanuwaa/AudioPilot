using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsPersistenceTransactionTests
{
    private static readonly string[] expected = ["save"];

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    public void RegistrationIsOnlyTouchedWhenItsEffectiveSettingsChange(bool before, bool after, bool changeMode, bool expectedRegistration)
    {
        var previous = new Settings { Startup = new() { Enabled = before } };
        Settings candidate = previous.Clone();
        candidate.Startup.Enabled = after;
        candidate.Startup.UseTaskScheduler = changeMode;
        List<string> steps = [];
        SettingsPersistenceTransaction.Save(previous, candidate, saved => { Assert.Same(candidate, saved); steps.Add("save"); },
            (_, persist) => { steps.Add("register"); persist(); steps.Add("commit"); });
        Assert.Equal(expectedRegistration ? ["register", "save", "commit"] : expected, steps);
        Assert.Equal(before, previous.Startup.Enabled);
        Assert.False(previous.Startup.UseTaskScheduler);
    }

    [Fact]
    public void PersistenceFailureRemainsInsideRegistrationTransaction()
    {
        var original = new Settings();
        Settings candidate = original.Clone();
        candidate.Startup.Enabled = true;
        bool rolledBack = false;
        Assert.Throws<IOException>(() => SettingsPersistenceTransaction.Save(original, candidate,
            _ => throw new IOException("disk full"), (_, persist) =>
            {
                try { persist(); }
                catch { rolledBack = true; throw; }
            }));
        Assert.True(rolledBack);
        Assert.False(original.Startup.Enabled);
    }
}

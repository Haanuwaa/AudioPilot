using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Configuration;

[Trait(TestCategories.Name, TestCategories.Integration)]
public sealed class StartupTaskIntegrationTests
{
    /// <summary>
    /// Requires explicit execution because it briefly registers a uniquely named task in Windows.
    /// Registration does not launch the application; the task is always removed afterward.
    /// </summary>
    [Fact(Explicit = true)]
    public void RegisterReadDisableDelete_RoundTripsThroughWindowsTaskScheduler()
    {
        var store = new WindowsStartupTaskStore($"AudioPilot-Test-{Guid.NewGuid():N}");
        string executable = Path.Combine(AppContext.BaseDirectory, "AudioPilot.exe");
        try
        {
            Assert.Null(store.Read());
            store.Write(StartupTaskDefinition.Create(executable, store.UserSid));
            Assert.True(StartupTaskDefinition.Matches(store.Read(), executable, store.UserSid, false), store.Read());
            store.Write(StartupTaskDefinition.Create(executable, store.UserSid, enabled: false));
            Assert.False(StartupTaskDefinition.Matches(store.Read(), executable, store.UserSid, false));
            Assert.True(StartupTaskDefinition.Matches(store.Read(), executable, store.UserSid, true));
        }
        finally
        {
            store.Delete();
        }
        Assert.Null(store.Read());
    }
}

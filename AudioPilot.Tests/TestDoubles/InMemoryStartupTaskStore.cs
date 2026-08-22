namespace AudioPilot.Tests.TestDoubles;

internal sealed class InMemoryStartupTaskStore : IStartupTaskStore
{
    internal static StartupService CreateStartupService() => new(
        "Software\\AudioPilot.Tests\\Startup", "AudioPilotTest", logger: null,
        new InMemoryUserRegistryAccessor(),
        Path.Combine(AppContext.BaseDirectory, "AudioPilot.exe"), new InMemoryStartupTaskStore());

    public string UserSid => "S-1-5-21-100-200-300-1001";
    public string? Xml { get; set; }
    public bool FailNextWrite { get; set; }
    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }

    public string? Read()
    {
        ReadCount++;
        return Xml;
    }

    public void Write(string xml)
    {
        WriteCount++;
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new UnauthorizedAccessException("Simulated Task Scheduler denial.");
        }
        Xml = xml;
    }

    public void Delete() => Xml = null;
}

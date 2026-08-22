namespace AudioPilot.Tests.Helpers;

internal sealed class TestSettingsWorkspace : TestScopedDirectory
{
    public string PrimaryDir { get; }
    public string FallbackDir { get; }

    public TestSettingsWorkspace(string scopeName)
        : base(scopeName)
    {
        PrimaryDir = Path.Combine(Root, "primary");
        FallbackDir = Path.Combine(Root, "fallback");

        Directory.CreateDirectory(PrimaryDir);
        Directory.CreateDirectory(FallbackDir);
    }
}

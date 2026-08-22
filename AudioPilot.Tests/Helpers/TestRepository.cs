using AudioPilot.CliHost;

namespace AudioPilot.Tests.Helpers;

internal static class TestRepository
{
    internal static string Root
    {
        get
        {
            Assert.True(RepositoryRootLocator.TryFind([AppContext.BaseDirectory], out string root),
                "Could not locate the AudioPilot repository root from the test output directory.");
            return root;
        }
    }
}

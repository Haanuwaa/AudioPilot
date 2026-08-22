namespace AudioPilot.Tests.Helpers;

internal class TestScopedDirectory : IDisposable
{
    public string Root { get; }

    internal TestScopedDirectory(string scopeName)
    {
        Root = Path.Combine(Path.GetTempPath(), "AudioPilot.Tests", scopeName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        DisposeResources();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
        }
    }

    protected virtual void DisposeResources()
    {
    }
}

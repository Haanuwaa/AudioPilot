using AudioPilot.Services.Configuration;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"AudioPilot-atomic-writer-{Guid.NewGuid():N}");

    [Fact]
    public void Write_WhenTemporaryWriteFails_PreservesExistingDestination()
    {
        Directory.CreateDirectory(_directory);
        string destination = Path.Combine(_directory, "export.json");
        File.WriteAllText(destination, "original");

        Assert.Throws<IOException>(() => AtomicFileWriter.Write(destination, temporaryPath =>
        {
            File.WriteAllText(temporaryPath, "partial");
            throw new IOException("simulated export failure");
        }));

        Assert.Equal("original", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(_directory, "export.json.*.tmp"));
    }

    [Fact]
    public void Write_ReplacesExistingDestinationAfterSuccessfulWrite()
    {
        Directory.CreateDirectory(_directory);
        string destination = Path.Combine(_directory, "export.json");
        File.WriteAllText(destination, "original");

        AtomicFileWriter.WriteAllText(destination, "replacement");

        Assert.Equal("replacement", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(_directory, "export.json.*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

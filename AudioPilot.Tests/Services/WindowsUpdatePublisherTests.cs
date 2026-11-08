using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using AudioPilot.Updater;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Tests.Services;

public sealed class WindowsUpdatePublisherTests : IDisposable
{
    private const string Publisher = "BB7EE30CA7475E632C3DC74B25E00364AF0DFECE3E317C3738D7ABF2BB5E0371";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-signatures-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PinnedSelfSignedPolicyVerifiesActualDigestAndPublisherWithoutTrustStoreChanges()
    {
        string path = WriteFixture();
        WindowsUpdatePublisher.VerifyFile(path, new(Publisher, true));
        Assert.Throws<CryptographicException>(() => WindowsUpdatePublisher.VerifyFile(path, new(new string('A', 64), true)));
        Assert.Throws<CryptographicException>(() => WindowsUpdatePublisher.VerifyFile(path, new(Publisher, false)));
        byte[] tampered = File.ReadAllBytes(path);
        using (var pe = new PEReader(new MemoryStream(tampered))) tampered[pe.PEHeaders.SectionHeaders[0].PointerToRawData + 16] ^= 1;
        File.WriteAllBytes(path, tampered);
        Assert.Throws<CryptographicException>(() => WindowsUpdatePublisher.VerifyFile(path, new(Publisher, true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortablePublisherCheckRequiresEveryEntryPointAndReleasesTemporaryFiles(bool missing)
    {
        string fixture = WriteFixture();
        var workspace = new OwnedWorkspace(Path.Combine(_root, "work"));
        string zip = Path.Combine(_root, "payload.zip");
        string[] entries = ["AudioPilot.exe", "AudioPilot.dll", "AudioPilot.Cli.exe", "AudioPilot.Cli.dll", "AudioPilot.Updater.exe",
            "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.UpdateProtocol.dll", "AudioPilot.TufBridge.exe"];
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (string name in entries.Where(name => !missing || name != "AudioPilot.TufBridge.exe")) archive.CreateEntryFromFile(fixture, name);
        var work = WindowsUpdatePublisher.VerifyPackageAsync(workspace, zip, "portable", new(Publisher, true), TestContext.Current.CancellationToken);
        if (missing) await Assert.ThrowsAsync<InvalidDataException>(() => work);
        else await work;
        Assert.Empty(Directory.EnumerateDirectories(workspace.Resolve("publisher-check")));
    }

    private string WriteFixture()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "publisher.dll");
        using var input = typeof(WindowsUpdatePublisherTests).Assembly.GetManifestResourceStream("AudioPilot.Tests.Fixtures.Updater.publisher.dll")!;
        using var output = File.Create(path);
        input.CopyTo(output);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

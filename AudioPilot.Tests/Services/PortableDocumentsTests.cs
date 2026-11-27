using System.IO.Compression;
using System.Security.Cryptography;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater;

namespace AudioPilot.Tests.Services;

[Trait("Execution", "Pure")]
public sealed class PortableDocumentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-documents-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CommitAndRecovery_ProjectMatchingDocumentsWithoutRewritingUnchangedFiles()
    {
        var workspace = CreateWorkspace();
        var deployment = new PortableDeployment(workspace);
        string personal = Path.Combine(_root, "personal.txt");
        File.WriteAllText(personal, "unrelated");
        var first = await Apply(deployment, Archive("first"), "1.0.0", true);
        Assert.True(first.Committed);
        AssertDocuments("first");
        string extra = Path.Combine(_root, "licenses", "personal-notice.txt");
        File.WriteAllText(extra, "unrelated notice");
        Assert.True((await Apply(deployment, Archive("rejected"), "1.0.1", false)).Recovered);
        AssertDocuments("first");
        string? duringPending = null;
        await Assert.ThrowsAsync<IOException>(() => Apply(deployment, Archive("interrupted"), "1.0.2", true, () =>
        {
            PortableDocuments.Synchronize(workspace, deployment.ReadState());
            duringPending = File.ReadAllText(Path.Combine(_root, "ABOUT.txt"));
            throw new IOException("Simulated interruption.");
        }));
        Assert.Equal("first/ABOUT.txt", duringPending);
        File.WriteAllText(Path.Combine(_root, "ABOUT.txt"), "incomplete projection");
        Assert.Equal(first.Current, deployment.Recover().Current);
        AssertDocuments("first");
        Assert.True((await Apply(deployment, Archive("committed"), "1.0.3", true)).Committed);
        AssertDocuments("committed");
        File.Delete(Path.Combine(_root, "licenses", "LICENSE"));
        PortableDocuments.Synchronize(workspace, deployment.ReadState(), onlyIfNeeded: true);
        AssertDocuments("committed");
        string about = Path.Combine(_root, "ABOUT.txt");
        DateTime untouched = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(about, untouched);
        PortableDocuments.Synchronize(workspace, deployment.ReadState());
        Assert.Equal(untouched, File.GetLastWriteTimeUtc(about));
        PortableDocuments.Synchronize(workspace, deployment.ReadState(), onlyIfNeeded: true);
        Assert.Equal(untouched, File.GetLastWriteTimeUtc(about));
        Assert.Equal("unrelated", File.ReadAllText(personal));
        Assert.Equal("unrelated notice", File.ReadAllText(extra));
        Assert.False(File.Exists(Path.Combine(_root, "LICENSE")));
        Assert.False(File.Exists(Path.Combine(_root, "THIRD-PARTY-NOTICES.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "UPDATER-NOTICES.txt")));
    }

    [Theory]
    [InlineData("ABOUT.txt")]
    [InlineData("licenses/LICENSE")]
    public async Task LockedDocument_DoesNotUndoCommitAndIsRepairedAfterRelease(string document)
    {
        var workspace = CreateWorkspace();
        var deployment = new PortableDeployment(workspace);
        string path = Path.Combine(_root, document);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "old");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True((await Apply(deployment, Archive("new"), "1.0.0", true)).Committed);
            Assert.False(deployment.ReadState().PendingHealth);
            Assert.Equal("old", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(workspace.Root, ".document-*.new"));
        }
        PortableDocuments.Synchronize(workspace, deployment.ReadState(), onlyIfNeeded: true);
        AssertDocuments("new");
        File.WriteAllText(path, "damaged");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PortableDocuments.Synchronize(workspace, deployment.ReadState());
            Assert.False(File.Exists(workspace.Resolve(PortableDocuments.ReceiptName)));
            Assert.Empty(Directory.GetFiles(workspace.Root, ".document-*.new"));
        }
        PortableDocuments.Synchronize(workspace, deployment.ReadState(), onlyIfNeeded: true);
        AssertDocuments("new");
    }

    private OwnedWorkspace CreateWorkspace()
    {
        Directory.CreateDirectory(_root);
        var workspace = new OwnedWorkspace(Path.Combine(_root, "app"));
        OwnedWorkspace.WriteJson(workspace.Resolve(PortableLayout.FileName), new PortableLayoutDescriptor(1, Guid.NewGuid().ToString("N"), true));
        return workspace;
    }

    private string Archive(string label)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string document in PortableDocuments.Names)
        {
            using var writer = new StreamWriter(zip.CreateEntry(document).Open());
            writer.Write(label + "/" + document);
        }
        return path;
    }

    private static Task<DeploymentResult> Apply(PortableDeployment deployment, string path, string version, bool healthy, Action? switched = null) =>
        deployment.ApplyAsync(path, new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            version, (_, _) => Task.FromResult(healthy), Token, switched);

    private void AssertDocuments(string label)
    {
        foreach (string document in PortableDocuments.Names)
            Assert.Equal(label + "/" + document, File.ReadAllText(Path.Combine(_root, document)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

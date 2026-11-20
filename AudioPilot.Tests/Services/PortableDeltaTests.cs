using System.IO.Compression;
using System.Security.Cryptography;
using AudioPilot.Updater;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Tests.Services;

public sealed class PortableDeltaTests
{
    [Fact]
    public async Task PreparedDeltaReusesVerifiedFilesAndUsesExistingRollback()
    {
        using var fixture = new Fixture();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.CreateAsync(token);
        var portable = new PortableDeployment(fixture.Workspace);
        var original = await portable.ApplyAsync(fixture.Baseline, new FileInfo(fixture.Baseline).Length, Hash(fixture.Baseline),
            "1.0.0", (_, _) => Task.FromResult(true), token);
        string originalDirectory = fixture.Workspace.Resolve("versions/" + original.Current);
        await PortableDelta.StageAsync(fixture.Workspace, originalDirectory, fixture.Generation, fixture.Delta, fixture.Authorization, "1.0.0", token);
        Assert.Equal(original.Current, portable.ReadState().Current);
        Assert.Equal("before", File.ReadAllText(Path.Combine(originalDirectory, "AudioPilot.exe")));
        using var lease = await PortableDelta.OpenVerifiedAsync(fixture.Delta, fixture.Authorization.Delta!, token);
        using var prepared = new VerifiedLocalUpdate(() => fixture.Authorization, fixture.Authorization, fixture.Delta, lease,
            preparedGeneration: fixture.Generation, baseVersion: "1.0.0");
        var rollback = await portable.ApplyAsync(prepared, (folder, _) =>
        {
            Assert.Equal("after", File.ReadAllText(Path.Combine(folder, "AudioPilot.exe")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "nested", "reuse.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "obsolete.txt")));
            Assert.True(portable.ReadState().PendingHealth);
            return Task.FromResult(false);
        }, token);
        Assert.True(rollback.Recovered);
        Assert.Equal(original.Current, portable.ReadState().Current);
        Assert.Equal("before", File.ReadAllText(Path.Combine(originalDirectory, "AudioPilot.exe")));
    }

    [Fact]
    public async Task DeltaRejectsModifiedBaselineEvenWhenThatFileWouldBeReplaced()
    {
        using var fixture = new Fixture();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.CreateAsync(token);
        string baseline = fixture.Workspace.Resolve("baseline");
        ZipFile.ExtractToDirectory(fixture.Baseline, baseline);
        File.WriteAllText(Path.Combine(baseline, "AudioPilot.exe"), "edited");
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableDelta.StageAsync(fixture.Workspace, baseline, fixture.Generation,
            fixture.Delta, fixture.Authorization, "1.0.0", token));
        Assert.Equal("edited", File.ReadAllText(Path.Combine(baseline, "AudioPilot.exe")));
    }

    [Fact]
    public async Task PreparedDeltaRejectsChangedFilesBeforeThePointerCanSwitch()
    {
        using var fixture = new Fixture();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.CreateAsync(token);
        var portable = new PortableDeployment(fixture.Workspace);
        var baseline = await portable.ApplyAsync(fixture.Baseline, new FileInfo(fixture.Baseline).Length, Hash(fixture.Baseline),
            "1.0.0", (_, _) => Task.FromResult(true), token);
        await PortableDelta.StageAsync(fixture.Workspace, fixture.Workspace.Resolve("versions/" + baseline.Current),
            fixture.Generation, fixture.Delta, fixture.Authorization, "1.0.0", token);
        File.WriteAllText(fixture.Workspace.Resolve($"job-delta/{fixture.Generation}/AudioPilot.exe"), "edited");
        using var lease = await PortableDelta.OpenVerifiedAsync(fixture.Delta, fixture.Authorization.Delta!, token);
        using var prepared = new VerifiedLocalUpdate(() => fixture.Authorization, fixture.Authorization, fixture.Delta, lease,
            preparedGeneration: fixture.Generation, baseVersion: "1.0.0");
        await Assert.ThrowsAsync<InvalidDataException>(() => portable.ApplyAsync(prepared,
            (_, _) => throw new InvalidOperationException("Unverified candidate launched."), token));
        Assert.Equal(baseline.Current, portable.ReadState().Current);
        Assert.False(Directory.Exists(fixture.Workspace.Resolve("versions/" + fixture.Generation)));
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-delta-" + Guid.NewGuid().ToString("N"));
        public OwnedWorkspace Workspace { get; }
        public string Baseline => Path.Combine(_root, "baseline.zip");
        private string Target => Path.Combine(_root, "target.zip");
        public string Delta => Path.Combine(_root, PortableDelta.Name("target.zip", "1.0.0"));
        public string Generation { get; } = "1.0.1_" + Guid.NewGuid().ToString("N");
        public AuthorizedUpdate Authorization
        {
            get
            {
                UpdateArtifact target = new("target.zip", "portable", "win-x64", "self-contained", new FileInfo(Target).Length, Hash(Target));
                var release = new UpdateRelease(1, "AudioPilot", "local-test", 1, 1, "1.0.1", "1.0.0", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, [target]);
                return new(release, target, Delta: target with { FileName = Path.GetFileName(Delta), Length = new FileInfo(Delta).Length, Sha256 = Hash(Delta) });
            }
        }
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = new(Path.Combine(_root, "work"));
        }
        public async Task CreateAsync(CancellationToken token)
        {
            Write(Baseline, ("AudioPilot.exe", "before"), ("nested/reuse.txt", "keep"), ("obsolete.txt", "delete"));
            Write(Target, ("AudioPilot.exe", "after"), ("nested/reuse.txt", "keep"));
            await PortableDelta.CreateAsync(Baseline, Target, "1.0.0", Delta, token);
        }
        private static void Write(string path, params (string Name, string Text)[] files)
        {
            using var bytes = File.Create(path);
            using var archive = new ZipArchive(bytes, ZipArchiveMode.Create);
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        public void Dispose()
        {
            OwnedWorkspace.CheckAncestors(_root);
            Directory.Delete(_root, recursive: true);
        }
    }
}

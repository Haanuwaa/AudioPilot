using AudioPilot.Tests.Helpers;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Ui;

namespace AudioPilot.Tests.Services;

[Trait(TestCategories.Execution, TestCategories.Pure)]
public sealed class UpdaterStorageTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("incomplete")]
    public void ActiveRuntimeIsReusedOnlyAfterFullVerification_AndRepairWaitsForItsLease(string state)
    {
        using var fixture = new TestScopedDirectory(nameof(UpdaterStorageTests));
        string payload = Path.Combine(fixture.Root, "payload"), pool = Path.Combine(fixture.Root, "runtime");
        Directory.CreateDirectory(payload);
        foreach (string name in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            File.WriteAllText(Path.Combine(payload, name), "synthetic runtime");
        Directory.CreateDirectory(Path.Combine(payload, "es"));
        byte[] expected = new byte[2 * 65536 + 17]; expected[^1] = 1;
        File.WriteAllBytes(Path.Combine(payload, "es", "AudioPilot.resources.dll"), expected);
        string staged = UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken);
        string runtime = Path.GetDirectoryName(staged)!;
        string satellite = Path.Combine(runtime, "es", "AudioPilot.resources.dll");
        byte[] current = (byte[])expected.Clone();
        if (state == "corrupt")
        {
            DateTime timestamp = File.GetLastWriteTimeUtc(satellite);
            current[^1] ^= 0xff;
            File.WriteAllBytes(satellite, current);
            File.SetLastWriteTimeUtc(satellite, timestamp);
        }
        if (state == "incomplete") File.Delete(Path.Combine(runtime, "runtime.ready"));
        using (File.Open(Path.Combine(runtime, "runtime.inuse"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (state == "valid")
                Assert.Equal(staged, UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken));
            else
                Assert.ThrowsAny<IOException>(() => UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken));
            Assert.Equal(current, File.ReadAllBytes(satellite));
            Assert.True(File.Exists(staged));
        }
        Assert.Equal(staged, UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken));
        Assert.Equal(expected, File.ReadAllBytes(satellite));
        Assert.Equal("AudioPilot runtime ready 1", File.ReadAllText(Path.Combine(runtime, "runtime.ready")));
    }

    [Fact]
    public void RuntimeSourceHashFailurePreservesTheOriginalError_AndCancellationDoesNotCreateACache()
    {
        using var fixture = new TestScopedDirectory(nameof(UpdaterStorageTests));
        string payload = Path.Combine(fixture.Root, "payload"), pool = Path.Combine(fixture.Root, "runtime");
        Directory.CreateDirectory(payload);
        foreach (string name in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            File.WriteAllText(Path.Combine(payload, name), "synthetic runtime");
        using (File.Open(Path.Combine(payload, "AudioPilot.Updater.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(pool));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => UpdateRuntimeStager.StageCore(payload, pool, stop.Token));
        Assert.False(Directory.Exists(pool));
        Assert.True(File.Exists(UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void RuntimeStagingPreservesAndRepairsCultureResources_AndCancellationCleansTheirDirectories()
    {
        using var fixture = new TestScopedDirectory(nameof(UpdaterStorageTests));
        string payload = Path.Combine(fixture.Root, "payload"), pool = Path.Combine(fixture.Root, "pool", "nested", "runtime");
        Directory.CreateDirectory(payload);
        foreach (string name in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            File.WriteAllText(Path.Combine(payload, name), "synthetic runtime");
        foreach (string culture in new[] { "en", "es" })
        {
            Directory.CreateDirectory(Path.Combine(payload, culture));
            File.WriteAllText(Path.Combine(payload, culture, "AudioPilot.resources.dll"), culture);
        }
        string runtime = Path.GetDirectoryName(UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken))!;
        Assert.Equal("en", File.ReadAllText(Path.Combine(runtime, "en", "AudioPilot.resources.dll")));
        string spanish = Path.Combine(runtime, "es", "AudioPilot.resources.dll");
        Assert.Equal("es", File.ReadAllText(spanish));
        File.WriteAllText(spanish, "xx");
        Assert.Equal(runtime, Path.GetDirectoryName(UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken)));
        Assert.Equal("es", File.ReadAllText(spanish));
        string canceledPool = Path.Combine(fixture.Root, "canceled");
        using var cancellation = new CancellationTokenSource();
        int copied = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => UpdateRuntimeStager.StageCore(payload, canceledPool, cancellation.Token,
            () => { if (++copied == 5) cancellation.Cancel(); }));
        Assert.Empty(Directory.EnumerateDirectories(canceledPool));
    }

    [Fact]
    public void RuntimeCancellationCleansItsOwnedCopy_AndPruningPreservesActiveLeasesAndUnknownDirectories()
    {
        using var fixture = new TestScopedDirectory(nameof(UpdaterStorageTests));
        string payload = Path.Combine(fixture.Root, "payload"), pool = Path.Combine(fixture.Root, "runtime");
        Directory.CreateDirectory(payload);
        foreach (string file in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            File.WriteAllText(Path.Combine(payload, file), "synthetic runtime content");
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => UpdateRuntimeStager.StageCore(payload, pool, cancel.Token, () => cancel.Cancel()));
        Assert.Empty(Directory.EnumerateDirectories(pool));

        string abandoned = RuntimeDirectory(pool), active = RuntimeDirectory(pool);
        string unknown = Path.Combine(pool, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "unowned");
        using (File.Open(Path.Combine(active, "runtime.inuse"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
        {
            string staged = UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(staged));
            Assert.True(Directory.Exists(active));
            Assert.False(Directory.Exists(abandoned));
            Assert.True(File.Exists(Path.Combine(unknown, "keep.txt")));
        }
        UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(active));

        string cached = UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken);
        using var cachedCancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => UpdateRuntimeStager.StageCore(payload, pool, cachedCancellation.Token, () => cachedCancellation.Cancel()));
        Assert.True(File.Exists(cached));
        Assert.Equal(cached, UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RuntimeStagingDetectsSourceAndCacheTailChangesWithoutMetadataHints()
    {
        using var fixture = new TestScopedDirectory(nameof(UpdaterStorageTests));
        string payload = Path.Combine(fixture.Root, "payload"), pool = Path.Combine(fixture.Root, "runtime");
        Directory.CreateDirectory(payload);
        foreach (string name in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            File.WriteAllText(Path.Combine(payload, name), "synthetic runtime content");
        string source = Path.Combine(payload, "AudioPilot.Updater.exe");
        byte[] expected = new byte[2 * 65536 + 17];
        expected[0] = 1; expected[65536] = 2; expected[^1] = 3;
        File.WriteAllBytes(source, expected);
        var sourceTimestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, sourceTimestamp);
        string staged = UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken);
        Assert.Equal(expected, File.ReadAllBytes(staged));

        var cacheTimestamp = sourceTimestamp.AddYears(1);
        File.SetLastWriteTimeUtc(staged, cacheTimestamp);
        Assert.Equal(staged, UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken));
        Assert.Equal(cacheTimestamp, File.GetLastWriteTimeUtc(staged));
        byte[] corrupt = (byte[])expected.Clone(); corrupt[^1] ^= 0xff;
        File.WriteAllBytes(staged, corrupt);
        File.SetLastWriteTimeUtc(staged, cacheTimestamp);
        Assert.Equal(staged, UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken));
        Assert.Equal(expected, File.ReadAllBytes(staged));

        expected[^1] = 4;
        File.WriteAllBytes(source, expected);
        File.SetLastWriteTimeUtc(source, sourceTimestamp);
        string changed = UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken);
        Assert.NotEqual(staged, changed);
        Assert.Equal(expected, File.ReadAllBytes(changed));
    }

    [Fact]
    public void RuntimeStagingRejectsAChangedCopyAndReleasesOwnershipForRetry()
    {
        using var fixture = new TestScopedDirectory(nameof(UpdaterStorageTests));
        string payload = Path.Combine(fixture.Root, "payload"), pool = Path.Combine(fixture.Root, "runtime");
        Directory.CreateDirectory(payload);
        foreach (string name in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            File.WriteAllText(Path.Combine(payload, name), "synthetic runtime content");
        string source = Path.Combine(payload, "AudioPilot.Updater.exe");
        byte[] content = new byte[65536 + 7]; File.WriteAllBytes(source, content);
        int copied = 0;
        Assert.Throws<InvalidDataException>(() => UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken, () =>
        {
            if (++copied != 1) return;
            content[^1] = 1;
            File.WriteAllBytes(source, content);
        }));
        Assert.Empty(Directory.EnumerateDirectories(pool));
        string staged = UpdateRuntimeStager.StageCore(payload, pool, TestContext.Current.CancellationToken);
        Assert.Equal(content, File.ReadAllBytes(staged));
    }

    private static string RuntimeDirectory(string pool)
    {
        string directory = Path.Combine(pool, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".pilot-runtime"), "AudioPilot pilot runtime 1");
        File.WriteAllText(Path.Combine(directory, "partial.dll"), "interrupted payload");
        return directory;
    }

    [Fact]
    public void TemporaryUiLogsAreRemovedOnlyAfterDrain_AndUnknownFilesPreserveTheDirectory()
    {
        using var fixture = new TestScopedDirectory(nameof(UpdaterStorageTests));
        var diagnostics = new UpdaterTemporaryDiagnostics(fixture.Root);
        Directory.CreateDirectory(Path.Combine(diagnostics.Root, "backups"));
        File.WriteAllText(Path.Combine(diagnostics.Root, "AudioPilot.log"), "ui helper log");
        File.WriteAllText(Path.Combine(diagnostics.Root, "backups", "AudioPilot.log.bak.1"), "previous log");
        diagnostics.Cleanup(writerDrained: false);
        Assert.True(Directory.Exists(diagnostics.Root));
        string unrelated = Path.Combine(diagnostics.Root, "keep.txt"); File.WriteAllText(unrelated, "unrelated");
        diagnostics.Cleanup(writerDrained: true);
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(Path.Combine(diagnostics.Root, "AudioPilot.log")));
        File.Delete(unrelated);
        diagnostics.Cleanup(writerDrained: true);
        Assert.False(Directory.Exists(diagnostics.Root));
    }
}

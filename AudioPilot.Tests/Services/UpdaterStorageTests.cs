using AudioPilot.Tests.Helpers;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Ui;

namespace AudioPilot.Tests.Services;

public sealed class UpdaterStorageTests
{
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

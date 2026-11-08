using System.ComponentModel;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Updater;
using AudioPilot.Updater.Ui;

namespace AudioPilot.Tests.Logging;

[Collection("LogPrivacy")]
public sealed class UpdaterLogTests
{
    [Fact]
    public void InstallerErrorsKeepNativeCodesAndStepsWithoutExposingPaths()
    {
        string text = UpdaterLog.Format(new(UpdateLogKind.OperationFailed, UpdateJobPhase.Installing,
            Reason: "installer-reboot-required", Error: new MsiUpdateException(3010, "install")));
        Assert.Contains("nativeCode=3010", text);
        Assert.Contains("msiStep=install", text);
        Assert.Contains("reason=installer-reboot-required", text);
    }

    [Fact]
    public async Task WorkspaceLogForcesRedactionAndKeepsOnlyItsTwoArchivesAcrossRestarts()
    {
        using var directory = new TestScopedDirectory(nameof(UpdaterLogTests));
        var workspace = new OwnedWorkspace(Path.Combine(directory.Root, "work"));
        string path = workspace.Resolve("updater.log");
        string unrelated = workspace.Resolve("unrelated.txt");
        File.WriteAllText(unrelated, "keep me");
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new() { RedactLogContent = false } });
        try
        {
            for (int restart = 0; restart < 4; restart++)
            {
                using (var oversized = File.Create(path)) oversized.SetLength(UpdaterLog.RotationBytes);
                var reopened = workspace;
                UpdaterLog.Attach(reopened);
                Assert.NotNull(reopened.Diagnostics);
                UpdateLog.Write(reopened.Diagnostics, new(UpdateLogKind.OperationFailed, UpdateJobPhase.Checking,
                    Operation: "private\noperation", Version: "secret-version", Reason: "private reason",
                    Error: new IOException(@"C:\Users\ExampleUser\settings.json" + Environment.NewLine + "https://example.invalid/?token=SecretToken", new Win32Exception(5, "PrivateMessage"))));
                await reopened.Diagnostics.DisposeAsync();
            }
            string text = File.ReadAllText(path);
            Assert.Contains("event=OperationFailed", text);
            Assert.Contains("phase=Checking", text);
            Assert.Contains("error=IOException", text);
            Assert.Contains("inner=Win32Exception", text);
            Assert.Contains("nativeCode=5", text);
            Assert.Contains("hash=", text);
            foreach (string sensitive in new[] { "ExampleUser", "settings.json", "example.invalid", "SecretToken", "PrivateMessage", "private reason", "private\noperation", "secret-version" })
                Assert.DoesNotContain(sensitive, text);
            string[] archives = Directory.GetFiles(workspace.Resolve("backups"), "updater.log.bak*");
            Assert.Equal(UpdaterLog.ArchiveCount, archives.Length);
            Assert.True(new FileInfo(path).Length < UpdaterLog.RotationBytes);
            Assert.Equal("keep me", File.ReadAllText(unrelated));
        }
        finally { LogPrivacy.ApplySettings(null); }
    }
}

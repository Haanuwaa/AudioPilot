using AudioPilot.Localization;
using AudioPilot.Tests.Helpers;
using AudioPilot.Updater;
using AudioPilot.Updater.Ui;

namespace AudioPilot.Tests.Services;

[Trait(TestCategories.Execution, TestCategories.Pure)]
public sealed class MsiDeploymentTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void BusyAndRebootFeedbackPreserveRetryAndRecoveryActions(string language)
    {
        var localization = new LocalizationService(language);
        var busy = UpdaterPresentation.From(new(1, 0, "job", "context", UpdateJobPhase.Deferred, Reason: "installer-busy"), false, false, language: localization);
        Assert.Equal(localization["Update_InstallerBusy"], busy.Message);
        Assert.True(busy.CanInstall);
        Assert.True(busy.CanCancel);
        var reboot = UpdaterPresentation.From(new(1, 0, "job", "context", UpdateJobPhase.RecoveryRequired, Reason: "installer-reboot-required"), false, false, language: localization);
        Assert.Equal(localization["Update_InstallerRebootRecovery"], reboot.Message);
        Assert.True(reboot.CanRecover);
        Assert.False(reboot.CanCancel);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(3010, true)]
    [InlineData(1603, true)]
    public void ReservedTransactionCommitsOnlyAfterHealthOtherwiseRollsBackAndPreservesNativeResult(int result, bool healthy)
    {
        using var directory = new TestScopedDirectory(nameof(MsiDeploymentTests));
        var api = new FakeApi { Result = (uint)result };
        using var reservation = MsiDeployment.Reserve(api);
        Assert.Equal(1, api.Begins);
        var options = new MsiOptions(directory.Root, Path.Combine(directory.Root, "data"), false, true, true);
        int healthChecks = 0, stopped = 0;
        bool Apply() => MsiDeployment.ApplyCore(Path.Combine(directory.Root, "package.msi"), options,
            () => { healthChecks++; return healthy; }, Path.Combine(directory.Root, "install.log"), () => stopped++, reservation);
        if (result == 0) Assert.Equal(healthy, Apply());
        else
        {
            var error = Assert.Throws<MsiUpdateException>(() => Apply());
            Assert.Equal(result, error.NativeErrorCode);
            Assert.Equal("install", error.Operation);
            Assert.Equal(result == 3010 ? "installer-reboot-required" : "installer-failed", error.Reason);
        }
        Assert.Equal(1, api.Begins);
        Assert.Equal(result == 0 ? 1 : 0, healthChecks);
        Assert.Equal(result == 0 && healthy ? 0 : 1, stopped);
        Assert.Equal([result == 0 && healthy], api.Ends);
        Assert.Equal(1, api.Closed);
        Assert.Equal([2u, 5u], api.Ui);
        Assert.True(api.LogDisabled);
        Assert.Contains("INSTALLDESKTOPSHORTCUT=0 INSTALLSTARTMENUSHORTCUT=1 ADD_CLI_TO_PATH=1 REBOOT=ReallySuppress", api.Properties);
    }

    [Fact]
    public void BusyReservationCannotInstallAndFailedCandidateStopStillRollsBack()
    {
        var busy = new FakeApi { BeginResult = 1618 };
        var error = Assert.Throws<MsiUpdateException>(() => MsiDeployment.Reserve(busy));
        Assert.Equal(1618, error.NativeErrorCode);
        Assert.Equal("installer-busy", error.Reason);
        Assert.Null(busy.Properties);
        Assert.Empty(busy.Ends);
        using var directory = new TestScopedDirectory(nameof(MsiDeploymentTests));
        var api = new FakeApi();
        using var reservation = MsiDeployment.Reserve(api);
        var expected = new IOException("candidate stop failed");
        Assert.Same(expected, Assert.Throws<IOException>(() => MsiDeployment.ApplyCore(Path.Combine(directory.Root, "package.msi"),
            new(directory.Root, directory.Root, false, false, false), () => false, Path.Combine(directory.Root, "install.log"), () => throw expected, reservation)));
        Assert.Equal([false], api.Ends);
        Assert.Equal(1, api.Closed);
    }

    private sealed class FakeApi : IMsiApi
    {
        public uint Result, BeginResult;
        public int Begins, Closed;
        public string? Properties;
        public bool LogDisabled;
        public List<bool> Ends { get; } = [];
        public List<uint> Ui { get; } = [];
        public uint Begin(out uint transaction, out nint ownerChanged)
        { Begins++; transaction = BeginResult == 0 ? 1u : 0; ownerChanged = 0; return BeginResult; }
        public uint End(bool commit) { Ends.Add(commit); return 0; }
        public uint Install(string package, string properties) { Properties = properties; return Result; }
        public uint EnableLog(string? path) { if (path == null) LogDisabled = true; return 0; }
        public uint SetUi(uint level) { Ui.Add(level); return 5; }
        public void Close(uint transaction, nint ownerChanged) { if (transaction != 0) Closed++; }
    }
}

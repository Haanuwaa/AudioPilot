using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Tests.Helpers;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Audio;

[Collection("RuntimeTuningConfigIsolation")]
public sealed class DeviceRoleSwitchEngineTests
{
    public DeviceRoleSwitchEngineTests()
    {
        RuntimeTuningConfig.SwitchRetryDelayMs = AppConstants.Timing.SwitchRetryDelayMs;
        RuntimeTuningConfig.SwitchRetryMaxDelayMs = AppConstants.Timing.SwitchRetryMaxDelayMs;
        RuntimeTuningConfig.SwitchMaxRetries = AppConstants.Timing.SwitchMaxRetries;
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_RetriesConfiguredAttempts_WhenVerificationNeverSucceeds()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-retries.log");

        bool success = await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultPlaybackDevice: _ => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_RetriesConfiguredAttempts_WhenVerificationNeverSucceeds),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.Equal(AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries, applyCalls);
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_RetriesConfiguredAttempts_WhenVerificationNeverSucceeds()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-retries.log");

        bool success = await DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            targetDeviceId: "target-id",
            targetName: "Target",
            inputRoles: [NRole.Communications],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultRecordingDevice: _ => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchInputRolesAsync_RetriesConfiguredAttempts_WhenVerificationNeverSucceeds),
            emitVerifyRetryWarning: false,
            traceComRetry: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.InRange(applyCalls, 1, AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_ThrowsOnFinalAttempt_WhenComExceptionPersists()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-com-exception.log");

        await Assert.ThrowsAsync<COMException>(() => DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) =>
            {
                applyCalls++;
                throw new COMException("simulated", unchecked((int)0x80004005));
            },
            getDefaultPlaybackDevice: _ => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_ThrowsOnFinalAttempt_WhenComExceptionPersists),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries, applyCalls);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_ThrowsOnFinalAttempt_WhenVerificationThrows()
    {
        int applyCalls = 0;
        int readCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-verify-exception.log");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (deviceId, _) =>
            {
                if (deviceId == "target-id") applyCalls++;
            },
            getDefaultPlaybackDevice: _ => ++readCalls == 1
                ? "original-id"
                : throw new InvalidOperationException("verify failed"),
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_ThrowsOnFinalAttempt_WhenVerificationThrows),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.InRange(applyCalls, 1, AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_WhenCanceledDuringRetryDelay_StopsAfterCurrentAttempt()
    {
        RuntimeTuningConfig.SwitchRetryDelayMs = 500;

        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-cancel.log");
        using var cancellationTokenSource = new CancellationTokenSource();

        Exception? exception = await Record.ExceptionAsync(() => DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) =>
            {
                applyCalls++;
                cancellationTokenSource.Cancel();
            },
            getDefaultPlaybackDevice: _ => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_WhenCanceledDuringRetryDelay_StopsAfterCurrentAttempt),
            cancellationToken: cancellationTokenSource.Token));

        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(1, applyCalls);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_DoesNotWaitForSharedCoreAudioWorker()
    {
        RuntimeTuningConfig.SwitchMaxRetries = 1;
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;

        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-output-worker-starvation.log");
        using var blockerStarted = new ManualResetEventSlim(false);
        using var releaseBlocker = new ManualResetEventSlim(false);

        Task blockerTask = Task.Run(() => ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            blockerStarted.Set();
            Assert.True(releaseBlocker.Wait(TimeSpan.FromSeconds(5), testCancellationToken));
        }, testCancellationToken), testCancellationToken);

        Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5), testCancellationToken));

        int applyCalls = 0;
        bool success = await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            targetDeviceId: "target-id",
            outputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) => applyCalls++,
            getDefaultPlaybackDevice: _ => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchOutputRolesAsync_DoesNotWaitForSharedCoreAudioWorker),
            cancellationToken: testCancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), testCancellationToken);

        Assert.False(success);
        Assert.Equal(1, applyCalls);

        releaseBlocker.Set();
        await blockerTask.WaitAsync(TimeSpan.FromSeconds(5), testCancellationToken);
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_ThrowsOnFinalAttempt_WhenComExceptionPersists()
    {
        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-com-exception.log");

        await Assert.ThrowsAsync<COMException>(() => DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            targetDeviceId: "target-id",
            targetName: "Target",
            inputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (_, _) =>
            {
                applyCalls++;
                throw new COMException("simulated", unchecked((int)0x80004005));
            },
            getDefaultRecordingDevice: _ => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchInputRolesAsync_ThrowsOnFinalAttempt_WhenComExceptionPersists),
            emitVerifyRetryWarning: false,
            traceComRetry: true,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries, applyCalls);
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_ThrowsOnFinalAttempt_WhenVerificationThrows()
    {
        int applyCalls = 0;
        int readCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-verify-exception.log");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            targetDeviceId: "target-id",
            targetName: "Target",
            inputRoles: [NRole.Multimedia],
            applyConfiguredRoles: (deviceId, _) =>
            {
                if (deviceId == "target-id") applyCalls++;
            },
            getDefaultRecordingDevice: _ => ++readCalls == 1
                ? "original-id"
                : throw new InvalidOperationException("verify failed"),
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchInputRolesAsync_ThrowsOnFinalAttempt_WhenVerificationThrows),
            emitVerifyRetryWarning: true,
            traceComRetry: false,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.InRange(applyCalls, 1, AudioPilot.Constants.AppConstants.Timing.SwitchMaxRetries);
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_WhenCanceledDuringRetryDelay_StopsAfterCurrentAttempt()
    {
        RuntimeTuningConfig.SwitchRetryDelayMs = 500;

        int applyCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-cancel.log");
        using var cancellationTokenSource = new CancellationTokenSource();

        Exception? exception = await Record.ExceptionAsync(() => DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            targetDeviceId: "target-id",
            targetName: "Target",
            inputRoles: [NRole.Communications],
            applyConfiguredRoles: (_, _) =>
            {
                applyCalls++;
                cancellationTokenSource.Cancel();
            },
            getDefaultRecordingDevice: _ => null,
            logger: loggerScope.Logger,
            opId: "testop",
            contextMethod: nameof(TrySwitchInputRolesAsync_WhenCanceledDuringRetryDelay_StopsAfterCurrentAttempt),
            emitVerifyRetryWarning: false,
            traceComRetry: false,
            cancellationToken: cancellationTokenSource.Token));

        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(1, applyCalls);
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_VerifiesEveryRole()
    {
        RuntimeTuningConfig.SwitchMaxRetries = 1;
        var assignments = new Dictionary<NRole, string>
        {
            [NRole.Console] = "old-console",
            [NRole.Multimedia] = "old-multimedia",
            [NRole.Communications] = "old-communications",
        };
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-all-roles.log");

        bool success = await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            "target-id",
            [NRole.Console, NRole.Multimedia, NRole.Communications],
            (deviceId, role) => assignments[role] = deviceId,
            role => assignments[role],
            loggerScope.Logger,
            "testop",
            nameof(TrySwitchOutputRolesAsync_VerifiesEveryRole),
            TestContext.Current.CancellationToken);

        Assert.True(success);
        Assert.All(assignments.Values, deviceId => Assert.Equal("target-id", deviceId));
    }

    [Fact]
    public async Task TrySwitchInputRolesAsync_AppliesEveryRole_WhenOneRoleAlreadyTargetsDevice()
    {
        RuntimeTuningConfig.SwitchMaxRetries = 1;
        var assignments = new Dictionary<NRole, string>
        {
            [NRole.Console] = "target-id",
            [NRole.Multimedia] = "old-multimedia",
            [NRole.Communications] = "old-communications",
        };
        var appliedRoles = new List<NRole>();
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-input-diverged-roles.log");

        bool success = await DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(
            "target-id",
            "Target",
            [NRole.Console, NRole.Multimedia, NRole.Communications],
            (deviceId, role) =>
            {
                appliedRoles.Add(role);
                assignments[role] = deviceId;
            },
            role => assignments[role],
            loggerScope.Logger,
            "testop",
            nameof(TrySwitchInputRolesAsync_AppliesEveryRole_WhenOneRoleAlreadyTargetsDevice),
            emitVerifyRetryWarning: false,
            traceComRetry: false,
            TestContext.Current.CancellationToken);

        Assert.True(success);
        Assert.Equal([NRole.Console, NRole.Multimedia, NRole.Communications], appliedRoles);
        Assert.All(assignments.Values, deviceId => Assert.Equal("target-id", deviceId));
    }

    [Fact]
    public async Task TrySwitchOutputRolesAsync_RollsBackEveryRole_WhenOneRoleCannotBeApplied()
    {
        RuntimeTuningConfig.SwitchMaxRetries = 1;
        var assignments = new Dictionary<NRole, string>
        {
            [NRole.Console] = "old-console",
            [NRole.Multimedia] = "old-multimedia",
        };
        using var loggerScope = new TestLoggerScope(nameof(DeviceRoleSwitchEngineTests), "switch-engine-role-rollback.log");

        bool success = await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(
            "target-id",
            [NRole.Console, NRole.Multimedia],
            (deviceId, role) =>
            {
                if (role != NRole.Multimedia || deviceId != "target-id") assignments[role] = deviceId;
            },
            role => assignments[role],
            loggerScope.Logger,
            "testop",
            nameof(TrySwitchOutputRolesAsync_RollsBackEveryRole_WhenOneRoleCannotBeApplied),
            TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.Equal("old-console", assignments[NRole.Console]);
        Assert.Equal("old-multimedia", assignments[NRole.Multimedia]);
    }
}


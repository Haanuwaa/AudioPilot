using System.Collections.Concurrent;
using System.Reflection;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Bluetooth;

public sealed class BluetoothReconnectCoordinatorTests
{
    [Fact]
    public async Task TryReconnectAsync_WhenDisabled_ReturnsFalseWithoutCallingService()
    {
        var fakeService = new FakeBluetoothReconnectService();
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        bool result = await coordinator.TryReconnectAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(false, 1, 200, 1000, true),
            opId: "test",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(0, fakeService.Calls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_WhenReconnectCalledButNotConnected_ReportsAttempted()
    {
        var fakeService = new FakeBluetoothReconnectService { NextResult = false };
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 200, 1000, false),
            opId: "test",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.False(result.Connected);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(0, result.CooldownSkips);
        Assert.Equal(1, fakeService.Calls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_WhenDisabled_ReportsNotAttempted()
    {
        var fakeService = new FakeBluetoothReconnectService();
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(false, 1, 200, 1000, true),
            opId: "test",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Attempted);
        Assert.False(result.Connected);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(0, result.CooldownSkips);
        Assert.Equal(0, fakeService.Calls);
    }

    [Fact]
    public async Task TryReconnectAsync_RespectsCooldown()
    {
        var fakeService = new FakeBluetoothReconnectService { NextResult = true };
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);
        var options = new BluetoothReconnectOptions(true, 1, 200, 5000, false);
        var configured = new List<CycleDevice> { new() { Id = "id-1", Name = "Bluetooth Headset" } };

        bool first = await coordinator.TryReconnectAsync(configured, [], BluetoothReconnectDeviceKind.Output, options, opId: "test-1", cancellationToken: TestContext.Current.CancellationToken);
        bool second = await coordinator.TryReconnectAsync(configured, [], BluetoothReconnectDeviceKind.Output, options, opId: "test-2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, fakeService.Calls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_CooldownStateDoesNotBlockOtherDevices()
    {
        var fakeService = new FakeBluetoothReconnectService { NextResult = true };
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);
        var options = new BluetoothReconnectOptions(true, 2, 200, 5000, false);

        await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "cooldown-seed",
            cancellationToken: TestContext.Current.CancellationToken);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [
                new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" },
                new CycleDevice { Id = "id-2", Name = "Galaxy Buds" },
            ],
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "cooldown-isolation",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.True(result.Connected);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, result.CooldownSkips);
        Assert.Equal(2, fakeService.Calls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_OpensTimeoutCircuitAfterConsecutiveTimeouts()
    {
        var fakeService = new FakeBluetoothReconnectService();
        fakeService.EnqueueException(new OperationCanceledException());
        fakeService.EnqueueException(new OperationCanceledException());

        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);
        var options = new BluetoothReconnectOptions(true, 1, 200, 0, false);
        var configured = new List<CycleDevice> { new() { Id = "id-1", Name = "Bluetooth Headset" } };

        BluetoothReconnectAttemptResult first = await coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "t1",
            cancellationToken: TestContext.Current.CancellationToken);

        BluetoothReconnectAttemptResult second = await coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "t2",
            cancellationToken: TestContext.Current.CancellationToken);

        BluetoothReconnectAttemptResult third = await coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "t3",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.Attempted);
        Assert.True(second.Attempted);
        Assert.False(third.Attempted);
        Assert.Equal(2, fakeService.Calls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_TimeoutCircuitStateDoesNotBlockOtherDevices()
    {
        var fakeService = new FakeBluetoothReconnectService();
        fakeService.EnqueueException(new OperationCanceledException());
        fakeService.EnqueueException(new OperationCanceledException());
        fakeService.EnqueueResult(true);

        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);
        var options = new BluetoothReconnectOptions(true, 2, 200, 0, false);

        await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "timeout-seed-1",
            cancellationToken: TestContext.Current.CancellationToken);

        await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "timeout-seed-2",
            cancellationToken: TestContext.Current.CancellationToken);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [
                new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" },
                new CycleDevice { Id = "id-2", Name = "Galaxy Buds" },
            ],
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "timeout-isolation",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.True(result.Connected);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(3, fakeService.Calls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_SuccessResetsTimeoutStreak()
    {
        var fakeService = new FakeBluetoothReconnectService();
        fakeService.EnqueueException(new OperationCanceledException());
        fakeService.EnqueueResult(true);
        fakeService.EnqueueException(new OperationCanceledException());

        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);
        var options = new BluetoothReconnectOptions(true, 1, 200, 0, false);
        var configured = new List<CycleDevice> { new() { Id = "id-1", Name = "Bluetooth Headset" } };

        BluetoothReconnectAttemptResult first = await coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "r1",
            cancellationToken: TestContext.Current.CancellationToken);

        BluetoothReconnectAttemptResult second = await coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "r2",
            cancellationToken: TestContext.Current.CancellationToken);

        object timeoutMapAfterSuccess = GetTimeoutCircuitMap(coordinator);
        Assert.False(ContainsTimeoutCircuitEntry(timeoutMapAfterSuccess, "id-1"));

        BluetoothReconnectAttemptResult third = await coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "r3",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.Attempted);
        Assert.True(second.Connected);
        Assert.True(third.Attempted);
        Assert.False(third.Connected);
        Assert.Equal(3, fakeService.Calls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_PrunesStaleReconnectState()
    {
        var fakeService = new FakeBluetoothReconnectService();
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        var lastAttemptMap = GetLastAttemptMap(coordinator);
        object timeoutMap = GetTimeoutCircuitMap(coordinator);

        string staleId = "stale-id";
        DateTime staleUtc = DateTime.UtcNow.AddMinutes(-(AppConstants.Timing.RetryStateTtlMinutes + 5));
        lastAttemptMap[staleId] = staleUtc;

        Type timeoutStateType = typeof(BluetoothReconnectCoordinator)
            .GetNestedType("TimeoutCircuitState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TimeoutCircuitState not found");
        object staleTimeoutState = Activator.CreateInstance(timeoutStateType)
            ?? throw new InvalidOperationException("Could not create timeout state");
        timeoutStateType.GetProperty("ConsecutiveTimeouts")?.SetValue(staleTimeoutState, 0);
        timeoutStateType.GetProperty("OpenUntilUtc")?.SetValue(staleTimeoutState, DateTime.MinValue);
        SetTimeoutCircuitEntry(timeoutMap, staleId, staleTimeoutState);

        var configured = new List<CycleDevice> { new() { Id = "id-1", Name = "Bluetooth Headset" } };
        var options = new BluetoothReconnectOptions(false, 1, 200, 0, true);

        for (int i = 0; i < 32; i++)
        {
            await coordinator.TryReconnectDetailedAsync(
                configured,
                [],
                BluetoothReconnectDeviceKind.Output,
                options,
                opId: $"prune-{i}",
                cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.False(lastAttemptMap.ContainsKey(staleId));
        Assert.False(ContainsTimeoutCircuitEntry(timeoutMap, staleId));
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_TimeoutThenKsFallbackSuccess_ReturnsConnected()
    {
        var fakeService = new FakeBluetoothReconnectService { NextFallbackResult = true };
        fakeService.EnqueueException(new OperationCanceledException());
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 200, 1000, false),
            opId: "test",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.True(result.Connected);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, fakeService.Calls);
        Assert.Equal(1, fakeService.FallbackCalls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_PairNotConnectedThenKsFallbackSuccess_ReturnsConnected()
    {
        var fakeService = new FakeBluetoothReconnectService { NextFallbackResult = true };
        fakeService.EnqueueResult(false);
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 200, 1000, false),
            opId: "pair-false-fallback-success",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.True(result.Connected);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, fakeService.Calls);
        Assert.Equal(1, fakeService.FallbackCalls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_StrictLikelyFilter_SkipsGenericSingleDevice()
    {
        var fakeService = new FakeBluetoothReconnectService();
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 200, 0, true),
            opId: "fallback-preferred-timeout",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Attempted);
        Assert.False(result.Connected);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(0, fakeService.Calls);
        Assert.Equal(0, fakeService.FallbackCalls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_LikelyBluetoothTimeout_StillFallsBackAfterPairTimeout()
    {
        var fakeService = new FakeBluetoothReconnectService { NextFallbackResult = true };
        fakeService.EnqueueException(new OperationCanceledException());
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 200, 0, true),
            opId: "likely-bluetooth-timeout",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.True(result.Connected);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, fakeService.Calls);
        Assert.Equal(1, fakeService.FallbackCalls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_RelaxedSingleCandidate_SkipsNeutralSingleDevice()
    {
        var fakeService = new FakeBluetoothReconnectService();
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Momentum 4" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 200, 0, true),
            opId: "relaxed-single-neutral",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Attempted);
        Assert.False(result.Connected);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(0, fakeService.Calls);
        Assert.Equal(0, fakeService.FallbackCalls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_RelaxedSingleCandidate_SkipsStrongNegativeSignal()
    {
        var fakeService = new FakeBluetoothReconnectService();
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "USB Speaker" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 200, 0, true),
            opId: "relaxed-single-negative",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Attempted);
        Assert.False(result.Connected);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(0, fakeService.Calls);
        Assert.Equal(0, fakeService.FallbackCalls);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_TimeoutFallback_UsesAttemptTimeoutBudget()
    {
        var fakeService = new FakeBluetoothReconnectService();
        fakeService.EnqueueAsync(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        });
        fakeService.EnqueueFallbackAsync(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        });

        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 400, 0, false),
            opId: "fallback-timeout-budget",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.False(result.Connected);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, fakeService.Calls);
        Assert.Equal(1, fakeService.FallbackCalls);
        Assert.False(fakeService.LastFallbackTokenWasCanceledAtInvocation);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_TimeoutFallback_LogsRemainingFallbackBudget()
    {
        using var loggerScope = new TestLoggerScope(nameof(TryReconnectDetailedAsync_TimeoutFallback_LogsRemainingFallbackBudget), "bluetooth-reconnect-fallback-budget.log", LogLevel.Warning);
        var fakeService = new FakeBluetoothReconnectService();
        double elapsedMilliseconds = 0;
        fakeService.EnqueueAsync(cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            elapsedMilliseconds = 70;
            return Task.FromResult(false);
        });
        fakeService.EnqueueFallbackAsync(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        });

        var coordinator = new BluetoothReconnectCoordinator(
            fakeService,
            loggerScope.Logger,
            () => () => elapsedMilliseconds);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 120, 0, false),
            opId: "fallback-budget-log",
            cancellationToken: TestContext.Current.CancellationToken);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.True(result.Attempted);
        Assert.False(result.Connected);
        Assert.Contains("fullBudgetMs=120", logText, StringComparison.Ordinal);
        Assert.Contains("remainingBudgetMs=50", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_PairTimeout_RecordsPairExecutionTimeInPhaseLog()
    {
        using var loggerScope = new TestLoggerScope(nameof(TryReconnectDetailedAsync_PairTimeout_RecordsPairExecutionTimeInPhaseLog), "bluetooth-reconnect-pair-timeout-phases.log", LogLevel.Debug);
        var fakeService = new FakeBluetoothReconnectService();
        fakeService.EnqueueAsync(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        });
        fakeService.EnqueueFallbackAsync(async cancellationToken =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        });

        var coordinator = new BluetoothReconnectCoordinator(fakeService, loggerScope.Logger);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 120, 0, false),
            opId: "pair-timeout-phases",
            cancellationToken: TestContext.Current.CancellationToken);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.True(result.Attempted);
        Assert.False(result.Connected);
        Assert.Contains("result=timeout", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("pairMs=0.0", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_PairAndFallbackTimeout_LogsRemainingBudgetInTopLevelTimeoutWarning()
    {
        using var loggerScope = new TestLoggerScope(nameof(TryReconnectDetailedAsync_PairAndFallbackTimeout_LogsRemainingBudgetInTopLevelTimeoutWarning), "bluetooth-reconnect-timeout-warning-budget.log", LogLevel.Warning);
        var fakeService = new FakeBluetoothReconnectService();
        fakeService.EnqueueAsync(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        });
        fakeService.EnqueueFallbackAsync(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        });

        var coordinator = new BluetoothReconnectCoordinator(fakeService, loggerScope.Logger);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }],
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 1, 120, 0, false),
            opId: "pair-and-fallback-timeout-budget",
            cancellationToken: TestContext.Current.CancellationToken);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.True(result.Attempted);
        Assert.False(result.Connected);
        Assert.Contains("timeoutMs=120", logText, StringComparison.Ordinal);
        Assert.Contains("fullBudgetMs=120", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("remainingBudgetMs=120", logText, StringComparison.Ordinal);
        Assert.Matches("remainingBudgetMs=([0-9]{1,2}|1[01][0-9])", logText);
    }

    [Theory]
    [InlineData(1200, 950)]
    [InlineData(250, 125)]
    [InlineData(80, 40)]
    [InlineData(1, 1)]
    public void ResolvePairPhaseBudgetMs_ReservesFallbackBudget(int attemptTimeoutMs, int expectedPairBudgetMs)
    {
        int pairBudgetMs = BluetoothReconnectCoordinator.ResolvePairPhaseBudgetMs(attemptTimeoutMs);

        Assert.Equal(expectedPairBudgetMs, pairBudgetMs);
    }

    [Theory]
    [InlineData(1200, 0, 1200)]
    [InlineData(1200, 249.1, 950)]
    [InlineData(1200, 950.0, 250)]
    [InlineData(1200, 1199.1, 0)]
    [InlineData(120, 121.0, 0)]
    public void ResolveFallbackRemainingBudgetMs_ReturnsExpectedRemainingBudget(int attemptTimeoutMs, double elapsedAttemptMs, int expectedRemainingBudgetMs)
    {
        int remainingBudgetMs = BluetoothReconnectCoordinator.ResolveFallbackRemainingBudgetMs(attemptTimeoutMs, elapsedAttemptMs);

        Assert.Equal(expectedRemainingBudgetMs, remainingBudgetMs);
    }

    [Theory]
    [InlineData(400, 401.0, 200, 200)]
    [InlineData(120, 121.0, 60, 60)]
    [InlineData(120, 35.2, 60, 84)]
    public void ResolveFallbackRemainingBudgetMs_WithElapsedCap_PreservesReservedFallbackWindow(int attemptTimeoutMs, double elapsedAttemptMs, int elapsedBudgetCapMs, int expectedRemainingBudgetMs)
    {
        int remainingBudgetMs = BluetoothReconnectCoordinator.ResolveFallbackRemainingBudgetMs(attemptTimeoutMs, elapsedAttemptMs, elapsedBudgetCapMs);

        Assert.Equal(expectedRemainingBudgetMs, remainingBudgetMs);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_ReportsAttemptProgress_ForEachAttempt()
    {
        var fakeService = new FakeBluetoothReconnectService();
        fakeService.EnqueueException(new InvalidOperationException("transient"));
        fakeService.EnqueueResult(true);

        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);
        var configured = new List<CycleDevice> { new() { Id = "id-1", Name = "Bluetooth Headset" } };
        var progressCalls = new List<(int Attempt, int MaxAttempts, string DeviceName)>();

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            new BluetoothReconnectOptions(true, 2, 200, 0, false),
            opId: "progress",
            onAttemptProgress: (attempt, maxAttempts, deviceName) => progressCalls.Add((attempt, maxAttempts, deviceName)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.True(result.Connected);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, progressCalls.Count);
        Assert.Equal((1, 2, "Bluetooth Headset"), progressCalls[0]);
        Assert.Equal((2, 2, "Bluetooth Headset"), progressCalls[1]);
    }

    [Theory]
    [InlineData("id", "Bluetooth Headset", true)]
    [InlineData("id", "USB DAC", false)]
    [InlineData("id", "Headset", false)]
    [InlineData("BTHENUM\\DEV_123", "Headset", false)]
    [InlineData("BTHENUM\\DEV_123", "Headphones", false)]
    [InlineData("id", "Galaxy Buds", true)]
    [InlineData("BTHENUM\\DEV_123", "Studio Output", false)]
    [InlineData("id", "WF-C700N", true)]
    [InlineData("id", "Virtual Bluetooth Cable", false)]
    [InlineData("id", "Headphones (Soundcore Space A40)", true)]
    [InlineData("id", "Headset (Soundcore Space A40)", true)]
    [InlineData("id", "Headphones (USB Audio Device)", false)]
    [InlineData("BTHENUM\\DEV_123", "Bluetooth Audio", true)]
    [InlineData("BTHENUM\\DEV_123", "USB Bluetooth Headset Stereo", false)]
    public void IsLikelyBluetoothEndpoint_UsesDeviceHeuristics(string id, string name, bool expected)
    {
        bool result = BluetoothReconnectCoordinator.IsLikelyBluetoothEndpoint(new CycleDevice { Id = id, Name = name });

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_InputReconnect_UsesInputKind()
    {
        var fakeService = new FakeBluetoothReconnectService { NextResult = true };
        var coordinator = new BluetoothReconnectCoordinator(fakeService, Logger.Instance);

        BluetoothReconnectAttemptResult result = await coordinator.TryReconnectDetailedAsync(
            [new CycleDevice { Id = "input-1", Name = "Bluetooth Microphone" }],
            [],
            BluetoothReconnectDeviceKind.Input,
            new BluetoothReconnectOptions(true, 1, 200, 0, true),
            opId: "input-reconnect",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Attempted);
        Assert.True(result.Connected);
        Assert.Equal(["input"], fakeService.Kinds);
    }

    [Fact]
    public void FromSettings_UsesRuntimeConfigValues()
    {
        BluetoothReconnectRuntimeConfig.MaxAttempts = 3;
        BluetoothReconnectRuntimeConfig.AttemptTimeoutMs = 1750;
        BluetoothReconnectRuntimeConfig.CooldownMs = 900;

        var settings = new Settings
        {
            Miscellaneous = new MiscellaneousSettings
            {
                BluetoothReconnectEnabled = true
            }
        };

        BluetoothReconnectOptions options = BluetoothReconnectOptions.FromSettings(settings);

        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(1750, options.AttemptTimeoutMs);
        Assert.Equal(900, options.CooldownMs);
    }

    [Fact]
    public async Task TryReconnectDetailedAsync_ConcurrentSameDeviceAttempts_DoNotOverlap()
    {
        var service = new ConcurrentTrackingBluetoothReconnectService();
        var coordinator = new BluetoothReconnectCoordinator(service, Logger.Instance);
        CycleDevice[] configured = [new CycleDevice { Id = "id-1", Name = "Bluetooth Headset" }];
        var options = new BluetoothReconnectOptions(true, 1, 1000, 0, false);

        Task<BluetoothReconnectAttemptResult> first = coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "first",
            cancellationToken: TestContext.Current.CancellationToken);
        await service.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Task<BluetoothReconnectAttemptResult> second = coordinator.TryReconnectDetailedAsync(
            configured,
            [],
            BluetoothReconnectDeviceKind.Output,
            options,
            opId: "second",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, service.MaxConcurrentCalls);
        service.ReleaseFirstAttempt.TrySetResult();

        BluetoothReconnectAttemptResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Connected));
        Assert.Equal(2, service.Calls);
        Assert.Equal(1, service.MaxConcurrentCalls);
    }

    private sealed class ConcurrentTrackingBluetoothReconnectService : IBluetoothReconnectService
    {
        private int _activeCalls;
        private int _calls;
        private int _maxConcurrentCalls;

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public Task<bool> TryReconnectPairedAudioDeviceAsync(
            string deviceName,
            CancellationToken cancellationToken)
        {
            return TryReconnectCoreAsync(cancellationToken);
        }

        public Task<bool> TryReconnectPairedAudioDeviceAsync(
            string deviceName,
            string opId,
            string kind,
            CancellationToken cancellationToken)
        {
            return TryReconnectCoreAsync(cancellationToken);
        }

        public Task<bool> TryReconnectUsingAudioEndpointControlAsync(
            string deviceName,
            string opId,
            string kind,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        private async Task<bool> TryReconnectCoreAsync(CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            int activeCalls = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(ref _maxConcurrentCalls, activeCalls);
            try
            {
                if (call == 1)
                {
                    FirstAttemptStarted.TrySetResult();
                    await ReleaseFirstAttempt.Task.WaitAsync(cancellationToken);
                }

                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            int current = Volatile.Read(ref target);
            while (value > current)
            {
                int observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private static ConcurrentDictionary<string, DateTime> GetLastAttemptMap(BluetoothReconnectCoordinator coordinator)
    {
        return (ConcurrentDictionary<string, DateTime>)(typeof(BluetoothReconnectCoordinator)
            .GetField("_lastAttemptByDeviceIdUtc", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(coordinator)
            ?? throw new InvalidOperationException("_lastAttemptByDeviceIdUtc not found"));
    }

    private static object GetTimeoutCircuitMap(BluetoothReconnectCoordinator coordinator)
    {
        return typeof(BluetoothReconnectCoordinator)
            .GetField("_timeoutCircuitByDeviceId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(coordinator)
            ?? throw new InvalidOperationException("_timeoutCircuitByDeviceId not found");
    }

    private static void SetTimeoutCircuitEntry(object timeoutMap, string id, object value)
    {
        PropertyInfo? indexer = timeoutMap.GetType().GetProperty("Item");
        indexer?.SetValue(timeoutMap, value, [id]);
    }

    private static bool ContainsTimeoutCircuitEntry(object timeoutMap, string id)
    {
        MethodInfo containsKey = timeoutMap.GetType().GetMethod("ContainsKey")
            ?? throw new InvalidOperationException("ContainsKey not found");
        return (bool)(containsKey.Invoke(timeoutMap, [id]) ?? false);
    }
}

using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceNotificationRegistrationHelperTests
{
    [Fact]
    public void Register_OwnsSubscription_AndRunsPostRegistrationAction()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-register.log", LogLevel.Debug);
        int createCalls = 0;
        int onRegisteredCalls = 0;
        var subscription = new TestSubscription();
        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            () =>
            {
                createCalls++;
                return subscription;
            },
            () => onRegisteredCalls++,
            static () => { });

        helper.Register();

        Assert.True(helper.IsRegistered);
        Assert.Equal(1, createCalls);
        Assert.Equal(1, onRegisteredCalls);
        Assert.Equal(0, subscription.DisposeCount);
    }

    [Fact]
    public void Unregister_DisposesSubscription_AndRunsPostUnregistrationAction()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-unregister.log", LogLevel.Debug);
        int onUnregisteredCalls = 0;
        var subscription = new TestSubscription();
        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            () => subscription,
            static () => { },
            () => onUnregisteredCalls++);
        helper.Register();

        helper.Unregister();

        Assert.False(helper.IsRegistered);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.Equal(1, onUnregisteredCalls);
    }

    [Fact]
    public void Register_DoesNothing_WhenAlreadyRegistered()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-already-registered.log", LogLevel.Debug);
        int createCalls = 0;
        int onRegisteredCalls = 0;
        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            () =>
            {
                createCalls++;
                return new TestSubscription();
            },
            () => onRegisteredCalls++,
            static () => { });

        helper.Register();
        helper.Register();

        Assert.True(helper.IsRegistered);
        Assert.Equal(1, createCalls);
        Assert.Equal(1, onRegisteredCalls);
    }

    [Fact]
    public void Register_LeavesStateUnchanged_WhenSubscriptionCreationThrows()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-register-throws.log", LogLevel.Debug);
        int onRegisteredCalls = 0;
        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            static () => throw new InvalidOperationException("boom"),
            () => onRegisteredCalls++,
            static () => { });

        helper.Register();

        Assert.False(helper.IsRegistered);
        Assert.Equal(0, onRegisteredCalls);
    }

    [Fact]
    public void Unregister_DoesNothing_WhenAlreadyUnregistered()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-already-unregistered.log", LogLevel.Debug);
        int onUnregisteredCalls = 0;
        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            static () => new TestSubscription(),
            static () => { },
            () => onUnregisteredCalls++);

        helper.Unregister();

        Assert.False(helper.IsRegistered);
        Assert.Equal(0, onUnregisteredCalls);
    }

    [Fact]
    public void Unregister_ClearsStateAndStopsMonitoring_WhenSubscriptionDisposeThrows()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-unregister-throws.log", LogLevel.Debug);
        int onUnregisteredCalls = 0;
        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            static () => new TestSubscription(throwOnDispose: true),
            static () => { },
            () => onUnregisteredCalls++);
        helper.Register();

        helper.Unregister();

        Assert.False(helper.IsRegistered);
        Assert.Equal(1, onUnregisteredCalls);
    }

    [Fact]
    public async Task Unregister_CancelsInFlightRegistration_WithoutPublishingSubscription()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceNotificationRegistrationHelperTests), "notification-helper-register-race.log", LogLevel.Debug);
        using var creationStarted = new ManualResetEventSlim(false);
        using var allowCreationToFinish = new ManualResetEventSlim(false);
        var subscription = new TestSubscription();
        int onRegisteredCalls = 0;
        int onUnregisteredCalls = 0;
        var helper = new AudioDeviceNotificationRegistrationHelper(
            loggerScope.Logger,
            () =>
            {
                creationStarted.Set();
                Assert.True(allowCreationToFinish.Wait(TimeSpan.FromSeconds(5)));
                return subscription;
            },
            () => onRegisteredCalls++,
            () => onUnregisteredCalls++);

        Task registerTask = Task.Run(helper.Register, TestContext.Current.CancellationToken);
        Assert.True(creationStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task unregisterTask = Task.Run(helper.Unregister, TestContext.Current.CancellationToken);
        await TestExecutionGuards.WaitUntilAsync(
            () => helper.IsUnregisteringForTests,
            "Unregister did not claim the in-flight registration before creation was released.");
        allowCreationToFinish.Set();

        await Task.WhenAll(registerTask, unregisterTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(helper.IsRegistered);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.Equal(0, onRegisteredCalls);
        Assert.Equal(0, onUnregisteredCalls);
    }

    private sealed class TestSubscription(bool throwOnDispose = false) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }
}

using System.Reflection;
using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

public sealed class TestExecutionGuardsTests
{
    [Fact]
    public void VisibleDesktopTests_RequireDiscoveryTimeOptIn()
    {
        static bool HasVisualTrait(MemberInfo member) => member.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments.Count == 2
            && Equals(attribute.ConstructorArguments[0].Value, TestCategories.Name)
            && Equals(attribute.ConstructorArguments[1].Value, TestCategories.VisualWpf));

        MethodInfo[] tests = [.. typeof(TestExecutionGuardsTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => (HasVisualTrait(type) || HasVisualTrait(method)) && method.IsDefined(typeof(FactAttribute))))];

        Assert.NotEmpty(tests);
        Assert.All(tests, method => Assert.True(
            method.IsDefined(typeof(VisualIntegrationFactAttribute)) || method.IsDefined(typeof(VisualIntegrationTheoryAttribute)),
            $"{method.DeclaringType!.Name}.{method.Name} must require explicit desktop interaction before execution."));
    }

    [Theory]
    [InlineData(typeof(Services.Audio.AudioDeviceIntegrationTests))]
    [InlineData(typeof(Services.Audio.AudioDeviceSessionLifecycleIntegrationTests))]
    [InlineData(typeof(Services.Audio.AudioEndpointTestHardwareTests))]
    public void RealAudioTests_RequireDiscoveryTimeOptIn(Type testClass)
    {
        MethodInfo[] tests = [.. testClass.GetMethods().Where(method => method.IsDefined(typeof(FactAttribute)))];
        Assert.NotEmpty(tests);
        Assert.All(tests, method => Assert.True(
            method.IsDefined(typeof(AudioHardwareFactAttribute)) || method.IsDefined(typeof(HardwareSoakFactAttribute)),
            $"{testClass.Name}.{method.Name} must require explicit hardware audio permission."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunIsolatedSta_ShutsDownDispatcherOnItsOwnerThreadEvenWhenActionFails(bool actionFails)
    {
        Dispatcher? dispatcher = null;
        bool shutdownOnOwnerThread = false;
        void Run() => TestExecutionGuards.RunIsolatedSta(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.ShutdownFinished += (_, _) => shutdownOnOwnerThread = dispatcher.CheckAccess();
            if (actionFails)
                throw new InvalidOperationException("test action failed");
        });

        if (actionFails)
            Assert.Equal("test action failed", Assert.Throws<InvalidOperationException>(Run).Message);
        else
            Run();

        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished);
        Assert.True(shutdownOnOwnerThread);
    }

    [Fact]
    public async Task RunIsolatedSta_LateCompletionAfterTimeoutDoesNotCrashTheTestHost()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Throws<TimeoutException>(() => TestExecutionGuards.RunIsolatedSta(() =>
            {
                workerStarted.TrySetResult(Thread.CurrentThread);
                release.Task.GetAwaiter().GetResult();
            }, TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            release.TrySetResult();
        }

        Thread worker = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(worker.Join(TimeSpan.FromSeconds(3)));
    }
}

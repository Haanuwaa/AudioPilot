using System.Reflection;
using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

public sealed class TestExecutionGuardsTests
{
    [Fact]
    public async Task AssertDoesNotCompleteWithin_PropagatesCancellationInsteadOfPassing()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestExecutionGuards.AssertDoesNotCompleteWithinAsync(
            pending.Task, TimeSpan.FromSeconds(10), "The operation should remain pending.", canceled.Token));
    }

    [Fact]
    public void AudioEndpointPrerequisite_AllowsAvailableHardware()
    {
        bool probed = false;
        Assert.True(TestExecutionGuards.RequireAudioEndpointPrerequisite("test", () => probed = true, requireHardware: true));
        Assert.True(probed);
    }

    [Fact]
    public void AudioEndpointPrerequisite_FailsMissingRequiredHardware()
    {
        var missing = new NAudio.CoreAudioApi.CoreAudioException(unchecked((int)0x80070490));
        var failure = Assert.Throws<InvalidOperationException>(() =>
            TestExecutionGuards.RequireAudioEndpointPrerequisite("test", () => throw missing, requireHardware: true));
        Assert.Same(missing, failure.InnerException);
        Assert.Contains("Strict hardware mode", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioEndpointPrerequisite_DoesNotHideOtherFailures(bool nativeFailure)
    {
        Exception failure = nativeFailure
            ? new NAudio.CoreAudioApi.CoreAudioException(unchecked((int)0x80070005))
            : new InvalidOperationException("unexpected probe failure");
        Assert.Same(failure, Assert.ThrowsAny<Exception>(() =>
            TestExecutionGuards.RequireAudioEndpointPrerequisite("test", () => throw failure, requireHardware: false)));
    }

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

    [Fact]
    public void RealAudioTests_RequireDiscoveryTimeOptIn()
    {
        static bool HasIntegrationTrait(MemberInfo member) => member.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments.Count == 2
            && Equals(attribute.ConstructorArguments[0].Value, TestCategories.Name)
            && Equals(attribute.ConstructorArguments[1].Value, TestCategories.Integration));

        static bool IsAudioIntegrationType(Type type) =>
            (type.Namespace is { } ns
                && (string.Equals(ns, "AudioPilot.Tests.Services.Audio", StringComparison.Ordinal)
                    || ns.StartsWith("AudioPilot.Tests.Services.Audio.", StringComparison.Ordinal)))
            || type == typeof(ViewModels.MixerMutationRegressionTests);

        MethodInfo[] tests = [.. typeof(TestExecutionGuardsTests).Assembly.GetTypes()
            .Where(IsAudioIntegrationType)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => (HasIntegrationTrait(type) || HasIntegrationTrait(method)) && method.IsDefined(typeof(FactAttribute))))];

        Assert.NotEmpty(tests);
        Assert.Contains(tests, method => method.DeclaringType == typeof(Services.Audio.NativeApplicationRoutingTests));
        Assert.Contains(tests, method => method.DeclaringType == typeof(ViewModels.MixerMutationRegressionTests));
        Assert.All(tests, method => Assert.True(
            method.IsDefined(typeof(AudioHardwareFactAttribute))
            || method.IsDefined(typeof(AudioHardwareTheoryAttribute))
            || method.IsDefined(typeof(HardwareSoakFactAttribute)),
            $"{method.DeclaringType!.Name}.{method.Name} must require explicit hardware audio permission at discovery time."));
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

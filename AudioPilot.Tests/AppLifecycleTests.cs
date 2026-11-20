using System.Runtime.CompilerServices;

namespace AudioPilot.Tests;

public sealed class AppLifecycleTests
{
    [Fact]
    public void DetachLifetimeEventHandlers_WhenNothingWasRegistered_DoesNotThrow()
    {
        App app = CreateAppShell();

        Exception? exception = Record.Exception(app.DetachLifetimeEventHandlersForTests);

        Assert.Null(exception);
        Assert.False(app.IsUnobservedTaskExceptionHandlerRegisteredForTests);
        Assert.Null(app.AttachedSingleInstanceHelperForTests);
    }

    [Fact]
    public void AttachSingleInstanceLifetimeHandlers_ThenDetachLifetimeEventHandlers_UnsubscribesStoredHelper()
    {
        App app = CreateAppShell();
        using var helper = new SingleInstanceHelper();

        app.AttachSingleInstanceLifetimeHandlersForTests(helper);

        Assert.Same(helper, app.AttachedSingleInstanceHelperForTests);
        Assert.True(helper.HasActivationRequestedSubscribersForTests);
        Assert.True(helper.HasCommandRequestedSubscribersForTests);

        app.DetachLifetimeEventHandlersForTests();

        Assert.Null(app.AttachedSingleInstanceHelperForTests);
        Assert.False(helper.HasActivationRequestedSubscribersForTests);
        Assert.False(helper.HasCommandRequestedSubscribersForTests);
    }

    private static App CreateAppShell()
    {
        return (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
    }
}

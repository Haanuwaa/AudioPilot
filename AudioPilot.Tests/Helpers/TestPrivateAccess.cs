using System.Collections;
using System.Reflection;
using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

internal static class TestPrivateAccess
{
    internal static T GetField<T>(object target, string fieldName)
    {
        FieldInfo? field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(target)!;
    }

    internal static object GetField(object target, string fieldName)
    {
        FieldInfo? field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target)!;
    }

    internal static void SetField<T>(object target, string fieldName, T value)
    {
        FieldInfo? field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    internal static Task InvokeNonPublicTask(object target, string methodName, params object?[]? args)
        => InvokeNonPublic<Task>(target, methodName, args);

    internal static Task<T> InvokeNonPublicTask<T>(object target, string methodName, params object?[]? args)
        => InvokeNonPublic<Task<T>>(target, methodName, args);

    internal static void RunTaskOnDispatcher(Task task, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, cancellationToken);
        deadline.CancelAfter(effectiveTimeout);
        using var registration = deadline.Token.Register(() => frame.Continue = false);
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted) _ = completed.Exception;
                frame.Continue = false;
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
        if (!task.IsCompleted)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Timed out while waiting for dispatcher test work to complete.");
        }
        task.GetAwaiter().GetResult();
    }

    internal static List<(int Id, string Description)> GetRegisteredHotkeys(HotkeyService hotkeyService)
    {
        var registrations = new List<(int Id, string Description)>();
        IList hotkeys = GetField<IList>(hotkeyService, "_hotkeys");
        foreach (object hotkey in hotkeys)
        {
            int id = GetField<int>(hotkey, "<Id>k__BackingField");
            string description = GetField<string>(hotkey, "<Description>k__BackingField");
            registrations.Add((id, description));
        }

        return registrations;
    }

    internal static object? InvokeNonPublic(object target, string methodName, params object?[]? args)
    {
        return InvokeMethod(target.GetType(), target, methodName, BindingFlags.Instance | BindingFlags.NonPublic, args);
    }

    internal static T InvokeNonPublic<T>(object target, string methodName, params object?[]? args)
        => (T)InvokeNonPublic(target, methodName, args)!;

    internal static object? InvokeNonPublicStatic(Type type, string methodName, params object?[]? args)
        => InvokeMethod(type, null, methodName, BindingFlags.Static | BindingFlags.NonPublic, args);

    private static object? InvokeMethod(Type type, object? target, string name, BindingFlags flags, object?[]? args)
    {
        MethodInfo? method = type.GetMethod(name, flags);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }
}

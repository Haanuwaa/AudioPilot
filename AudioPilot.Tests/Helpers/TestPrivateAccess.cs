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
    {
        MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method!.Invoke(target, args)!;
    }

    internal static Task<T> InvokeNonPublicTask<T>(object target, string methodName, params object?[]? args)
    {
        MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task<T>)method!.Invoke(target, args)!;
    }

    internal static void RunTaskOnDispatcher(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
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
}

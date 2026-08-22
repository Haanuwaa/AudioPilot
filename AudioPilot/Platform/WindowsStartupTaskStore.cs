using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AudioPilot.Platform;

internal interface IStartupTaskStore
{
    string UserSid { get; }
    string? Read();
    void Write(string xml);
    void Delete();
}

/// <summary>
/// Owns only the current user's named startup task. COM objects are scoped to each operation
/// so callers can use the service from either the dispatcher or a worker thread.
/// </summary>
internal sealed class WindowsStartupTaskStore(string appName) : IStartupTaskStore
{
    public string UserSid
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        }
    }

    public string? Read() => WithFolder((folder, sid) =>
    {
        object? task = null;
        try
        {
            task = folder.GetTask($"{appName} Startup {sid}");
            return (string)((dynamic)task).Xml;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            return null;
        }
        finally
        {
            Release(task);
        }
    });

    public void Write(string xml) => WithFolder<object?>((folder, sid) =>
    {
        object? task = null;
        try
        {
            string security = $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{sid})";
            task = folder.RegisterTask($"{appName} Startup {sid}", xml, 6, sid, null, 3, security);
            return null;
        }
        finally
        {
            Release(task);
        }
    });

    public void Delete() => WithFolder<object?>((folder, sid) =>
    {
        try
        {
            folder.DeleteTask($"{appName} Startup {sid}", 0);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002))
        {
        }
        return null;
    });

    private T WithFolder<T>(Func<dynamic, string, T> action)
    {
        object? service = null;
        object? folder = null;
        try
        {
            Type type = Type.GetTypeFromCLSID(new Guid("0F87369F-A4E5-4CFC-BD3E-73E6154572DD"), throwOnError: true)!;
            service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            return action(folder, UserSid);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    private static void Release(object? instance)
    {
        if (instance != null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}

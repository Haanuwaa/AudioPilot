using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI;

internal static partial class AppShellIntegration
{
    internal const string ApplicationId = "Haanuwaa.AudioPilot";
    internal const string NotificationRegistryPath = @"Software\Classes\AppUserModelId\" + ApplicationId;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint window, in Guid interfaceId, out nint propertyStore);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int eventId, uint flags, string path, nint unused);

    private static string ResolveExecutable() => Environment.ProcessPath is { } processPath &&
        string.Equals(Path.GetFileName(processPath), "AudioPilot.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath : Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");

    internal static void ApplyWindowIdentity(Window window)
    {
        if (DiagnosticSession.IsActive || Application.Current is not App) return;
        try
        {
            string executable = ResolveExecutable();
            if (File.Exists(executable))
                SetWindowIdentity(new System.Windows.Interop.WindowInteropHelper(window).Handle, executable);
        }
        catch (Exception ex) { Logger.Instance.Warning("AppShellIntegration", "window-shell-identity-unavailable", nameof(ApplyWindowIdentity), ex); }
    }

    internal static (uint Id, string Value)[] CreateWindowProperties(string executable) =>
    [
        (2, $"\"{executable}\""),
        (3, executable + ",0"),
        (4, Constants.AppConstants.Identity.DisplayName),
        (5, ApplicationId),
    ];

    internal static unsafe void SetWindowIdentity(nint window, string executable)
    {
        Guid propertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, in propertyStoreId, out nint store));
        if (store == 0) throw new InvalidOperationException("Window property store is unavailable.");
        try
        {
            nint* methods = *(nint**)store;
            // IPropertyStore's IUnknown vtable places SetValue at slot 6 and Commit at slot 7.
            var setValue = (delegate* unmanaged[Stdcall]<nint, NativePropertyKey*, NativePropVariant*, int>)methods[6];
            foreach ((uint id, string text) in CreateWindowProperties(executable))
            {
                NativePropertyKey key = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), id);
                NativePropVariant value = new() { vt = 31, pointerValue = Marshal.StringToCoTaskMemUni(text) };
                try { Marshal.ThrowExceptionForHR(setValue(store, &key, &value)); }
                finally { value.Dispose(); }
            }
            var commit = (delegate* unmanaged[Stdcall]<nint, int>)methods[7];
            Marshal.ThrowExceptionForHR(commit(store));
        }
        finally { Marshal.Release(store); }
    }

    internal static IReadOnlyList<JumpTask> CreateTasks(string executable) =>
    [
        CreateTask(executable, "Switch audio device", ShellAction.DevicePicker),
        CreateTask(executable, "Open volume mixer", ShellAction.VolumeMixer),
        CreateTask(executable, "Open routines", ShellAction.Routines),
    ];

    private static JumpTask CreateTask(string executable, string title, ShellAction action) => new()
    {
        Title = title,
        ApplicationPath = executable,
        Arguments = ShellActivation.GetArguments(action),
        WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
        IconResourcePath = executable,
        IconResourceIndex = 0,
    };

    internal static void InitializeIdentity()
    {
        if (DiagnosticSession.IsActive) return;
        try { Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(ApplicationId)); }
        catch (Exception ex) { Logger.Instance.Warning("AppShellIntegration", "shell-identity-unavailable", nameof(InitializeIdentity), ex); }
        RegisterNotificationDisplayName(CurrentUserRegistryAccessor.Instance);
    }

    /// <summary>Registers shared sender branding without replacing executable paths or activation metadata.</summary>
    internal static void RegisterNotificationDisplayName(IUserRegistryAccessor registry)
    {
        try
        {
            string displayName = Constants.AppConstants.Identity.DisplayName;
            if (!string.Equals(registry.GetValue(NotificationRegistryPath, "DisplayName") as string, displayName, StringComparison.Ordinal))
            {
                registry.SetValue(NotificationRegistryPath, "DisplayName", displayName);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Warning("AppShellIntegration", "notification-identity-unavailable", nameof(RegisterNotificationDisplayName), ex);
        }
    }

    internal static void RegisterJumpList(Application application)
    {
        if (DiagnosticSession.IsActive) return;
        try
        {
            string executable = ResolveExecutable();
            if (!File.Exists(executable)) return;
            var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            foreach (JumpTask task in CreateTasks(executable)) list.JumpItems.Add(task);
            list.JumpItemsRejected += (_, args) => Logger.Instance.Debug("AppShellIntegration", () => $"jump-list-items-rejected | count={args.RejectedItems.Count}");
            JumpList.SetJumpList(application, list);
            SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, executable, 0);
        }
        catch (Exception ex) { Logger.Instance.Warning("AppShellIntegration", "jump-list-unavailable", nameof(RegisterJumpList), ex); }
    }
}

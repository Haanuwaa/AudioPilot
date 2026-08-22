using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.Platform;

/// <summary>Scopes both IPC transports and process recovery to the same Windows user and session.</summary>
internal sealed partial record SingleInstanceScope(int SessionId, string UserSid)
{
    internal static SingleInstanceScope Current { get; } = GetCurrent();

    internal string Qualify(string instanceName) =>
        $"{instanceName}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UserSid)))}.{SessionId}";

    internal static SingleInstanceScope? Read(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, 0x0008, out SafeAccessTokenHandle token)) return null;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return identity.User is { } user ? new(process.SessionId, user.Value) : null;
        }
    }

    private static SingleInstanceScope GetCurrent()
    {
        using Process process = Process.GetCurrentProcess();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new(process.SessionId, identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user identity is unavailable."));
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
}

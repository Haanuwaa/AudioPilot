using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AudioPilot.Tests.Helpers;

internal partial class TestScopedDirectory : IDisposable
{
    public string Root { get; }

    internal TestScopedDirectory(string scopeName)
    {
        Root = Path.Combine(Path.GetTempPath(), "AudioPilot.Tests", scopeName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal static void CreateHardLink(string link, string existingFile)
    {
        if (!CreateHardLinkNative(link, existingFile, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Dispose()
    {
        DisposeResources();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
        }
    }

    protected virtual void DisposeResources()
    {
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkNative(string link, string existingFile, IntPtr securityAttributes);
}

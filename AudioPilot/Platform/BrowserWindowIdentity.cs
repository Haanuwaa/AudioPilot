using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AudioPilot.Platform;

/// <summary>
/// Reads the shell's effective window identity, including process-level IDs used by portable browsers.
/// The Windows shell resolver is optional and undocumented; activation or lookup failure leaves other discovery paths available.
/// </summary>
internal sealed class BrowserWindowIdentity : IDisposable
{
    private readonly IActivatedNativeComObject<IApplicationResolverNativeInterop>? _resolver;

    internal BrowserWindowIdentity()
    {
        if (ComThreadingHelper.EnsureComInitialized())
        {
            NativeAudioInteropHelper.ComActivator.TryCreateTyped(
                new Guid("660B90C8-73A9-4B58-8CAE-355B7F55341B"), typeof(IApplicationResolverNativeInterop).GUID, 1,
                out _resolver, out _);
        }
    }

    internal string? GetAppId(nint window, uint processId)
    {
        if (_resolver == null) { return null; }
        nint text = 0;
        try
        {
            int result = _resolver.Interface.GetAppIDForWindow(window, out text, 0, 0, 0);
            if (result < 0 || text == 0)
            {
                Marshal.FreeCoTaskMem(text);
                text = 0;
                result = _resolver.Interface.GetAppIDForProcess(processId, out text, 0, 0, 0);
            }
            return result >= 0 && text != 0 ? Marshal.PtrToStringUni(text) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    public void Dispose() => _resolver?.Dispose();
}

/// <summary>Projects the Windows 8+ resolver IID; the shortcut methods preserve the native vtable slots.</summary>
[GeneratedComInterface]
[Guid("DE25675A-72DE-44B4-9373-05170450C140")]
internal partial interface IApplicationResolverNativeInterop
{
    [PreserveSig]
    int GetAppIDForShortcut(nint shellItem, out nint appId);
    [PreserveSig]
    int GetAppIDForShortcutObject(nint shellLink, nint shellItem, out nint appId);
    [PreserveSig]
    int GetAppIDForWindow(nint window, out nint appId, nint pinningPrevented, nint explicitAppId, nint embeddedShortcutValid);
    [PreserveSig]
    int GetAppIDForProcess(uint processId, out nint appId, nint pinningPrevented, nint explicitAppId, nint embeddedShortcutValid);
}

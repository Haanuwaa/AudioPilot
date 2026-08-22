using AudioPilot.Services.Hotkeys;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Hotkeys;

public sealed class AppRuntimeHotkeyBindingsTests
{
    [Fact]
    public void Unwire_RemovesRuntimeCallbacksAndIsIdempotent()
    {
        using var hotkeys = new HotkeyService();
        int callbackCount = 0;
        void callback() => callbackCount++;
        var bindings = new AppRuntimeHotkeyBindings(
            hotkeys,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback,
callback);

        bindings.Wire();
        TestPrivateAccess.GetField<Action?>(hotkeys, "OnToggleAppVisibilityHotkeyPressed")?.Invoke();
        Assert.Equal(1, callbackCount);

        bindings.Unwire();
        bindings.Unwire();

        Assert.Null(TestPrivateAccess.GetField<Action?>(hotkeys, "OnToggleAppVisibilityHotkeyPressed"));
        Assert.Equal(1, callbackCount);
    }
}

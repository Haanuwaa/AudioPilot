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
            callback,
            callback,
            callback,
            callback,
            onQuickDevicePicker: callback,
            onVolumeMixer: callback);

        bindings.Wire();
        TestPrivateAccess.GetField<Action?>(hotkeys, "OnToggleAppVisibilityHotkeyPressed")?.Invoke();
        TestPrivateAccess.GetField<Action?>(hotkeys, "OnShowAudioStatusPressed")?.Invoke();
        TestPrivateAccess.GetField<Action?>(hotkeys, "OnQuickDevicePickerPressed")?.Invoke();
        TestPrivateAccess.GetField<Action?>(hotkeys, "OnVolumeMixerPressed")?.Invoke();
        TestPrivateAccess.GetField<Action?>(hotkeys, "OnMediaSeekForwardPressed")?.Invoke();
        TestPrivateAccess.GetField<Action?>(hotkeys, "OnMediaSeekBackwardPressed")?.Invoke();
        Assert.Equal(6, callbackCount);

        bindings.Unwire();
        bindings.Unwire();

        Assert.Null(TestPrivateAccess.GetField<Action?>(hotkeys, "OnToggleAppVisibilityHotkeyPressed"));
        Assert.Null(TestPrivateAccess.GetField<Action?>(hotkeys, "OnShowAudioStatusPressed"));
        Assert.Null(TestPrivateAccess.GetField<Action?>(hotkeys, "OnQuickDevicePickerPressed"));
        Assert.Null(TestPrivateAccess.GetField<Action?>(hotkeys, "OnVolumeMixerPressed"));
        Assert.Null(TestPrivateAccess.GetField<Action?>(hotkeys, "OnMediaSeekForwardPressed"));
        Assert.Null(TestPrivateAccess.GetField<Action?>(hotkeys, "OnMediaSeekBackwardPressed"));
        Assert.Equal(6, callbackCount);
    }
}

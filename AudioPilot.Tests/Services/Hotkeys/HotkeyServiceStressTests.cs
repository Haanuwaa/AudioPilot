using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Hotkeys;

[Trait(TestCategories.Name, TestCategories.Stress)]
public sealed class HotkeyServiceStressTests
{
    [StressFact]
    public void RegisterDynamicHotkey_RepeatedReplacement_KeepsRegistrationStateConsistent_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(RegisterDynamicHotkey_RepeatedReplacement_KeepsRegistrationStateConsistent_WhenStressEnabled)))
        {
            return;
        }

        using HotkeyService service = CreateStressService();
        string[] hotkeys = ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];
        const int dynamicId = 12000;

        for (int iteration = 0; iteration < 1000; iteration++)
        {
            bool registered = service.RegisterDynamicHotkey(dynamicId, hotkeys[iteration % hotkeys.Length], static () => { }, "Stress Hotkey");
            Assert.True(registered);
        }

        Assert.Single(TestPrivateAccess.GetRegisteredHotkeys(service), registration => registration.Id == dynamicId);
        Assert.Single(TestPrivateAccess.GetField<HashSet<string>>(service, "_registeredCombos"));
        Assert.Single(TestPrivateAccess.GetField<Dictionary<int, string>>(service, "_registrationDeliveryById"));
        Assert.Single(TestPrivateAccess.GetField<Dictionary<int, HotkeyRegistrationOutcome>>(service, "_registrationOutcomeById"));

        service.UnregisterHotkey(dynamicId);

        Assert.Empty(TestPrivateAccess.GetRegisteredHotkeys(service));
        Assert.Empty(TestPrivateAccess.GetField<HashSet<string>>(service, "_registeredCombos"));
        Assert.Empty(TestPrivateAccess.GetField<Dictionary<int, string>>(service, "_registrationDeliveryById"));
        Assert.Empty(TestPrivateAccess.GetField<Dictionary<int, HotkeyRegistrationOutcome>>(service, "_registrationOutcomeById"));
    }

    [StressFact]
    public void RegisterAndUnregisterMixedHotkeyBatches_LeavesNoResidualState_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(RegisterAndUnregisterMixedHotkeyBatches_LeavesNoResidualState_WhenStressEnabled)))
        {
            return;
        }

        FakeMouseHotkeyCaptureHost? host = null;
        using HotkeyService service = CreateStressService(dispatch => host = new FakeMouseHotkeyCaptureHost(dispatch));
        Assert.NotNull(host);

        for (int iteration = 0; iteration < 250; iteration++)
        {
            Assert.True(service.RegisterOutputSwitchHotkey("Ctrl+Alt+F1"));
            Assert.True(service.RegisterInputSwitchHotkey("Ctrl+Alt+F2"));
            Assert.True(service.RegisterOutputReverseSwitchHotkey("Ctrl+Alt+F3"));
            Assert.True(service.RegisterInputReverseSwitchHotkey("Ctrl+Alt+F4"));
            Assert.True(service.RegisterMediaHotkeys("F7", "F8", "F9", "F10"));
            Assert.True(service.RegisterDynamicHotkey(13000, "Ctrl+WheelUp", static () => { }, "Wheel Stress Hotkey"));
            Assert.True(host!.IsRunning);
            Assert.True(host.Snapshot.TryMatch(HotkeyMainInput.WheelUp, HotkeyModifierMask.Ctrl, out _));

            service.UnregisterAllHotkeys();

            Assert.Empty(TestPrivateAccess.GetRegisteredHotkeys(service));
            Assert.Empty(TestPrivateAccess.GetField<HashSet<string>>(service, "_registeredCombos"));
            Assert.Empty(TestPrivateAccess.GetField<Dictionary<int, string>>(service, "_registrationDeliveryById"));
            Assert.Empty(TestPrivateAccess.GetField<Dictionary<int, HotkeyRegistrationOutcome>>(service, "_registrationOutcomeById"));
            Assert.False(host.IsRunning);
            Assert.False(host.Snapshot.TryMatch(HotkeyMainInput.WheelUp, HotkeyModifierMask.Ctrl, out _));
        }
    }

    private static HotkeyService CreateStressService(Func<Action<MouseHotkeyBindingSnapshot, long>, IMouseHotkeyCaptureHost>? mouseCaptureHostFactory = null)
    {
        var service = new HotkeyService(
            hotkeyParser: null,
            dispatchCoordinator: null,
            registerHotKeyInvoker: static (_, _, _, _) => new HotkeyOsRegistrationResult(Succeeded: true),
            unregisterHotKeyInvoker: static (_, _) => { },
            setKeyboardHookInvoker: static () => (IntPtr)1,
            mouseCaptureHostFactory: mouseCaptureHostFactory);
        service.InitializeInfrastructure();
        return service;
    }

    private sealed class FakeMouseHotkeyCaptureHost(Action<MouseHotkeyBindingSnapshot, long> dispatchMatch) : IMouseHotkeyCaptureHost
    {
        private readonly Action<MouseHotkeyBindingSnapshot, long> _ = dispatchMatch;

        public bool IsRunning { get; private set; }
        public MouseHotkeySnapshot Snapshot { get; private set; } = MouseHotkeySnapshot.Empty;

        public bool TryStart(MouseHotkeySnapshot snapshot)
        {
            Snapshot = snapshot;
            IsRunning = true;
            return true;
        }

        public void UpdateSnapshot(MouseHotkeySnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public void Stop()
        {
            Snapshot = MouseHotkeySnapshot.Empty;
            IsRunning = false;
        }

        public void Dispose()
        {
            Snapshot = MouseHotkeySnapshot.Empty;
            IsRunning = false;
        }
    }
}

using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppExternalReloadCoordinatorTests
{
    [Fact]
    public void Apply_ExecutesExpectedReloadSequence()
    {
        List<string> calls = [];
        Settings settings = new()
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices = [new CycleDevice { Id = "out-1", Name = "Speakers" }]
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices = [new CycleDevice { Id = "in-1", Name = "Mic" }]
                }
            },
            Routines = new RoutinesSettings
            {
                Items = [new AudioRoutine { Id = "routine-1", Name = "Routine" }]
            }
        };

        AppExternalReloadCoordinator.Apply(
            settings,
            new ExternalReloadDependencies(
                () => calls.Add("cache"),
                () => calls.Add("log-level"),
                () => calls.Add("advanced-tuning"),
                () => calls.Add("load-output"),
                _ => calls.Add("apply-output"),
                () => calls.Add("load-input"),
                _ => calls.Add("apply-input"),
                _ => calls.Add("apply-routines"),
                () => calls.Add("output-hotkey"),
                () => calls.Add("output-reverse-hotkey"),
                () => calls.Add("input-hotkey"),
                () => calls.Add("input-reverse-hotkey"),
                () => calls.Add("output-hotkeys-enabled"),
                () => calls.Add("input-hotkeys-enabled"),
                () => calls.Add("theme"),
                () => calls.Add("overlay-position"),
                () => calls.Add("overlay-duration"),
                () =>
                {
                    calls.Add("register-hotkeys");
                    return new HotkeyRegistrationResult(true, true, true, true, true, true, true, true, true);
                },
                _ => calls.Add("log-hotkeys"),
                () => calls.Add("register-routine-hotkeys"),
                () => calls.Add("startup"),
                () => calls.Add("preserve-audio-levels"),
                () => calls.Add("overlay-enabled"),
                () => calls.Add("settings-apply-log"),
                () => calls.Add("overlay-display"),
                () => calls.Add("audio-config"),
                () => calls.Add("sync-drafts")));

        Assert.Equal(
            [
                "cache", "log-level", "advanced-tuning", "load-output", "apply-output", "load-input", "apply-input", "apply-routines",
                "output-hotkey", "output-reverse-hotkey", "input-hotkey", "input-reverse-hotkey",
                "output-hotkeys-enabled", "input-hotkeys-enabled", "theme", "overlay-position", "overlay-duration",
                "register-hotkeys", "log-hotkeys", "register-routine-hotkeys", "startup", "preserve-audio-levels",
                "overlay-enabled", "settings-apply-log", "overlay-display", "audio-config", "sync-drafts"
            ],
            calls);
    }
}

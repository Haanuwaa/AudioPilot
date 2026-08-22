using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppStartupRegistryCoordinatorTests
{
    [Theory]
    [InlineData(false, true, false, false, (int)StartupInitAction.Add, true, false)]
    [InlineData(false, true, true, true, (int)StartupInitAction.ValidatePath, true, false)]
    [InlineData(false, false, true, false, (int)StartupInitAction.Remove, false, true)]
    [InlineData(false, false, false, false, (int)StartupInitAction.None, false, false)]
    [InlineData(true, false, true, true, (int)StartupInitAction.None, true, false)]
    [InlineData(true, false, true, false, (int)StartupInitAction.Add, true, false)]
    [InlineData(true, false, false, false, (int)StartupInitAction.None, false, false)]
    public void BuildInitPlan_ReturnsExpectedPlan(
        bool noSettingsFileExists,
        bool settingsRunAtStartup,
        bool inStartup,
        bool inStartupWithValidPath,
        int expectedAction,
        bool expectedRunAtStartupValue,
        bool expectedWarn)
    {
        StartupInitPlan plan = AppStartupRegistryCoordinator.BuildInitPlan(
            noSettingsFileExists,
            settingsRunAtStartup,
            inStartup,
            inStartupWithValidPath);

        Assert.Equal((StartupInitAction)expectedAction, plan.Action);
        Assert.Equal(expectedRunAtStartupValue, plan.RunAtStartupValue);
        Assert.Equal(expectedWarn, plan.Warn);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void IsStaleDebounceRequest_ReturnsExpectedValue(bool useSameSource, bool currentRunAtStartupDiffers, bool expected)
    {
        using var active = new CancellationTokenSource();
        using var request = useSameSource ? active : new CancellationTokenSource();

        bool stale = AppStartupRegistryCoordinator.IsStaleDebounceRequest(
            active,
            request,
            currentRunAtStartup: !currentRunAtStartupDiffers,
            targetValue: currentRunAtStartupDiffers);

        Assert.Equal(expected, stale);
    }

    [Fact]
    public void CreateStartupUpdatedSettings_PreservesStartupRelevantFields_AndClonesCollections()
    {
        var source = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "output-1", Name = "Speakers" }
                    ],
                    SwitchHotkey = "Ctrl+Alt+O",
                    ReverseSwitchHotkey = "Ctrl+Alt+Shift+O",
                    HotkeysEnabled = false
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "input-1", Name = "Microphone" }
                    ],
                    SwitchHotkey = "Ctrl+Alt+I",
                    ReverseSwitchHotkey = "Ctrl+Alt+Shift+I",
                    HotkeysEnabled = false
                }
            },
            Hotkeys = new HotkeysSettings
            {
                Listen = new HotkeysListenSettings
                {
                    MonitorOutputDeviceId = "output-monitor-id",
                    MonitorOutputDeviceName = "Output Monitor"
                }
            },
            RunAtStartup = false,
        };

        Settings updated = AppStartupRegistryCoordinator.CreateStartupUpdatedSettings(source, startupEnabled: true);

        Assert.True(updated.RunAtStartup);
        Assert.Equal(source.DeviceSwitching.Output.ReverseSwitchHotkey, updated.DeviceSwitching.Output.ReverseSwitchHotkey);
        Assert.Equal(source.DeviceSwitching.Input.ReverseSwitchHotkey, updated.DeviceSwitching.Input.ReverseSwitchHotkey);
        Assert.Equal(source.DeviceSwitching.Output.SwitchHotkey, updated.DeviceSwitching.Output.SwitchHotkey);
        Assert.Equal(source.DeviceSwitching.Input.SwitchHotkey, updated.DeviceSwitching.Input.SwitchHotkey);
        Assert.Equal(source.DeviceSwitching.Output.HotkeysEnabled, updated.DeviceSwitching.Output.HotkeysEnabled);
        Assert.Equal(source.DeviceSwitching.Input.HotkeysEnabled, updated.DeviceSwitching.Input.HotkeysEnabled);
        Assert.Equal(source.Hotkeys.Listen.MonitorOutputDeviceId, updated.Hotkeys.Listen.MonitorOutputDeviceId);
        Assert.Equal(source.Hotkeys.Listen.MonitorOutputDeviceName, updated.Hotkeys.Listen.MonitorOutputDeviceName);
        Assert.Equal(source.DeviceSwitching.Output.SwitchRoles, updated.DeviceSwitching.Output.SwitchRoles);
        Assert.NotSame(source.DeviceSwitching.Output.CycleDevices, updated.DeviceSwitching.Output.CycleDevices);
        Assert.NotSame(source.DeviceSwitching.Input.CycleDevices, updated.DeviceSwitching.Input.CycleDevices);
        Assert.NotSame(source.DeviceSwitching.Output.CycleDevices[0], updated.DeviceSwitching.Output.CycleDevices[0]);
        Assert.NotSame(source.DeviceSwitching.Input.CycleDevices[0], updated.DeviceSwitching.Input.CycleDevices[0]);
    }

    [Theory]
    [InlineData((int)StartupInitAction.Add, true, false, false)]
    [InlineData((int)StartupInitAction.Remove, false, true, false)]
    [InlineData((int)StartupInitAction.ValidatePath, false, false, true)]
    [InlineData((int)StartupInitAction.None, false, false, false)]
    public void ExecuteInitPlan_InvokesExpectedAction(int action, bool expectedAdd, bool expectedRemove, bool expectedValidate)
    {
        bool addInvoked = false;
        bool removeInvoked = false;
        bool validateInvoked = false;

        AppStartupRegistryCoordinator.ExecuteInitPlan(
            new StartupInitPlan((StartupInitAction)action, "test", true, false),
            noSettingsFileExists: false,
            addToStartup: () => addInvoked = true,
            removeFromStartup: () => removeInvoked = true,
            validateAndUpdateStartupPath: () => validateInvoked = true,
            startupRegistryOpId: "startup-registry:test-action",
            logger: Logger.Instance);

        Assert.Equal(expectedAdd, addInvoked);
        Assert.Equal(expectedRemove, removeInvoked);
        Assert.Equal(expectedValidate, validateInvoked);
    }

    [Fact]
    public void ExecuteInitPlan_LogsOpId_WhenAddingRegistryEntry()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppStartupRegistryCoordinatorTests), "startup-registry-init.log", LogLevel.Info);

        AppStartupRegistryCoordinator.ExecuteInitPlan(
            new StartupInitPlan(StartupInitAction.Add, "test-add", true, false),
            noSettingsFileExists: false,
            addToStartup: static () => { },
            removeFromStartup: static () => { },
            validateAndUpdateStartupPath: static () => { },
            startupRegistryOpId: "startup-registry:test-init",
            logger: loggerScope.Logger);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistrySync, logText, StringComparison.Ordinal);
        Assert.Contains("opId=startup-registry:test-init", logText, StringComparison.Ordinal);
        Assert.Contains("action=add", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void TryApplyStartupChange_ReturnsFalse_WhenRegistryActionThrows()
    {
        bool result = AppStartupRegistryCoordinator.TryApplyStartupChange(
            enable: true,
            addToStartup: () => throw new InvalidOperationException("boom"),
            removeFromStartup: static () => { },
            startupRegistryOpId: "startup-registry:test-error",
            logger: Logger.Instance,
            methodName: "test");

        Assert.False(result);
    }

    [Fact]
    public void TryApplyStartupChange_LogsOpId_OnSuccess()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppStartupRegistryCoordinatorTests), "startup-registry-update.log", LogLevel.Info);

        bool result = AppStartupRegistryCoordinator.TryApplyStartupChange(
            enable: true,
            addToStartup: static () => { },
            removeFromStartup: static () => { },
            startupRegistryOpId: "startup-registry:test-update",
            logger: loggerScope.Logger,
            methodName: "test");

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.True(result);
        Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.App.StartupRegistryUpdate, logText, StringComparison.Ordinal);
        Assert.Contains("opId=startup-registry:test-update", logText, StringComparison.Ordinal);
        Assert.Contains("action=add success=true", logText, StringComparison.Ordinal);
    }
}

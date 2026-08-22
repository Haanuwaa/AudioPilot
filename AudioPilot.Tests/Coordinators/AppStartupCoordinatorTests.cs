using System.Text.RegularExpressions;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Configuration;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class AppStartupCoordinatorTests
{
    [Fact]
    public async Task InitializeAsync_ShowsWindow_WhenSettingsAreNull()
    {
        var vm = new FakeStartupViewModel { CurrentSettings = null };
        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-null-settings.log", LogLevel.Info);
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        Assert.Equal(1, vm.InitializeCalls);
        Assert.Equal(1, vm.ShowCalls);
        Assert.Equal(0, vm.MinimizeCalls);
    }

    [Fact]
    public async Task InitializeAsync_Minimizes_WhenConfigured()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply"
                    }
                }
            }
        };
        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-minimize.log", LogLevel.Info);
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        Assert.Equal(1, vm.InitializeCalls);
        Assert.Equal(0, vm.ShowCalls);
        Assert.Equal(1, vm.StartHiddenCalls);
        Assert.Equal(0, vm.MinimizeCalls);
        Assert.Equal(1, vm.EnableRoutineAppStartMonitoringCalls);
        Assert.Equal(1, vm.ExecuteAudioPilotStartupRoutinesCalls);
        Assert.True(vm.LastAudioPilotStartupShowOverlay);
        Assert.Equal(1, vm.MarkStartupVisibilityResolvedCalls);
        Assert.Equal(0, vm.RegisterRoutineHotkeysCalls);
        Assert.Equal(1, hotkeys.ToggleAppVisibilityCalls);
        Assert.Equal(1, hotkeys.MediaCalls);
        Assert.Equal(1, hotkeys.MuteCalls);
        Assert.Equal(1, hotkeys.ListenCalls);
        Assert.Equal(1, hotkeys.VolumeStepCalls);
        Assert.Equal(1, hotkeys.OutputSwitchCalls);
        Assert.Equal(1, hotkeys.InputSwitchCalls);
        Assert.Equal(1, hotkeys.OutputReverseCalls);
        Assert.Equal(1, hotkeys.InputReverseCalls);
    }

    [Fact]
    public async Task InitializeAsync_ShowsWindow_WhenUnconfigured()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings()
        };
        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-unconfigured.log", LogLevel.Info);
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: true);

        Assert.Equal(1, vm.InitializeCalls);
        Assert.Equal(true, vm.LastNoSettingsFlag);
        Assert.Equal(1, vm.ShowCalls);
        Assert.Equal(0, vm.MinimizeCalls);
        Assert.Equal(1, vm.EnableRoutineAppStartMonitoringCalls);
        Assert.Equal(1, vm.ExecuteAudioPilotStartupRoutinesCalls);
        Assert.True(vm.LastAudioPilotStartupShowOverlay);
        Assert.Equal(1, vm.MarkStartupVisibilityResolvedCalls);
        Assert.Equal(0, vm.RegisterRoutineHotkeysCalls);
    }

    [Fact]
    public async Task InitializeAsync_ShowsWindow_WhenConfiguredButShowWasRequestedDuringStartup()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply"
                    }
                }
            },
            HasInteractiveShowRequest = true,
        };
        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-show-request.log", LogLevel.Info);
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        Assert.Equal(1, vm.ShowCalls);
        Assert.Equal(0, vm.StartHiddenCalls);
    }

    [Fact]
    public async Task InitializeAsync_ResolvesStartupVisibilityWithoutDeferredRecheck()
    {
        int showRequestChecks = 0;
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply"
                    }
                }
            },
            HasInteractiveShowRequestProvider = () =>
            {
                showRequestChecks++;
                return false;
            },
        };

        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-single-visibility-resolution.log", LogLevel.Info);
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        Assert.Equal(1, showRequestChecks);
        Assert.Equal(0, vm.ShowCalls);
        Assert.Equal(1, vm.StartHiddenCalls);
    }

    [Fact]
    public async Task InitializeAsync_DisabledSwitchHotkeys_RegisterAsEmpty()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply",
                        ReverseSwitchHotkey = "Ctrl+Alt+Shift+Multiply",
                        HotkeysEnabled = false
                    },
                    Input = new DeviceSwitchingInputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Subtract",
                        ReverseSwitchHotkey = "Ctrl+Alt+Shift+Subtract",
                        HotkeysEnabled = false
                    }
                }
            }
        };

        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-disabled-switch-hotkeys.log", LogLevel.Info);
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        Assert.Equal(string.Empty, hotkeys.LastOutputSwitchHotkey);
        Assert.Equal(string.Empty, hotkeys.LastOutputReverseHotkey);
        Assert.Equal(string.Empty, hotkeys.LastInputSwitchHotkey);
        Assert.Equal(string.Empty, hotkeys.LastInputReverseHotkey);
    }

    [Fact]
    public async Task InitializeAsync_ShowsSettingsWarning_WhenDiagnosticsExist()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply"
                    }
                }
            },
            Warnings =
            [
                "Show/hide app hotkey value 'BadKey' is invalid. Set a valid combination."
            ]
        };

        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-settings-warning.log", LogLevel.Info);

        string? warningMessage = null;
        string? warningCaption = null;
        var coordinator = new AppStartupCoordinator(
            vm,
            hotkeys,
            loggerScope.Logger,
            (message, caption) =>
            {
                warningMessage = message;
                warningCaption = caption;
                return Task.FromResult(AppDialogResult.Acknowledged);
            });

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        Assert.Equal("Settings Warnings", warningCaption);
        Assert.NotNull(warningMessage);
        Assert.Contains("Some settings need attention:", warningMessage);
        Assert.Contains("Show/hide app hotkey", warningMessage);
    }

    [Fact]
    public async Task InitializeAsync_SuppressesDisconnectedDeviceWarnings_WhenConfigured()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                Miscellaneous = new MiscellaneousSettings
                {
                    SuppressDeviceStartupWarnings = true,
                },
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply"
                    }
                }
            },
            Diagnostics =
            [
                new("output-cycle-disconnected-devices", "Output cycle includes disconnected device: Headset.", "Reconnect that output device."),
                new("input-cycle-disconnected-devices", "Input cycle includes disconnected device: Mic.", "Reconnect that input device."),
            ]
        };

        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-settings-warning-suppressed.log", LogLevel.Debug);

        string? warningMessage = null;
        var coordinator = new AppStartupCoordinator(
            vm,
            hotkeys,
            loggerScope.Logger,
            (message, _) =>
            {
                warningMessage = message;
                return Task.FromResult(AppDialogResult.Acknowledged);
            });

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Null(warningMessage);
        Assert.Contains("startup-settings-warnings-suppressed", logText, StringComparison.Ordinal);
        Assert.Contains("count=2", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_SuppressesOnlyDisconnectedDeviceWarnings()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                Miscellaneous = new MiscellaneousSettings
                {
                    SuppressDeviceStartupWarnings = true,
                },
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply"
                    }
                }
            },
            Diagnostics =
            [
                new("output-cycle-disconnected-devices", "Output cycle includes disconnected device: Headset.", "Reconnect that output device."),
                new("invalid-hotkey-toggle-app-visibility-hotkey", "Show/hide app hotkey value 'BadKey' is invalid.", "Set a valid combination."),
            ]
        };

        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-settings-warning-partial-suppressed.log", LogLevel.Info);

        string? warningMessage = null;
        var coordinator = new AppStartupCoordinator(
            vm,
            hotkeys,
            loggerScope.Logger,
            (message, _) =>
            {
                warningMessage = message;
                return Task.FromResult(AppDialogResult.Acknowledged);
            });

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        Assert.NotNull(warningMessage);
        Assert.DoesNotContain("Output cycle", warningMessage);
        Assert.Contains("Show/hide app hotkey", warningMessage);
    }

    [Fact]
    public async Task InitializeAsync_EmitsCorrelatedStartupLifecycleLogs()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup.log", LogLevel.Debug);

        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply"
                    }
                }
            }
        };
        var hotkeys = new FakeStartupHotkeyRegistrar();
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        string logText = loggerScope.DisposeAndReadLogText();

        Match opIdMatch = MyRegex().Match(logText);
        Assert.True(opIdMatch.Success, $"Expected startup opId in log.\nLog text:\n{logText}");
        string startupOpId = opIdMatch.Groups[1].Value;

        Assert.Contains(AppConstants.Audio.LogEvents.StartupCoordinator.Start, logText, StringComparison.Ordinal);
        Assert.Contains(AppConstants.Audio.LogEvents.StartupCoordinator.Complete, logText, StringComparison.Ordinal);
        Assert.Contains($"opId={startupOpId}", logText, StringComparison.Ordinal);
        Assert.Contains("action=start-hidden-to-tray", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(AppConstants.Audio.LogEvents.StartupCoordinator.HotkeysRegisterProcessed, logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_LogsCorrelatedHotkeyRegistrationFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-failure.log", LogLevel.Debug);

        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = "Ctrl+Alt+Multiply"
                    }
                }
            }
        };
        var hotkeys = new FakeStartupHotkeyRegistrar
        {
            OutputSwitchResult = false,
        };
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        string logText = loggerScope.DisposeAndReadLogText();

        Match opIdMatch = MyRegex().Match(logText);
        Assert.True(opIdMatch.Success, $"Expected startup opId in log.\nLog text:\n{logText}");
        string startupOpId = opIdMatch.Groups[1].Value;

        Assert.Contains(AppConstants.Audio.LogEvents.StartupCoordinator.HotkeysRegisterFailed, logText, StringComparison.Ordinal);
        Assert.Contains($"opId={startupOpId}", logText, StringComparison.Ordinal);
        Assert.Contains("output=False", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_RegistersVolumeStepHotkeys_OnStartup()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                Hotkeys = new HotkeysSettings
                {
                    Volume = new HotkeysVolumeSettings
                    {
                        MasterUp = "Alt+WheelUp",
                        MasterDown = "Alt+WheelDown"
                    }
                }
            }
        };
        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-volume-step.log", LogLevel.Debug);
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(1, hotkeys.VolumeStepCalls);
        Assert.DoesNotContain(AppConstants.Audio.LogEvents.StartupCoordinator.HotkeysRegisterProcessed, logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_LogsVolumeStepRegistrationFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-volume-step-failure.log", LogLevel.Debug);

        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                Hotkeys = new HotkeysSettings
                {
                    Volume = new HotkeysVolumeSettings
                    {
                        MasterUp = "Alt+WheelUp"
                    }
                }
            }
        };
        var hotkeys = new FakeStartupHotkeyRegistrar
        {
            VolumeStepResult = false,
        };
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppConstants.Audio.LogEvents.StartupCoordinator.HotkeysRegisterFailed, logText, StringComparison.Ordinal);
        Assert.Contains("volumeStep=False", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_PropagatesAllowedStandaloneExceptionsToHotkeyRegistrar()
    {
        var vm = new FakeStartupViewModel
        {
            CurrentSettings = new Settings
            {
                Hotkeys = new HotkeysSettings
                {
                    App = new HotkeysAppSettings
                    {
                        ToggleAppVisibility = "PrintScreen"
                    },
                    Global = new HotkeysGlobalSettings
                    {
                        AdditionalStandaloneKeys = ["PrintScreen"]
                    }
                }
            }
        };
        var hotkeys = new FakeStartupHotkeyRegistrar();
        using var loggerScope = new TestLoggerScope(nameof(AppStartupCoordinatorTests), "startup-standalone-allowlist.log", LogLevel.Info);
        var coordinator = new AppStartupCoordinator(vm, hotkeys, loggerScope.Logger);

        await coordinator.InitializeAsync(noSettingsFileExists: false);

        Assert.Equal(1, hotkeys.UpdateAllowedStandaloneCalls);
        Assert.Equal(["PrintScreen"], hotkeys.LastAdditionalStandaloneHotkeyKeys);
    }

    [GeneratedRegex(@"opId=(startup:[0-9a-f]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}


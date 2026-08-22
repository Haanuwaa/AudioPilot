using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppHotplugOverlayCoordinatorTests
{
    [Fact]
    public void ProcessPostRefresh_WhenConfiguredOutputConnects_ShowsOutputOverlay()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentOutputDevices = [new CycleDevice { Id = "out-1", Name = "Desk Speakers" }];

            coordinator.ProcessPostRefresh();

            RecordingOverlayPresenter presenter = Assert.Single(presenters);
            var (kind, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(OverlayDeviceKind.Output, kind);
            Assert.Equal("Connected output device", header);
            Assert.Equal("Desk Speakers", deviceName);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenConfiguredOutputRemapsByName_ShowsOutputOverlay()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-saved", Name = "Desk Speakers" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentOutputDevices = [new CycleDevice { Id = "out-remapped", Name = "Desk Speakers" }];

            coordinator.ProcessPostRefresh();

            RecordingOverlayPresenter presenter = Assert.Single(presenters);
            var (kind, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(OverlayDeviceKind.Output, kind);
            Assert.Equal("Connected output device", header);
            Assert.Equal("Desk Speakers", deviceName);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenConfiguredOutputNameMatchIsAmbiguous_DoesNotShowOverlay()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-saved", Name = "Desk Speakers" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentOutputDevices =
            [
                new CycleDevice { Id = "out-remapped-a", Name = "Desk Speakers" },
                new CycleDevice { Id = "out-remapped-b", Name = "Desk Speakers" }
            ];

            coordinator.ProcessPostRefresh();

            Assert.Empty(presenters);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenConfiguredInputNameMatchIsAmbiguous_DoesNotShowOverlay()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Input = new DeviceSwitchingInputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "in-saved", Name = "Desk Mic" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentInputDevices =
            [
                new CycleDevice { Id = "in-remapped-a", Name = "Desk Mic" },
                new CycleDevice { Id = "in-remapped-b", Name = "Desk Mic" }
            ];

            coordinator.ProcessPostRefresh();

            Assert.Empty(presenters);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenConfiguredOutputConnects_UsesActiveDeviceName()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Saved Speakers" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentOutputDevices = [new CycleDevice { Id = "out-1", Name = "Current Speakers" }];

            coordinator.ProcessPostRefresh();

            RecordingOverlayPresenter presenter = Assert.Single(presenters);
            var (kind, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(OverlayDeviceKind.Output, kind);
            Assert.Equal("Connected output device", header);
            Assert.Equal("Current Speakers", deviceName);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenConnectedOverlaySuppressed_DoesNotShowConnectedOutput()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            harness.ViewModel.SuppressConnectedHotplugOverlay(output: true, suppressMs: 10_000);
            currentOutputDevices = [new CycleDevice { Id = "out-1", Name = "Desk Speakers" }];

            coordinator.ProcessPostRefresh();

            Assert.Empty(presenters);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenOutputConnectsAndInputDisconnects_ShowsStackedOverlays()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" }
                        ]
                    },
                    Input = new DeviceSwitchingInputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "in-1", Name = "Desk Mic" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [new CycleDevice { Id = "in-1", Name = "Desk Mic" }];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentOutputDevices = [new CycleDevice { Id = "out-1", Name = "Desk Speakers" }];
            currentInputDevices = [];

            coordinator.ProcessPostRefresh();

            Assert.Equal(2, presenters.Count);
            Assert.Collection(
                presenters.SelectMany(static presenter => presenter.Messages),
                first =>
                {
                    Assert.Equal(OverlayDeviceKind.Output, first.kind);
                    Assert.Equal("Connected output device", first.header);
                    Assert.Equal("Desk Speakers", first.deviceName);
                },
                second =>
                {
                    Assert.Equal(OverlayDeviceKind.Error, second.kind);
                    Assert.Equal("Disconnected input device", second.header);
                    Assert.Equal("Desk Mic", second.deviceName);
                });
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenConnectedOutputSuppressed_StillShowsDisconnectedInputOverlay()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" }
                        ]
                    },
                    Input = new DeviceSwitchingInputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "in-1", Name = "Desk Mic" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [new CycleDevice { Id = "in-1", Name = "Desk Mic" }];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            harness.ViewModel.SuppressConnectedHotplugOverlay(output: true, suppressMs: 10_000);
            currentOutputDevices = [new CycleDevice { Id = "out-1", Name = "Desk Speakers" }];
            currentInputDevices = [];

            coordinator.ProcessPostRefresh();

            RecordingOverlayPresenter presenter = Assert.Single(presenters);
            var (kind, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(OverlayDeviceKind.Error, kind);
            Assert.Equal("Disconnected input device", header);
            Assert.Equal("Desk Mic", deviceName);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenMultipleConfiguredOutputsConnect_ShowsOnePerLineBullets()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" },
                            new CycleDevice { Id = "out-2", Name = "Headphones" },
                            new CycleDevice { Id = "out-3", Name = "HDMI Monitor" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentOutputDevices =
            [
                new CycleDevice { Id = "out-1", Name = "Desk Speakers" },
                new CycleDevice { Id = "out-2", Name = "Headphones" },
                new CycleDevice { Id = "out-3", Name = "HDMI Monitor" }
            ];

            coordinator.ProcessPostRefresh();

            RecordingOverlayPresenter presenter = Assert.Single(presenters);
            var (kind, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(OverlayDeviceKind.Output, kind);
            Assert.Equal("Connected output devices", header);
            Assert.Equal("- Desk Speakers\n- HDMI Monitor\n- Headphones", deviceName);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenConfiguredSelectionChangesForAlreadyConnectedOutputs_DoesNotShowConnectedOverlay()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });

            harness.SetCachedSettings(new Settings());

            List<CycleDevice> currentOutputDevices =
            [
                new CycleDevice { Id = "out-1", Name = "Desk Speakers" },
                new CycleDevice { Id = "out-2", Name = "Headphones" }
            ];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" },
                            new CycleDevice { Id = "out-2", Name = "Headphones" }
                        ]
                    }
                }
            });

            coordinator.ProcessPostRefresh();

            Assert.Empty(presenters);
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenOutputConnectsAndDisconnects_ShowsSeparateStackedCards()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" },
                            new CycleDevice { Id = "out-2", Name = "Headphones" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [new CycleDevice { Id = "out-1", Name = "Desk Speakers" }];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentOutputDevices = [new CycleDevice { Id = "out-2", Name = "Headphones" }];

            coordinator.ProcessPostRefresh();

            Assert.Equal(2, presenters.Count);
            Assert.Collection(
                presenters.SelectMany(static presenter => presenter.Messages),
                first =>
                {
                    Assert.Equal(OverlayDeviceKind.Output, first.kind);
                    Assert.Equal("Connected output device", first.header);
                    Assert.Equal("Headphones", first.deviceName);
                },
                second =>
                {
                    Assert.Equal(OverlayDeviceKind.Error, second.kind);
                    Assert.Equal("Disconnected output device", second.header);
                    Assert.Equal("Desk Speakers", second.deviceName);
                });
        });
    }

    [Fact]
    public void ProcessPostRefresh_WhenInputConnectsAndDisconnects_ShowsSeparateStackedCards()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Input = new DeviceSwitchingInputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "in-1", Name = "Desk Mic" },
                            new CycleDevice { Id = "in-2", Name = "USB Mic" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [new CycleDevice { Id = "in-1", Name = "Desk Mic" }];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentInputDevices = [new CycleDevice { Id = "in-2", Name = "USB Mic" }];

            coordinator.ProcessPostRefresh();

            Assert.Equal(2, presenters.Count);
            Assert.Collection(
                presenters.SelectMany(static presenter => presenter.Messages),
                first =>
                {
                    Assert.Equal(OverlayDeviceKind.Input, first.kind);
                    Assert.Equal("Connected input device", first.header);
                    Assert.Equal("USB Mic", first.deviceName);
                },
                second =>
                {
                    Assert.Equal(OverlayDeviceKind.Error, second.kind);
                    Assert.Equal("Disconnected input device", second.header);
                    Assert.Equal("Desk Mic", second.deviceName);
                });
        });
    }

    [Fact]
    public void ProcessPostRefresh_PrefersCurrentSettingsOverPersistedSettings_WhenEvaluatingConfiguredDevices()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });

            harness.SettingsService.SaveSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-persisted", Name = "Persisted Speakers" }
                        ]
                    }
                }
            });
            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-current", Name = "Current Speakers" }
                        ]
                    }
                }
            });
            List<CycleDevice> currentOutputDevices = [];
            List<CycleDevice> currentInputDevices = [];

            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));
            coordinator.CaptureInitialSnapshot();

            currentOutputDevices = [new CycleDevice { Id = "out-current", Name = "Current Speakers" }];

            coordinator.ProcessPostRefresh();

            RecordingOverlayPresenter presenter = Assert.Single(presenters);
            var (kind, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(OverlayDeviceKind.Output, kind);
            Assert.Equal("Connected output device", header);
            Assert.Equal("Current Speakers", deviceName);
        });
    }

    [Fact]
    public void ProcessPostRefresh_UsesInjectedFreshSnapshot_WhenKnownDeviceListsRemainStale()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var presenters = new List<RecordingOverlayPresenter>();
            var overlayService = new OverlayService(
                dispatch: action => action(),
                presenterFactory: _ =>
                {
                    var presenter = new RecordingOverlayPresenter();
                    presenters.Add(presenter);
                    return presenter;
                });

            harness.SetCachedSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" }
                        ]
                    }
                }
            });

            SetKnownActiveDeviceInfos(
                harness.ViewModel,
                [new CycleDevice { Id = "out-1", Name = "Desk Speakers" }],
                []);

            List<CycleDevice> currentOutputDevices = [new CycleDevice { Id = "out-1", Name = "Desk Speakers" }];
            List<CycleDevice> currentInputDevices = [];
            var coordinator = new AppHotplugOverlayCoordinator(
                harness.SettingsService,
                harness.ViewModel,
                new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication),
                () => CloneSnapshot(currentOutputDevices, currentInputDevices));

            coordinator.CaptureInitialSnapshot();

            currentOutputDevices = [];

            coordinator.ProcessPostRefresh();

            RecordingOverlayPresenter presenter = Assert.Single(presenters);
            var (kind, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(OverlayDeviceKind.Error, kind);
            Assert.Equal("Disconnected output device", header);
            Assert.Equal("Desk Speakers", deviceName);
        });
    }

    [Fact]
    public void CaptureInitialSnapshot_UsesKnownDeviceListsWithoutForcingEnumeration()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppHotplugOverlayCoordinatorTests));
            var cachedSettings = new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        CycleDevices =
                        [
                            new CycleDevice { Id = "out-1", Name = "Desk Speakers" }
                        ]
                    }
                }
            };
            AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell(cachedSettings: cachedSettings);
            SetKnownActiveDeviceInfos(
                viewModel,
                [new CycleDevice { Id = "out-1", Name = "Desk Speakers" }],
                []);

            var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
            var overlayService = new OverlayService(dispatch: action => action(), presenterFactory: _ => new RecordingOverlayPresenter());
            var coordinator = new AppHotplugOverlayCoordinator(settingsService, viewModel, new Lazy<OverlayService>(() => overlayService, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

            var exception = Record.Exception(coordinator.CaptureInitialSnapshot);

            Assert.Null(exception);
        });
    }

    private static void SetKnownActiveDeviceInfos(
        AppViewModel viewModel,
        IReadOnlyList<CycleDevice> outputDevices,
        IReadOnlyList<CycleDevice> inputDevices)
    {
        List<CycleDevice> knownOutputs = TestPrivateAccess.GetField<List<CycleDevice>>(viewModel, "_outputDevices");
        List<CycleDevice> knownInputs = TestPrivateAccess.GetField<List<CycleDevice>>(viewModel, "_inputDevices");

        knownOutputs.Clear();
        knownOutputs.AddRange(outputDevices.Select(CloneDevice));
        knownInputs.Clear();
        knownInputs.AddRange(inputDevices.Select(CloneDevice));
    }

    private static (List<CycleDevice> OutputDevices, List<CycleDevice> InputDevices) CloneSnapshot(
        IReadOnlyList<CycleDevice> outputDevices,
        IReadOnlyList<CycleDevice> inputDevices)
    {
        return
            ([.. outputDevices.Select(CloneDevice)],
             [.. inputDevices.Select(CloneDevice)]);
    }

    private static CycleDevice CloneDevice(CycleDevice device)
    {
        return new CycleDevice
        {
            Id = device.Id,
            Name = device.Name,
            DisplayOrder = device.DisplayOrder,
        };
    }
}

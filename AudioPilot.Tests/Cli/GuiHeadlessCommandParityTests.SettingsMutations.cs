using System.Reflection;
using System.Runtime.CompilerServices;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Coordinators;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Cli;

public sealed partial class GuiHeadlessCommandParityTests
{
    [Fact]
    public Task ConfigWritePreservesUnrelatedGuiDraftsAndRebasesCleanFields() => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        bool originalRedaction = LogPrivacy.IsRedactionEnabled;
        LogLevel originalLogLevel = Logger.Instance.MinimumLevel;
        await using (var scope = new ParityScope())
        {
            var vm = scope.Harness.ViewModel;
            bool sounds = vm.SettingsPlayAppSoundsDraft;
            vm.SettingsPlayAppSoundsDraft = !sounds;
            vm.SettingsOverlayDurationSecondsDraft = "unfinished";
            vm.Theme = AppTheme.Dark;
            vm.SettingsLanguageDraft = "es";
            vm.Hotkey.LoadFromString("Ctrl+Alt+X");
            var device = new CycleDevice { Id = "draft-device", Name = "Draft device" };
            vm.OutputCycleDevices.Add(device);
            var routine = new AudioRoutine { Id = "draft-routine", Name = "Draft routine", MasterVolumePercent = 25 };
            vm.Routines.Add(routine);

            var (Updated, Error) = await scope.Gui.SetConfigAsync("redact-log-content", "false");

            Assert.True(Updated, Error);
            Assert.False(vm.SettingsRedactLogContentDraft);
            Assert.False(LogPrivacy.IsRedactionEnabled);
            Assert.True((await scope.Gui.SetConfigAsync("language", "en")).Updated);
            Assert.True((await scope.Gui.SetConfigAsync("theme", "Light")).Updated);
            Assert.Equal(!sounds, vm.SettingsPlayAppSoundsDraft);
            Assert.Equal("unfinished", vm.SettingsOverlayDurationSecondsDraft);
            Assert.Equal(AppTheme.Dark, vm.Theme);
            Assert.Equal("es", vm.SettingsLanguageDraft);
            Assert.Equal("Ctrl+Alt+X", vm.Hotkey.ToHotkeyString().Replace(" ", ""));
            Assert.Same(device, Assert.Single(vm.OutputCycleDevices));
            Assert.Same(routine, Assert.Single(vm.Routines));
            Settings saved = scope.Harness.SettingsService.LoadSettings();
            Assert.Equal(sounds, saved.App.PlayAppSounds);
            Assert.False(saved.Diagnostics.RedactLogContent);
            Assert.Equal("en", saved.App.Language);
            Assert.Equal(AppTheme.Light, saved.App.Theme);
            Assert.Empty(saved.DeviceSwitching.Output.CycleDevices);
            Assert.Empty(saved.Routines.Items);
        }
        Assert.Equal(originalRedaction, LogPrivacy.IsRedactionEnabled);
        Assert.Equal(originalLogLevel, Logger.Instance.MinimumLevel);
    });

    [Theory]
    [InlineData("output")]
    [InlineData("input")]
    [InlineData("routines")]
    public Task ImportCannotOverwriteAnUnsavedGuiList(string kind) => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        var vm = scope.Harness.ViewModel;
        var imported = scope.Harness.SettingsService.LoadSettings();
        if (kind == "routines")
        {
            vm.Routines.Add(new() { Id = "local", Name = "Local", MasterVolumePercent = 25 });
            imported.Routines.Items.Add(new() { Id = "remote", Name = "Remote", MasterVolumePercent = 30 });
        }
        else
        {
            var draft = kind == "output" ? vm.OutputCycleDevices : vm.InputCycleDevices;
            draft.Add(new() { Id = "local", Name = "Local" });
            (kind == "output" ? imported.DeviceSwitching.Output.CycleDevices : imported.DeviceSwitching.Input.CycleDevices).Add(new() { Id = "remote", Name = "Remote" });
        }
        string before = File.ReadAllText(scope.GuiSettingsPath);
        string path = Path.Combine(Path.GetDirectoryName(scope.GuiSettingsPath)!, "import.json");
        SettingsTransferService.ExportSettings(imported, path);

        var (Success, Output) = await scope.Gui.ImportConfigAsync(path, true, false, true, false);

        Assert.False(Success);
        Assert.Contains("gui-draft-conflict", Output);
        Assert.Contains("Save or discard", Output);
        Assert.Equal(before, File.ReadAllText(scope.GuiSettingsPath));
        Assert.Equal("local", kind == "routines" ? Assert.Single(vm.Routines).Id
            : Assert.Single(kind == "output" ? vm.OutputCycleDevices : vm.InputCycleDevices).Id);
    });

    [Theory]
    [InlineData(CliAction.RoutineEnable)]
    [InlineData(CliAction.RoutineDelete)]
    [InlineData(CliAction.CycleRemoveOutput)]
    [InlineData(CliAction.ConfigImport)]
    public Task CanceledQueuedMutationSettlesBeforeTheWriteGateOpens(CliAction action) => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        Settings settings = scope.Harness.SettingsService.LoadSettings();
        settings.Routines.Items.Add(new() { Id = "probe", Name = "Probe", Enabled = false, MasterVolumePercent = 25 });
        settings.DeviceSwitching.Output.CycleDevices.Add(new() { Id = "probe", Name = "Probe" });
        scope.Harness.SettingsService.SaveSettings(settings);
        scope.Harness.SetCachedSettings(settings);
        scope.Harness.ViewModel.Routines.Add(settings.Routines.Items[0].Clone());
        string path = Path.Combine(Path.GetDirectoryName(scope.GuiSettingsPath)!, "import.json");
        SettingsTransferService.ExportSettings(settings, path);
        string before = File.ReadAllText(scope.GuiSettingsPath);
        var gate = TestPrivateAccess.GetField<SemaphoreSlim>(scope.Harness.ViewModel, "_settingsWriteSemaphore");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var pending = CliCommandExecutor.ExecuteAsync(new CliCommand
            {
                Action = action,
                Key = action == CliAction.ConfigImport ? path : "probe",
                JsonOutput = true,
            }, scope.Gui);
            Assert.False(pending.IsCompleted);
            scope.RequestCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(before, File.ReadAllText(scope.GuiSettingsPath));
            Assert.False(scope.Harness.SettingsService.LoadSettings().Routines.Items[0].Enabled);
        }
        finally { gate.Release(); }
    });

    [Fact]
    public Task StartupCommandWaitsForPendingApplyAndItsChoicePersists() => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int registrations = 0;
        AppViewModel.TryApplyStartupChangeOverrideForTests = (_, _) =>
        {
            if (Interlocked.Increment(ref registrations) == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Apply was not released.");
            }
            return true;
        };
        Task? apply = null;
        Task<bool>? startup = null;
        try
        {
            scope.Harness.ViewModel.SettingsRunAtStartupDraft = true;
            apply = scope.Harness.ViewModel.ApplySettingsForTestsAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            startup = scope.Gui.SetStartupEnabledAsync(false);
            Assert.False(startup.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref registrations));
            release.Set();
            await apply.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(await startup.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(scope.Harness.SettingsService.LoadSettings().Startup.Enabled);
            Assert.False(scope.Harness.ViewModel.RunAtStartup);
            Assert.False(scope.Harness.ViewModel.SettingsRunAtStartupDraft);
            Assert.Equal(2, registrations);
        }
        finally
        {
            release.Set();
            try { if (apply != null) await apply; if (startup != null) await startup; }
            finally { AppViewModel.ResetTestHooks(); }
        }
    });

    [Theory]
    [InlineData(false, false, true, false, 50, false)]
    [InlineData(false, false, false, false, 0, true)]
    [InlineData(true, false, false, false, 50, true)]
    [InlineData(false, true, true, false, 50, true)]
    [InlineData(false, true, true, true, 50, true)]
    [InlineData(false, true, false, false, 0, false)]
    public Task EndpointVolumeHonorsMuteOwnership(bool deafen, bool hold, bool muted, bool driverClearsMute, float percent, bool expectedMute) => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        var endpoint = (AudioEndpointVolume)RuntimeHelpers.GetUninitializedObject(typeof(AudioEndpointVolume));
        FieldInfo field = typeof(AudioEndpointVolume).GetField("audioEndPointVolume", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var native = (AppAudioCommandCoordinatorTests.EndpointVolumeProxy)DispatchProxy.Create(field.FieldType, typeof(AppAudioCommandCoordinatorTests.EndpointVolumeProxy));
        native.Muted = muted;
        native.DriverClearsMute = driverClearsMute;
        field.SetValue(endpoint, native);
        var device = (MMDevice)RuntimeHelpers.GetUninitializedObject(typeof(MMDevice));
        typeof(MMDevice).GetField("audioEndpointVolume", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(device, endpoint);
        TestPrivateAccess.SetField(scope.Harness.ViewModel, "_deafenBackingField", deafen);
        scope.Harness.ViewModel.IsMicrophoneHoldControlling = () => hold;
        try
        {
            var (Success, Percent, Muted) = scope.Harness.ViewModel.ApplyCliEndpointVolume(device, playback: !hold, percent);
            Assert.True(Success);
            Assert.Equal(percent, Percent);
            Assert.Equal(percent / 100f, native.Level);
            Assert.Equal(expectedMute, Muted);
            Assert.Equal(expectedMute, native.Muted);
        }
        finally
        {
            TestPrivateAccess.SetField(scope.Harness.ViewModel, "_deafenBackingField", false);
            scope.Harness.ViewModel.IsMicrophoneHoldControlling = null;
        }
    });
}

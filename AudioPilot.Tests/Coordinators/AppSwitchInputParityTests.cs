using System.Reflection;
using System.Runtime.CompilerServices;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

[Collection("RuntimeTuningConfigIsolation")]
public sealed class AppSwitchInputParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputRetry_PreservesOptions_AndHonorsSupersession(bool superseded)
    {
        using var logger = new TestLoggerScope(nameof(AppSwitchInputParityTests), "input-retry.log", LogLevel.Info);
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        using var coordinator = new AppSwitchCommandCoordinator(null!, overlay, logger.Logger,
            new BluetoothReconnectCoordinator(new FakeBluetoothReconnectService(), logger.Logger));
        AppSwitchIntentTracker tracker = TestPrivateAccess.GetField<AppSwitchIntentTracker>(coordinator, "_inputIntentTracker");
        AppSwitchRequestCoordinator requests = TestPrivateAccess.GetField<AppSwitchRequestCoordinator>(coordinator, "_requestCoordinator");
        tracker.Begin();
        RuntimeTuningConfig.InputSwitchDebounceMs = 50;
        typeof(AppSwitchCommandCoordinator).GetMethod("TryQueueInputCoalescedRetry", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(
            coordinator, [new List<CycleDevice> { new() { Id = "input", Name = "Microphone" } }, false, false,
                new BluetoothReconnectOptions(false, 0, 0, 0, false), AppSwitchRequestRejectionReason.InProgress, "retry"]);
        if (superseded) tracker.Begin();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (TestPrivateAccess.GetField<int>(requests, "_pendingInputRetryQueued") != 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        string log = logger.DisposeAndReadLogText();
        if (superseded) Assert.DoesNotContain(AppConstants.Audio.LogEvents.InputSwitch.Start, log);
        else Assert.Contains("preserveAudioLevels=False", log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredInputSuccess_ShowsCompletionFeedback(bool alreadyActive)
    {
        using var logger = new TestLoggerScope(nameof(AppSwitchInputParityTests), "input-deferred.log");
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var audio = (AudioDeviceService)RuntimeHelpers.GetUninitializedObject(typeof(AudioDeviceService));
        var coordinator = new AppSwitchDeferredAutoSwitchCoordinator(audio, overlay, logger.Logger, (_, _) => { });
        var callbacks = (DeferredAutoSwitchCallbacks)typeof(AppSwitchDeferredAutoSwitchCoordinator)
            .GetMethod("CreateDeferredInputAutoSwitchCallbacks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(coordinator, [(Func<string, string, (bool, string)>)((_, name) => (true, name)), false])!;

        (alreadyActive ? callbacks.OnAlreadyActive : callbacks.OnSwitchSuccess)("input-deferred", "Headset microphone");

        Assert.Contains(presenter.Messages, message => message.header == "Switched input device" && message.deviceName == "Headset microphone");
    }

    [Fact]
    public void DeferredInput_DoesNotQueueSupersededIntent()
    {
        using var logger = new TestLoggerScope(nameof(AppSwitchInputParityTests), "input-deferred-cancel.log");
        var coordinator = new AppSwitchDeferredAutoSwitchCoordinator(null!, null!, logger.Logger, (_, _) => { });
        coordinator.TryScheduleInputAutoSwitch("cancelled", [], "input", "Microphone", false,
            (_, name) => (false, name), () => false,
            token => CancellationTokenSource.CreateLinkedTokenSource(token), new CancellationToken(true));

        Assert.Equal(0, TestPrivateAccess.GetField<int>(coordinator, "_deferredInputSwitchInFlight"));
    }
}

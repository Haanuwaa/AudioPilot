using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("stop")]
    public void DeviceReferences_TrackFreshInventoryWorkUntilItFinishesAndHonorShutdown(string outcome)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            AppViewModel model = harness.ViewModel;
            Settings settings = BuildCachedSettings();
            settings.Miscellaneous.DeviceReferenceFileMode = DeviceReferenceFileMode.Plaintext;
            harness.SetCachedSettings(settings);
            SetKnownActiveDeviceInfos(model, [new() { Id = "stale-ui", Name = "Old dropdown entry" }], []);
            int uiThread = Environment.CurrentManagedThreadId;
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<(List<CycleDevice>, List<CycleDevice>)>(TaskCreationOptions.RunContinuationsAsynchronously);
            MethodInfo queue = typeof(AppViewModel).GetMethod("RunBackgroundWork", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var generator = new DeviceReferenceGenerationCoordinator(
                work => queue.Invoke(model, [work, "device-reference-test"]),
                _ => { started.TrySetResult(Environment.CurrentManagedThreadId); return pending.Task; },
                () => model.CurrentSettings!.Miscellaneous.DeviceReferenceFileMode,
                (outputs, inputs, hashed) => harness.SettingsService.GenerateDeviceReferenceFile(outputs, inputs, hashed),
                TestPrivateAccess.GetField<SemaphoreSlim>(model, "_settingsWriteSemaphore"), model.IsCleaningUpForTests);
            TestPrivateAccess.SetField(model, "_deviceReferenceGeneration", generator);

            typeof(AppViewModel).GetMethod("GenerateDeviceReferenceFile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
            TestPrivateAccess.RunTaskOnDispatcher(started.Task);
            Assert.NotEqual(uiThread, started.Task.Result);
            var tracked = TestPrivateAccess.GetField<ConcurrentDictionary<int, Task>>(model, "_backgroundTasks");
            Assert.Contains(tracked.Values, task => !task.IsCompleted);
            string path = Path.Combine(_workspace.PrimaryDir, "DEVICES.txt");
            Assert.False(File.Exists(path));

            if (outcome == "cancel") TestPrivateAccess.GetField<CancellationTokenSource>(model, "_backgroundWorkCts").Cancel();
            else if (outcome == "stop") TestPrivateAccess.SetField(model, "_isCleaningUp", true);
            Assert.Contains(tracked.Values, task => !task.IsCompleted);
            pending.SetResult(([new() { Id = "fresh-endpoint", Name = "Fresh device", StableId = " Opaque " }], []));
            WaitForQueuedBackgroundTasks(model);
            Assert.Equal(outcome == "success", File.Exists(path));
            if (outcome == "success")
            {
                string text = File.ReadAllText(path);
                Assert.Contains("fresh-endpoint | Fresh device", text);
                Assert.Contains("  Stable ID:  Opaque ", text);
                Assert.DoesNotContain("stale-ui", text);
            }
            if (outcome == "stop") TestPrivateAccess.SetField(model, "_isCleaningUp", false);
        });
    }
}

using System.Windows.Threading;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliLanguageUpdatePreservesUnrelatedDraftsAndNewerLanguageEdits(bool newerLanguageEdit)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            var cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            var model = harness.ViewModel;
            model.SettingsPlayAppSoundsDraft = false;
            var device = new AudioPilot.Models.CycleDevice { Id = "draft-output", Name = "Draft device" };
            model.OutputCycleDevices.Add(device);

            TestPrivateAccess.RunTaskOnDispatcher(model.EnterSettingsWriteLockForTestsAsync());
            Task<(bool Updated, string? Error)> operation;
            try
            {
                operation = model.SetConfigFromCliAsync("language", "es", TestContext.Current.CancellationToken);
                Assert.False(operation.IsCompleted);
                if (newerLanguageEdit) model.SettingsLanguageDraft = "en";
            }
            finally { model.ReleaseSettingsWriteLockForTests(); }

            TestPrivateAccess.RunTaskOnDispatcher(operation);
            var (updated, error) = operation.GetAwaiter().GetResult();
            Assert.True(updated, error);
            Assert.Equal("es", harness.SettingsService.LoadSettings().Language);
            string expectedLanguage = newerLanguageEdit ? "en" : "es";
            Assert.Equal(expectedLanguage, model.SettingsLanguageDraft);
            Assert.Equal(expectedLanguage, model.Localization.EffectiveLanguage);
            Assert.False(model.SettingsPlayAppSoundsDraft);
            Assert.Same(device, model.OutputCycleDevices.Last());
            TestPrivateAccess.RunTaskOnDispatcher(model.WaitForQueuedBackgroundTasksForTestsAsync());
            TestPrivateAccess.RunTaskOnDispatcher(model.CleanupAsync());
        });
    }

    [Fact]
    public void ApplyPreservesANewerLanguageSelection_AndTheNextApplyPersistsIt()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var cached = BuildCachedSettings();
            harness.SetCachedSettings(cached);
            harness.SettingsService.SaveSettings(cached);
            var model = harness.ViewModel;
            model.SettingsLanguageDraft = "es";
            SemaphoreSlim gate = TestPrivateAccess.GetField<SemaphoreSlim>(model, "_settingsWriteSemaphore");
            gate.Wait(TestContext.Current.CancellationToken);
            Task operation;
            try
            {
                operation = TestPrivateAccess.InvokeNonPublicTask(model, "ApplySettingsAsync", false);
                Assert.True(model.IsApplyingSettings);
                Assert.False(operation.IsCompleted);
                model.SettingsLanguageDraft = "en";
            }
            finally { gate.Release(); }
            TestPrivateAccess.RunTaskOnDispatcher(operation);
            Assert.Equal("es", harness.SettingsService.LoadSettings().Language);
            Assert.Equal("en", model.SettingsLanguageDraft);
            Assert.Equal("en", model.Localization.EffectiveLanguage);
            TestPrivateAccess.RunTaskOnDispatcher(TestPrivateAccess.InvokeNonPublicTask(model, "ApplySettingsAsync", false));
            Assert.Equal("en", harness.SettingsService.LoadSettings().Language);
        });
    }
}

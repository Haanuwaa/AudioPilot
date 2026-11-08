using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpdatePreparation_FlushesAutosaveButPreservesManualUnsavedEdits(bool autoSave)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var settings = new Settings { Theme = AppTheme.Dark };
            settings.DeviceSwitching.Output.HotkeysEnabled = false;
            settings.DeviceSwitching.Input.HotkeysEnabled = false;
            settings.Miscellaneous.AutoSaveEnabled = autoSave;
            harness.SetCachedSettings(settings);
            harness.SettingsService.SaveSettings(settings);
            harness.ViewModel.Theme = AppTheme.Light;
            harness.ViewModel.SetUpdatePreparationBlocked(true);
            try
            {
                Task<bool> flush = harness.ViewModel.FlushForUpdateAsync(TestContext.Current.CancellationToken);
                TestPrivateAccess.RunTaskOnDispatcher(flush);
                bool saved = flush.GetAwaiter().GetResult();
                Assert.Equal(autoSave, saved);
                Assert.Equal(autoSave ? AppTheme.Light : AppTheme.Dark, harness.SettingsService.LoadSettings().Theme);
                Assert.Equal(!autoSave, harness.ViewModel.HasPendingUpdateEdits);
            }
            finally { harness.ViewModel.SetUpdatePreparationBlocked(false); }
        });
    }

    [Fact]
    public void UpdatePreparation_FailedSaveKeepsTheDraftAndDoesNotAuthorizeRestart()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            var settings = new Settings { Theme = AppTheme.Dark };
            settings.DeviceSwitching.Output.HotkeysEnabled = false;
            settings.DeviceSwitching.Input.HotkeysEnabled = false;
            settings.Miscellaneous.AutoSaveEnabled = true;
            harness.SetCachedSettings(settings);
            harness.SettingsService.SaveSettings(settings);
            string primary = harness.SettingsService.GetSettingsPath();
            harness.SettingsService.OverrideWriteTargetsForTests(_workspace.PrimaryDir, _workspace.FallbackDir);
            harness.ViewModel.Theme = AppTheme.Light;
            harness.ViewModel.SetUpdatePreparationBlocked(true);
            try
            {
                Task<bool> flush = harness.ViewModel.FlushForUpdateAsync(TestContext.Current.CancellationToken);
                TestPrivateAccess.RunTaskOnDispatcher(flush);
                Assert.False(flush.GetAwaiter().GetResult());
                Assert.True(harness.ViewModel.HasPendingUpdateEdits);
                Assert.Equal(AppTheme.Light, harness.ViewModel.Theme);
            }
            finally
            {
                harness.ViewModel.SetUpdatePreparationBlocked(false);
                harness.SettingsService.OverrideWriteTargetsForTests(primary, System.IO.Path.Combine(_workspace.FallbackDir, "settings.json"));
            }
        });
    }
}

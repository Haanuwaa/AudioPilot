using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsSavePatchTests
{
    [Fact]
    public void SavingSwitchOrderPreservesPickerPreferenceChangedAfterEditStarted()
    {
        var original = new Settings();
        Settings edited = original.Clone();
        edited.DeviceSwitching.Output.CycleDevices.Add(new() { Id = "chosen", Name = "Chosen" });
        SettingsSavePatch patch = SettingsSavePatch.Capture(original, edited);
        Settings latest = original.Clone();
        latest.DeviceSwitching.PickerShowAllAvailableDevices = true;

        Settings merged = patch.Apply(latest);

        Assert.True(merged.DeviceSwitching.PickerShowAllAvailableDevices);
        Assert.Equal("chosen", Assert.Single(merged.DeviceSwitching.Output.CycleDevices).Id);
    }

    [Fact]
    public void CaptureDetachesEditsAndApplyPreservesUnrelatedLatestFields()
    {
        var original = new Settings();
        Settings edited = original.Clone();
        edited.Overlay.DurationSeconds = 4;
        edited.App.Language = "es";
        edited.DeviceSwitching.Output.CycleDevices = [new() { Id = "chosen", Name = "Chosen" }];
        SettingsSavePatch patch = SettingsSavePatch.Capture(original, edited);
        edited.Overlay.DurationSeconds = 9;
        edited.DeviceSwitching.Output.CycleDevices.Clear();
        Settings latest = original.Clone();
        latest.Overlay.Enabled = false;
        latest.App.Theme = AppTheme.Dark;
        latest.Startup.UseTaskScheduler = true;
        latest.Updates.CheckAutomatically = true;
        latest.Diagnostics.RedactLogContent = false;
        latest.Routines.DefaultTimeZoneId = "UTC";
        latest.AdvancedTuning.OutputSwitchDebounceMs = 333;
        latest.DeviceSwitching.Input.CycleDevices = [new() { Id = "new-input", Name = "New input" }];

        Settings merged = patch.Apply(latest);

        Assert.Equal(4, merged.Overlay.DurationSeconds);
        Assert.False(merged.Overlay.Enabled);
        Assert.Equal("es", merged.App.Language);
        Assert.Equal(AppTheme.Dark, merged.App.Theme);
        Assert.True(merged.Startup.UseTaskScheduler);
        Assert.True(merged.Updates.CheckAutomatically);
        Assert.False(merged.Diagnostics.RedactLogContent);
        Assert.Equal("UTC", merged.Routines.DefaultTimeZoneId);
        Assert.Equal(333, merged.AdvancedTuning.OutputSwitchDebounceMs);
        Assert.Equal("chosen", Assert.Single(merged.DeviceSwitching.Output.CycleDevices).Id);
        Assert.Equal("new-input", Assert.Single(merged.DeviceSwitching.Input.CycleDevices).Id);
        Assert.Empty(latest.DeviceSwitching.Output.CycleDevices);
        merged.DeviceSwitching.Output.CycleDevices.Clear();
        Assert.Single(patch.Apply(latest).DeviceSwitching.Output.CycleDevices);
    }

    [Fact]
    public void ExplicitCollectionRemovalAndSameFieldEditWinWithoutChangingLatest()
    {
        var original = new Settings { App = new() { Theme = AppTheme.Light } };
        original.Routines.Items = [new() { Id = "old", Name = "Old" }];
        Settings edited = original.Clone();
        edited.App.Theme = AppTheme.Dark;
        edited.Routines.Items.Clear();
        SettingsSavePatch patch = SettingsSavePatch.Capture(original, edited);
        Settings latest = original.Clone();
        latest.App.Theme = AppTheme.System;
        latest.Routines.Items.Add(new() { Id = "other", Name = "Other" });

        Settings merged = patch.Apply(latest);

        Assert.Equal(AppTheme.Dark, merged.App.Theme);
        Assert.Empty(merged.Routines.Items);
        Assert.Equal(2, latest.Routines.Items.Count);
        Assert.Equal(AppTheme.System, latest.App.Theme);
    }

    [Fact]
    public void EmptyPatchApplyReturnsDetachedCloneOfLatest()
    {
        var original = new Settings { App = new() { Theme = AppTheme.Dark } };
        SettingsSavePatch patch = SettingsSavePatch.Capture(original, original.Clone());
        Settings latest = original.Clone();
        latest.App.Language = "fr";
        latest.DeviceSwitching.Output.CycleDevices.Add(new() { Id = "spk", Name = "Speakers" });

        Settings merged = patch.Apply(latest);

        Assert.NotSame(latest, merged);
        Assert.Equal("fr", merged.App.Language);
        Assert.Single(merged.DeviceSwitching.Output.CycleDevices);
        merged.DeviceSwitching.Output.CycleDevices.Clear();
        Assert.Single(latest.DeviceSwitching.Output.CycleDevices);
    }
}

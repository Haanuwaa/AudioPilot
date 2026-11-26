using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.ViewModels;

public sealed class ListManagerProjectionTests
{
    [Fact]
    public void DeviceViews_FlagOldAliasWhenExactEndpointIsAlsoConfigured()
    {
        CycleDevice old = new() { Id = "old", Name = "Old name", StableId = "stable" };
        CycleDevice current = new() { Id = "new", Name = "Speakers", StableId = "stable" };
        var inventory = new ListManagerDeviceInventory([current],
            new Dictionary<Role, string> { [Role.Multimedia] = "new" }, []);

        var rows = ListManagerProjection.BuildDeviceRows([old, current], inventory);
        Assert.Equal(2, rows.Length);
        Assert.Same(old, rows[0].Item);
        Assert.Equal("Duplicate", rows[0].Status);
        Assert.Equal(2, rows[0].DuplicateOfPosition);
        Assert.Null(rows[0].Connected);
        Assert.Equal("Default for Multimedia", rows[1].Subtitle);
        Assert.Same(current, Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Output, "", filter: 1, sort: 0)).Item);
        Assert.Empty(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Output, "", filter: 3, sort: 0));
    }

    [Fact]
    public void DeviceViews_PreserveConfiguredIdentityAndDistinguishUnknownFromDisconnected()
    {
        CycleDevice missing = new() { Id = "missing", Name = "Missing headphones" };
        CycleDevice remembered = new() { Id = "old", Name = "Old mic name", StableId = "stable" };
        CycleDevice reconnected = new() { Id = "new", Name = "USB mic", StableId = "stable" };
        CycleDevice available = new() { Id = "other", Name = "Another mic" };
        CycleDevice[] configured = [missing, remembered];
        var inventory = new ListManagerDeviceInventory([reconnected, available],
            new Dictionary<Role, string> { [Role.Communications] = "new", [Role.Console] = "other" }, [Role.Multimedia]);

        var rows = ListManagerProjection.BuildDeviceRows(configured, inventory);
        var connected = Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Input, "usb", filter: 1, sort: 0));
        Assert.Same(remembered, connected.Item);
        Assert.Equal(2, connected.Position);
        Assert.Equal("Default for Communications", connected.Subtitle);
        Assert.Same(available, Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Input, "", filter: 3, sort: 0)).Item);
        Assert.Same(missing, Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Input, "", filter: 2, sort: 0)).Item);

        var unknown = ListManagerProjection.BuildDeviceRows(configured, inventory: null);
        Assert.Equal(2, unknown.Length);
        Assert.All(unknown, row => Assert.Equal("Status unavailable", row.Status));
        Assert.Empty(ListManagerProjection.GetVisibleRows(unknown, ListManagerMode.Input, "", filter: 2, sort: 0));
    }

    [Fact]
    public void RoutineViews_SearchShortcutsAndTriggersWithoutChangingConfiguredPositions()
    {
        AudioRoutine first = new() { Id = "first", Name = "Zebra", Hotkey = "Ctrl+Alt+T", DisplayOrder = 1 };
        AudioRoutine second = new() { Id = "second", Name = "Alpha", ShowInTrayMenu = true, DisplayOrder = 2, HasConflict = true };
        ListManagerRow[] rows = [ListManagerProjection.BuildRoutineRow(first), ListManagerProjection.BuildRoutineRow(second)];

        var sorted = ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Routines, "", filter: 0, sort: 1);
        Assert.Equal([2, 1], sorted.Select(row => row.Position));
        Assert.Same(first, Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Routines, "ctrl+alt+t", filter: 0, sort: 0)).Item);
        Assert.Same(second, Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Routines, "tray", filter: 3, sort: 0)).Item);
        Assert.Same(second, Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Routines, "bandeja", filter: 3, sort: 0)).Item);
        Assert.Equal(1, first.DisplayOrder);
        Assert.Equal(2, second.DisplayOrder);
    }

    [Fact]
    public void RoutineViews_SearchSpanishTriggerSummariesWithOrWithoutAccents()
    {
        AudioRoutine routine = new()
        {
            Name = "Fixture",
            Triggers = [new() { Kind = RoutineTriggerKind.Application, ApplicationMode = ApplicationTriggerMode.AppLaunch, AppPath = @"C:\Fixture\player.exe" }],
        };
        ListManagerRow[] rows = [ListManagerProjection.BuildRoutineRow(routine)];

        Assert.Same(routine, Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Routines, "aplicación", 0, 0)).Item);
        Assert.Same(routine, Assert.Single(ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Routines, "APLICACION", 0, 0)).Item);
    }

    [Fact]
    public void RoutineViews_UseFeminineStatusLabelsWhenSwitchingToSpanish()
    {
        var localization = new LocalizationService("en");
        var enabled = ListManagerProjection.BuildRoutineRow(new AudioRoutine { Enabled = true });
        var disabled = ListManagerProjection.BuildRoutineRow(new AudioRoutine { Enabled = false });

        Assert.Equal("Enabled", enabled.FormatStatus(localization));
        Assert.Equal("Disabled", disabled.FormatStatus(localization));
        localization.SetLanguage("es");
        Assert.Equal("Activada", enabled.FormatStatus(localization));
        Assert.Equal("Desactivada", disabled.FormatStatus(localization));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeviceViews_IncompleteDiscoveryKeepsExactMatchesAndDefersRecovery(bool complete)
    {
        CycleDevice exact = new() { Id = "known", Name = "Known speaker" };
        CycleDevice remembered = new() { Id = "old", Name = "Headphones", StableId = "stable" };
        CycleDevice nameOnly = new() { Id = "old-name", Name = "Other headphones" };
        CycleDevice missing = new() { Id = "missing", Name = "Missing speaker" };
        CycleDevice reconnected = new() { Id = "replacement", Name = "New headphones", StableId = "stable" };
        CycleDevice sameName = new() { Id = "replacement-name", Name = nameOnly.Name };
        var inventory = new ListManagerDeviceInventory([exact, reconnected, sameName],
            new Dictionary<Role, string> { [Role.Multimedia] = exact.Id }, [], IsComplete: complete);

        var rows = ListManagerProjection.BuildDeviceRows([exact, remembered, nameOnly, missing], inventory);

        Assert.True(rows[0].Connected);
        Assert.Equal("Multimedia", rows[0].DefaultRoles);
        foreach (var row in rows.Skip(1).Take(2))
        {
            Assert.Equal(complete ? true : (bool?)null, row.Connected);
            Assert.Equal(complete ? "Connected" : "Status unavailable", row.Status);
        }
        Assert.Equal(complete ? false : (bool?)null, rows[3].Connected);
        Assert.Equal(complete ? 1 : 0,
            ListManagerProjection.GetVisibleRows(rows, ListManagerMode.Output, "", filter: 2, sort: 0).Length);
    }

    [Theory]
    [InlineData("routine-condition-network-disconnected", false)]
    [InlineData("routine-condition-network-connected", false)]
    [InlineData("routine-condition-outside-time-window", false)]
    [InlineData("routine-condition-device-not-default", false)]
    [InlineData("routine-target-app-not-running", false)]
    [InlineData(null, false)]
    [InlineData("routine-condition-invalid", true)]
    [InlineData("routine-condition-unavailable", true)]
    [InlineData("routine-target-app-unavailable", true)]
    public void RoutineViews_ExpectedSkipsStayQuietAndUnavailableChecksNeedAttention(string? code, bool needsAttention)
    {
        var routine = new AudioRoutine { LastRunState = RoutineLastRunState.Skipped, LastRunSkipCode = code };
        Assert.Equal(needsAttention, ListManagerProjection.BuildRoutineRow(routine).NeedsAttention);

        routine.LastRunState = RoutineLastRunState.Failed;
        Assert.True(ListManagerProjection.BuildRoutineRow(routine).NeedsAttention);
        routine.LastRunState = RoutineLastRunState.Succeeded;
        Assert.False(ListManagerProjection.BuildRoutineRow(routine).NeedsAttention);
        routine.HasConflict = true;
        Assert.True(ListManagerProjection.BuildRoutineRow(routine).NeedsAttention);
    }
}

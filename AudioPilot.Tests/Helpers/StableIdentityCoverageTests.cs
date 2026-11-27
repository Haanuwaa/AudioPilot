using System.Collections.ObjectModel;
using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Helpers;

public sealed class StableIdentityCoverageTests
{
    [Theory]
    [InlineData("opaque", true)]
    [InlineData("OPAQUE", false)]
    [InlineData(" opaque ", false)]
    public void AvailabilityPreservesOpaqueIdentity(string candidate, bool expected)
        => Assert.Equal(expected, new RoutineDeviceReference { Id = "old", StableId = "opaque" }.GetAvailability([new() { Id = "new", StableId = candidate }]));

    [Fact]
    public void CaseDistinctStableIdsAreNotAmbiguous()
        => Assert.True(new RoutineDeviceReference { Id = "old", StableId = "opaque" }.GetAvailability([new() { Id = "first", StableId = "opaque" }, new() { Id = "second", StableId = "OPAQUE" }]));

    [Theory]
    [InlineData("opaque", " opaque ")]
    [InlineData("opaque", "OPAQUE")]
    public void StatefulFingerprintPreservesOpaqueIdentity(string first, string second)
    {
        Assert.NotEqual(AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(new() { OutputDeviceStableId = first }),
            AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(new() { OutputDeviceStableId = second }));
        Assert.NotEqual(AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(new() { InputDeviceStableId = first }),
            AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(new() { InputDeviceStableId = second }));
    }

    [Theory]
    [InlineData("unique", 1)]
    [InlineData("case-distinct", -1)]
    [InlineData("ambiguous", -1)]
    [InlineData("name-only", -1)]
    [InlineData("exact", 2)]
    public void DropdownReconnectUsesOnlyExactOrUniqueStableIdentity(string scenario, int expected)
    {
        var remembered = new CycleDevice { Id = "old", Name = "Same", StableId = "opaque" };
        List<CycleDevice> available = [new() { Id = "new", Name = "Same", StableId = scenario == "case-distinct" ? "OPAQUE" : scenario == "name-only" ? null : "opaque" }];
        if (scenario is "ambiguous" or "exact") available.Add(new() { Id = scenario == "exact" ? "old" : "other", StableId = "opaque" });
        Assert.Equal(expected, AppViewModelDeviceCycleHelper.LoadAvailableDevices([], [], available, -1, "All Devices", true, remembered));
    }

    [Fact]
    public void MonitoringSelectionRecoversDuplicateNamesAndKeepsUnavailableStableIdentity()
    {
        ObservableCollection<CycleDevice> options = [];
        CycleDevice selected = AppViewModelListenMonitorOutputHelper.RefreshOptions(options,
            [new() { Id = "first", Name = "Headphones", StableId = "intended" }, new() { Id = "second", Name = "Headphones", StableId = "other" }],
            "old", "Headphones", currentSelectedStableId: "intended");
        Assert.Equal("first", selected.Id);
        Assert.Equal("intended", selected.StableId);
        selected = AppViewModelListenMonitorOutputHelper.RefreshOptions(options, [], selected.Id, selected.Name, currentSelectedStableId: selected.StableId);
        Assert.Equal("intended", selected.StableId);
        Assert.Equal("intended", options[1].StableId);
    }

    [Fact]
    public void MonitoringStableIdentitySurvivesSettingsAndCliWithoutNormalization()
    {
        var settings = new Settings();
        Assert.True(CliConfigManager.TrySet(settings, "listen-monitor-output-device-stable-id", " opaque ", out _));
        var cloned = AppSettingsWorkflowCoordinator.CloneSettings(settings);
        Assert.Equal(" opaque ", cloned.Hotkeys.Listen.MonitorOutputDeviceStableId);
        Assert.Equal(" opaque ", JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(cloned))!.Hotkeys.Listen.MonitorOutputDeviceStableId);
        Assert.True(CliConfigManager.TrySet(settings, "listen-monitor-output-device-id", "other", out _));
        Assert.Null(settings.Hotkeys.Listen.MonitorOutputDeviceStableId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WifiPermissionFeedbackRetainsNamesAndRedaction(bool json)
    {
        var scan = new WifiScanResult([], 5);
        string result = CliOutputFormatter.FormatNetworkList(["Private network"], json, true, scan.Warning);
        Assert.DoesNotContain("Private network", result, StringComparison.Ordinal);
        Assert.Contains("Location", result, StringComparison.Ordinal);
        if (json)
        {
            using var parsed = JsonDocument.Parse(result);
            JsonElement data = parsed.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetProperty("count").GetInt32());
            Assert.Single(data.GetProperty("warnings").EnumerateArray());
        }
        Assert.Null(new WifiScanResult([]).Warning);
        Assert.NotNull(new WifiScanResult([], 1055).Warning);
    }
}

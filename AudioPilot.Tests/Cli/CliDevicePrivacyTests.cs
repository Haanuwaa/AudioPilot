using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.Models;

namespace AudioPilot.Tests.Cli;

public sealed class CliDevicePrivacyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VolumeError_RedactsIdentifierAndMessage(bool jsonOutput)
    {
        string result = CliOutputFormatter.FormatVolumeError("master", "volume-get-failed",
            "Could not find device 'private-device'.", jsonOutput, "private-device", redactOutput: true);

        Assert.DoesNotContain("private-device", result, StringComparison.Ordinal);
        Assert.Contains("volume-get-failed", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SwitchPreview_PreservesContractAndHonorsRedaction(bool jsonOutput, bool redactOutput)
    {
        var target = new CycleDevice { Id = "private-target-id", Name = "Private headset" };

        string result = CliOutputFormatter.FormatSwitchPreview("output", "private-current-id", target, jsonOutput, redactOutput);

        Assert.Contains("switch-dry-run", result, StringComparison.Ordinal);
        if (redactOutput)
        {
            Assert.DoesNotContain(target.Id, result, StringComparison.Ordinal);
            Assert.DoesNotContain(target.Name, result, StringComparison.Ordinal);
            Assert.DoesNotContain("private-current-id", result, StringComparison.Ordinal);
            Assert.Contains("device-id[", result, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(target.Id, result, StringComparison.Ordinal);
            Assert.Contains(target.Name, result, StringComparison.Ordinal);
        }

        if (jsonOutput)
        {
            JsonObject data = (JsonObject)JsonNode.Parse(result)!["data"]!;
            Assert.True(data["dryRun"]!.GetValue<bool>());
            Assert.Equal("output", data["kind"]!.GetValue<string>());
            Assert.NotEqual(data["currentDeviceId"]!.GetValue<string>(), data["targetDeviceId"]!.GetValue<string>());
        }
    }
}

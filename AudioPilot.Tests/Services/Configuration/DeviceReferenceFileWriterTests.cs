using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class DeviceReferenceFileWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StableIds_AreOptionalRespectPrivacyAndTriggerRewrites(bool anonymizeIds)
    {
        using var workspace = new TestSettingsWorkspace(nameof(StableIds_AreOptionalRespectPrivacyAndTriggerRewrites));
        string path = Path.Combine(workspace.PrimaryDir, "DEVICES.txt");
        var writer = new DeviceReferenceFileWriter(File.WriteAllText);
        CycleDevice output = new() { Id = "output-endpoint", Name = "Speakers", StableId = " Opaque-Output " };
        CycleDevice input = new() { Id = "input-endpoint", Name = "Microphone", StableId = "Opaque-Input\r\nValue" };

        Assert.True(writer.WriteIfChanged(path, [output], [input], anonymizeIds));
        string text = File.ReadAllText(path);
        Assert.Equal(2, text.Split("  Stable ID:", StringSplitOptions.None).Length - 1);
        if (anonymizeIds)
        {
            Assert.DoesNotContain(output.Id, text);
            Assert.DoesNotContain(input.Id, text);
            Assert.DoesNotContain("Opaque", text);
            Assert.Equal(4, text.Split("sha256:", StringSplitOptions.None).Length - 1);
        }
        else
        {
            Assert.Contains("output-endpoint | Speakers" + Environment.NewLine + "  Stable ID:  Opaque-Output ", text);
            Assert.Contains("input-endpoint | Microphone" + Environment.NewLine + "  Stable ID: Opaque-Input  Value", text);
        }
        Assert.False(writer.WriteIfChanged(path, [output], [input], anonymizeIds));
        output.StableId = " Opaque-output ";
        Assert.True(writer.WriteIfChanged(path, [output], [input], anonymizeIds));
        Assert.NotEqual(text, File.ReadAllText(path));
        output.StableId = null;
        input.StableId = string.Empty;
        Assert.True(writer.WriteIfChanged(path, [output], [input], anonymizeIds));
        Assert.DoesNotContain("Stable ID:", File.ReadAllText(path));
    }

    [Fact]
    public void FailedWrite_IsRetriedAndDeletedExportIsRecreated()
    {
        using var workspace = new TestSettingsWorkspace(nameof(FailedWrite_IsRetriedAndDeletedExportIsRecreated));
        string path = Path.Combine(workspace.PrimaryDir, "DEVICES.txt");
        int writes = 0;
        var writer = new DeviceReferenceFileWriter((target, content) =>
        {
            if (++writes == 1) throw new IOException("Unavailable destination");
            File.WriteAllText(target, content);
        });
        CycleDevice[] devices = [new() { Id = "out-1", Name = "Speakers" }];

        Assert.Throws<IOException>(() => writer.WriteIfChanged(path, devices, [], false));
        Assert.True(writer.WriteIfChanged(path, devices, [], false));
        Assert.False(writer.WriteIfChanged(path, devices, [], false));
        File.Delete(path);
        Assert.True(writer.WriteIfChanged(path, devices, [], false));

        Assert.Equal(3, writes);
        Assert.Contains("out-1 | Speakers", File.ReadAllText(path));
    }

    [Fact]
    public void Export_ReordersConsistentlyButRewritesForNamesModesAndDestination()
    {
        using var workspace = new TestSettingsWorkspace(nameof(Export_ReordersConsistentlyButRewritesForNamesModesAndDestination));
        string path = Path.Combine(workspace.PrimaryDir, "DEVICES.txt");
        var writer = new DeviceReferenceFileWriter(File.WriteAllText);
        CycleDevice first = new() { Id = "out-1", Name = "Speakers" };
        CycleDevice second = new() { Id = "out-2", Name = "Headset" };

        Assert.True(writer.WriteIfChanged(path, [second, first], [], false));
        Assert.False(writer.WriteIfChanged(path, [first, second], [], false));
        first.Name = "Speakers\r\n[INPUT DEVICES]";
        Assert.True(writer.WriteIfChanged(path, [first, second], [], false));
        Assert.Contains("out-1 | Speakers  [INPUT DEVICES]", File.ReadAllText(path));
        Assert.True(writer.WriteIfChanged(path, [first, second], [], true));
        Assert.DoesNotContain("out-1", File.ReadAllText(path));
        Assert.Contains("sha256:", File.ReadAllText(path));
        Assert.True(writer.WriteIfChanged(Path.Combine(workspace.PrimaryDir, "OTHER.txt"), [first, second], [], true));
        first.Id = "out-3";
        Assert.True(writer.WriteIfChanged(path, [first, second], [], true));
    }
}

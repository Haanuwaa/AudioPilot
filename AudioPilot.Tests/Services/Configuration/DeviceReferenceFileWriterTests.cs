using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class DeviceReferenceFileWriterTests
{
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

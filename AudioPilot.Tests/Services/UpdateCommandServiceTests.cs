using System.Net;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Services.Updates;
using AudioPilot.Tests.Helpers;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Tests.Services;

public sealed class UpdateCommandServiceTests
{
    [Fact]
    public async Task HeadlessCheckRespectsServerCooldownAndPreservesKnownReleaseOnFailure()
    {
        int reads = 0;
        var known = new UpdateCheckSnapshot("1.0.0", "9.0.0", AppConstants.Links.RepositoryUrl + "/releases/latest", true, false, DateTimeOffset.UtcNow, null, null);
        var failed = await UpdateCommandService.CheckHeadlessAsync(known, TestContext.Current.CancellationToken,
            _ => { reads++; throw new ReleaseCheckException(HttpStatusCode.TooManyRequests, TimeSpan.FromDays(9)); },
            () => false, _ => { });
        Assert.Equal(known.LatestVersion, failed.LatestVersion);
        Assert.Equal(known.LastCheckedUtc, failed.LastCheckedUtc);
        Assert.Equal("update-check-failed", failed.ErrorCode);
        Assert.True(failed.RetryAfterUtc > DateTimeOffset.UtcNow.AddDays(8));
        Assert.Same(failed, await UpdateCommandService.CheckHeadlessAsync(failed, TestContext.Current.CancellationToken,
            _ => { reads++; throw new InvalidOperationException("A server cooldown must prevent this call."); }, () => false, _ => { }));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task PublicApplyCannotStartAHelperOrEnableTheDisabledPolicy()
    {
        Assert.Null(UpdateClientPolicy.Current);
        Assert.True(CliCommand.TryParse(["update", "apply", "--version", "9.0.0", "--approve", "--json"], out var command, out _));
        var result = await UpdateCommandService.ExecuteAsync(command, null, TestContext.Current.CancellationToken);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("public-updates-disabled", result.Output);
    }

    [Fact]
    public void SavedCheckFeedbackRejectsCorruptOrDuplicateState()
    {
        using var directory = new TestScopedDirectory(nameof(UpdateCommandServiceTests));
        string path = Path.Combine(directory.Root, "update-check.json");
        var state = new UpdateCheckSnapshot("1.0.0", "2.0.0", "https://github.com/Haanuwaa/AudioPilot/releases/latest", true, false, DateTimeOffset.UtcNow, null, null);
        UpdateCheckSnapshotStore.Save(state, Logger.Instance, path);
        Assert.Equal(state, UpdateCheckSnapshotStore.Read(path));
        File.WriteAllText(path, "{\"InstalledVersion\":\"1.0.0\",\"InstalledVersion\":\"2.0.0\"}");
        Assert.Null(UpdateCheckSnapshotStore.Read(path));
    }

    [Fact]
    public async Task ControlWireRejectsDuplicateApprovalBeforeDispatch()
    {
        await using var stream = new MemoryStream();
        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"Schema\":1,\"Operation\":\"apply\",\"Approve\":false,\"Approve\":true}");
        byte[] size = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
        stream.Write(size); stream.Write(body); stream.Position = 0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => UpdaterControlWire.ReadAsync<UpdaterControlRequest>(stream, TestContext.Current.CancellationToken));
    }
}

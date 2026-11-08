using System.Text;
using System.Text.Json;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater;

namespace AudioPilot.Tests.Services;

public sealed class UpdateClientPolicyTests
{
    [Fact]
    public void OrdinaryBuild_CannotAuthorizePilotLifecycleOrRuntimeStaging()
    {
        Assert.Null(UpdateClientPolicy.Current);
        string nonce = new('A', 64);
        var launch = new UpdateLaunch(1, nonce, "AudioPilot.Update." + nonce, 1, 1,
            Path.Combine(UpdateClientPolicy.RuntimeRoot, Guid.NewGuid().ToString("N"), "AudioPilot.Updater.exe"),
            Guid.NewGuid().ToString("N"), false, false, Pilot: true);
        Assert.Throws<InvalidDataException>(() => UpdateLifecycleWire.ParseLaunch(UpdateLifecycleWire.SerializeLaunch(launch), null));
        Assert.Throws<InvalidOperationException>(() => UpdateRuntimeStager.Stage(Path.GetTempPath(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"Schema\":1,\"Schema\":2}")]
    [InlineData("{\"Schema\":1,\"CandidateVersion\":\"1.0.2\",\"BootstrapRoot\":\"e30=\",\"PublisherSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"Origin\":\"https://example.invalid/\"}")]
    public void Policy_RejectsAmbiguousPropertiesAndFeedOverrides(string json) =>
        Assert.Throws<JsonException>(() => UpdateClientPolicy.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void StablePolicyRequiresExplicitPublicActivationAndRejectsReplacementPins()
    {
        byte[] root = Encoding.UTF8.GetBytes("temporary-public-root");
        string pin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(root));
        var policy = new UpdateClientPolicy(1, null, Convert.ToBase64String(root), new string('A', 64), "stable", pin);
        Assert.Equal(policy, UpdateClientPolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(policy)));
        var feed = new HttpsStableFeed(policy);
        Assert.Throws<InvalidOperationException>(() => feed.BaseUri);
        var enabled = policy with { PublicUpdatesEnabled = true };
        Assert.Equal(enabled, UpdateClientPolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(enabled)));
        Assert.True(enabled.IsEnabled);
        Assert.Equal(UpdateClientPolicy.StableOrigin, new HttpsStableFeed(enabled).BaseUri.AbsoluteUri);
        Assert.Throws<InvalidDataException>(() => policy.Target("portable"));
        foreach (var invalid in new[] { policy with { CandidateVersion = "1.0.2" },
            policy with { BootstrapSha256 = new string('B', 64) }, policy with { Channel = "preview" } })
        {
            Assert.Throws<InvalidDataException>(() => UpdateClientPolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid)));
            Assert.Throws<InvalidDataException>(() => new HttpsStableFeed(invalid));
        }
    }

    [Fact]
    public void Policy_RejectsAReplacementBootstrapEvenWithAValidPublisherDigest()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new UpdateClientPolicy(1, "1.0.2", "e30=", new string('A', 64)));
        Assert.Throws<InvalidDataException>(() => UpdateClientPolicy.Parse(bytes));
    }
}

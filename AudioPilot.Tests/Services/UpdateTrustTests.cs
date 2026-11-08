using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioPilot.Updater;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Tests.Services;

public sealed class UpdateTrustTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AudioPilot-update-trust-" + Guid.NewGuid().ToString("N"));
    private readonly TemporaryUpdateSigner _rootKey = new();
    private readonly TemporaryUpdateSigner _releaseKey = new();
    private readonly Clock _clock = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static UpdateSelection Selection => new("portable", "win-x64", "self-contained", "1.0.0", "1.0.0");

    [Theory]
    [InlineData("portable", "win-x86", "self-contained")]
    [InlineData("portable", "win-x64", "self-contained")]
    [InlineData("portable", "win-arm64", "self-contained")]
    [InlineData("portable", "win-x86", "framework-dependent")]
    [InlineData("portable", "win-x64", "framework-dependent")]
    [InlineData("portable", "win-arm64", "framework-dependent")]
    [InlineData("msi", "win-x86", "self-contained")]
    [InlineData("msi", "win-x64", "self-contained")]
    [InlineData("msi", "win-arm64", "self-contained")]
    public async Task SignedSelection_UsesTheExactVariantAndHoldsBytesAgainstReplacement(string kind, string architecture, string runtime)
    {
        var (workspace, client, _) = Initialize();
        string path = workspace.Resolve("candidate" + (kind == "msi" ? ".msi" : ".zip"));
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var artifact = LocalUpdateTrustFixture.Artifact(path, kind, architecture, runtime);
        byte[] signed = Sign(Release(artifact));
        using (var lease = await client.OpenPackageAsync(signed, new(kind, architecture, runtime, "1.0.0", "1.0.0"), path, Token))
        {
            Assert.Equal(artifact, lease.Authorization.Artifact);
            lease.Revalidate();
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [4, 3, 2, 1]));
            Assert.Throws<IOException>(() => File.Delete(path));
        }
        File.WriteAllBytes(path, [4, 3, 2, 1]);
        await Assert.ThrowsAsync<CryptographicException>(() => client.OpenPackageAsync(signed, new(kind, architecture, runtime, "1.0.0", "1.0.0"), path, Token));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("unknown-key")]
    [InlineData("root-role")]
    [InlineData("duplicate-envelope")]
    [InlineData("duplicate-payload")]
    [InlineData("null-artifact")]
    public void InvalidSignedDocuments_NeverAdvanceTrust(string failure)
    {
        var (workspace, client, _) = Initialize();
        var release = Release(Artifact());
        byte[] document = Sign(release);
        switch (failure)
        {
            case "tampered":
                var envelope = UpdateTrustMetadata.Decode<SignedUpdateDocument>(document);
                document = UpdateTrustMetadata.Encode(envelope with { Payload = Convert.ToBase64String(UpdateTrustMetadata.Encode(release with { Version = "1.0.2" })) });
                break;
            case "unknown-key":
                using (var stranger = new TemporaryUpdateSigner()) document = TemporaryUpdateSigner.Sign("release", release, stranger);
                break;
            case "root-role": document = TemporaryUpdateSigner.Sign("root", release, _releaseKey); break;
            case "duplicate-envelope": document = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(document).Insert(1, "\"Payload\":\"AA==\",")); break;
            case "duplicate-payload": document = TemporaryUpdateSigner.SignPayload("release", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(UpdateTrustMetadata.Encode(release)).Insert(1, "\"Version\":\"9.0.0\",")), _releaseKey); break;
            case "null-artifact": document = Sign(release with { Artifacts = [null!] }); break;
        }
        byte[] before = File.ReadAllBytes(workspace.Resolve("trust-state.json"));
        Exception? rejected = Record.Exception(() => client.Authorize(document, Selection));
        Assert.True(rejected is CryptographicException or JsonException or InvalidDataException, rejected?.ToString() ?? "The invalid document was accepted.");
        Assert.Equal(before, File.ReadAllBytes(workspace.Resolve("trust-state.json")));
    }

    [Fact]
    public void ReplayAndVersionReplacement_AreRejectedAfterReloadButIdenticalMetadataIsReusable()
    {
        var (workspace, client, pin) = Initialize();
        var release = Release(Artifact()) with { Sequence = 2 };
        client.Authorize(Sign(release), Selection);
        var reloaded = new LocalUpdateTrustClient(workspace, pin, _clock);
        Assert.Equal("1.0.1", reloaded.Authorize(Sign(release), Selection).Release.Version);
        Assert.Throws<InvalidDataException>(() => reloaded.Authorize(Sign(release with { Sequence = 1 }), Selection));
        Assert.Throws<InvalidDataException>(() => reloaded.Authorize(Sign(release with { ExpiresUtc = release.ExpiresUtc.AddMinutes(1) }), Selection));
        Assert.Throws<InvalidDataException>(() => reloaded.Authorize(Sign(release with { Sequence = 3, Version = "1.0.0" }), Selection with { InstalledVersion = "0.0.0" }));
        Assert.Throws<InvalidDataException>(() => reloaded.Authorize(Sign(release with { Sequence = 3, Artifacts = [Artifact() with { Length = 200 }] }), Selection));
        reloaded.Authorize(Sign(release with { Sequence = 3, ExpiresUtc = release.ExpiresUtc.AddMinutes(1) }), Selection);
    }

    [Fact]
    public void AuthenticatedReleaseWithoutOurVariant_StillBlocksReplayOfAnOlderCatalog()
    {
        var (workspace, client, pin) = Initialize();
        byte[] older = Sign(Release(Artifact()));
        client.Authorize(older, Selection);
        byte[] newer = Sign(Release(Artifact() with { Architecture = "win-arm64" }) with { Version = "1.0.2", Sequence = 2 });
        Assert.Throws<InvalidDataException>(() => client.Authorize(newer, Selection));
        var reloaded = new LocalUpdateTrustClient(workspace, pin, _clock);
        Assert.Throws<InvalidDataException>(() => reloaded.Authorize(older, Selection));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-channel")]
    [InlineData("wrong-product")]
    [InlineData("wrong-variant")]
    [InlineData("ambiguous-variant")]
    [InlineData("old-helper")]
    [InlineData("not-newer")]
    public void SignedButUnusableReleases_CannotAuthorizeDeployment(string failure)
    {
        var (_, client, _) = Initialize();
        var release = Release(Artifact());
        var selection = Selection;
        switch (failure)
        {
            case "expired": release = release with { IssuedUtc = _clock.Now.AddHours(-2), ExpiresUtc = _clock.Now.AddHours(-1) }; break;
            case "future": release = release with { IssuedUtc = _clock.Now.AddHours(1) }; break;
            case "wrong-channel": release = release with { Channel = "stable" }; break;
            case "wrong-product": release = release with { Product = "AnotherApp" }; break;
            case "wrong-variant": selection = selection with { Architecture = "win-arm64" }; break;
            case "ambiguous-variant": release = release with { Artifacts = [Artifact(), Artifact() with { FileName = "another.zip" }] }; break;
            case "old-helper": release = release with { MinimumHelperVersion = "1.0.1" }; break;
            case "not-newer": selection = selection with { InstalledVersion = "1.0.1" }; break;
        }
        Assert.Throws<InvalidDataException>(() => client.Authorize(Sign(release), selection));
    }

    [Fact]
    public void RootRotation_RequiresOldAndNewAuthorityAndRevokesTheOldReleaseKey()
    {
        var (workspace, client, pin) = Initialize();
        using var newRoot = new TemporaryUpdateSigner();
        using var newRelease = new TemporaryUpdateSigner();
        var next = Root() with { Version = 2, Epoch = 2, RootKeys = [newRoot.PublicKey], ReleaseKeys = [newRelease.PublicKey] };
        Assert.Throws<CryptographicException>(() => client.RotateRoot(TemporaryUpdateSigner.Sign("root", next, newRoot)));
        Assert.Throws<CryptographicException>(() => client.RotateRoot(TemporaryUpdateSigner.Sign("root", next, _rootKey)));
        Assert.Throws<InvalidDataException>(() => client.RotateRoot(TemporaryUpdateSigner.Sign("root", next with { Version = 3 }, _rootKey, newRoot)));
        client.Authorize(Sign(Release(Artifact()) with { Sequence = 9007199254740991, Version = "2147483647.0.0" }), Selection);
        client.RotateRoot(TemporaryUpdateSigner.Sign("root", next, _rootKey, newRoot));
        var reloaded = new LocalUpdateTrustClient(workspace, pin, _clock);
        Assert.Throws<CryptographicException>(() => reloaded.Authorize(Sign(Release(Artifact()) with { Epoch = 2 }), Selection));
        byte[] trusted = TemporaryUpdateSigner.Sign("release", Release(Artifact()) with { Epoch = 2 }, newRelease);
        Assert.Equal(1, reloaded.Authorize(trusted, Selection).Release.Sequence);
        Assert.Throws<InvalidDataException>(() => reloaded.Authorize(TemporaryUpdateSigner.Sign("release", Release(Artifact()) with
        { Epoch = 2, Sequence = 2, Artifacts = [Artifact() with { Length = 80 }] }, newRelease), Selection));
    }

    [Fact]
    public void Thresholds_CountEachAuthorizedKeyOnlyOnce()
    {
        var workspace = new OwnedWorkspace(_directory);
        using var second = new TemporaryUpdateSigner();
        var root = Root() with { ReleaseKeys = [_releaseKey.PublicKey, second.PublicKey], ReleaseThreshold = 2 };
        byte[] anchor = TemporaryUpdateSigner.Sign("root", root, _rootKey);
        var client = new LocalUpdateTrustClient(workspace, UpdateTrustMetadata.Digest(anchor), _clock);
        client.Initialize(anchor);
        byte[] one = Sign(Release(Artifact()));
        Assert.Throws<CryptographicException>(() => client.Authorize(one, Selection));
        var envelope = UpdateTrustMetadata.Decode<SignedUpdateDocument>(one);
        Assert.Throws<InvalidDataException>(() => client.Authorize(UpdateTrustMetadata.Encode(envelope with { Signatures = [envelope.Signatures[0], envelope.Signatures[0]] }), Selection));
        client.Authorize(TemporaryUpdateSigner.Sign("release", Release(Artifact()), _releaseKey, second), Selection);
    }

    [Fact]
    public async Task ExpiryAndRevocation_AreRecheckedOnAnAlreadyVerifiedPackage()
    {
        var (workspace, client, _) = Initialize();
        string path = workspace.Resolve("candidate.zip");
        File.WriteAllBytes(path, [1, 2, 3]);
        byte[] document = Sign(Release(LocalUpdateTrustFixture.Artifact(path, "portable", "win-x64", "self-contained")));
        using var lease = await client.OpenPackageAsync(document, Selection, path, Token);
        _clock.Now = _clock.Now.AddHours(2);
        Assert.Throws<InvalidDataException>(lease.Revalidate);
        _clock.Now = _clock.Now.AddHours(-2);
        Assert.Throws<InvalidDataException>(lease.Revalidate);
        _clock.Now = _clock.Now.AddHours(2);
        using var replacement = new TemporaryUpdateSigner();
        client.RotateRoot(TemporaryUpdateSigner.Sign("root", Root() with { Version = 2, Epoch = 2, ReleaseKeys = [replacement.PublicKey] }, _rootKey));
        Assert.Throws<CryptographicException>(lease.Revalidate);
    }

    [Fact]
    public void MissingCorruptOrReplacedTrustState_DoesNotResetToBootstrap()
    {
        var (workspace, client, _) = Initialize();
        byte[] document = Sign(Release(Artifact()));
        client.Authorize(document, Selection);
        _clock.Now = _clock.Now.AddMinutes(-10);
        Assert.Throws<InvalidDataException>(() => client.Authorize(document, Selection));
        _clock.Now = _clock.Now.AddMinutes(10);
        string statePath = workspace.Resolve("trust-state.json");
        File.WriteAllText(statePath, "{}");
        Assert.Throws<JsonException>(() => client.Authorize(document, Selection));
        File.Delete(statePath);
        Assert.Throws<FileNotFoundException>(() => client.Authorize(document, Selection));
        using var stranger = new TemporaryUpdateSigner();
        byte[] replacement = TemporaryUpdateSigner.Sign("root", Root() with { RootKeys = [stranger.PublicKey] }, stranger);
        Assert.Throws<CryptographicException>(() => client.Initialize(replacement));
    }

    [Fact]
    public void PersistenceFailure_DoesNotReturnAnAuthorization()
    {
        var (workspace, client, _) = Initialize();
        using var held = new FileStream(workspace.Resolve("trust-state.json.new"), FileMode.Create, FileAccess.Write, FileShare.None);
        Assert.Throws<IOException>(() => client.Authorize(Sign(Release(Artifact())), Selection));
    }

    private (OwnedWorkspace Workspace, LocalUpdateTrustClient Client, string Pin) Initialize()
    {
        var workspace = new OwnedWorkspace(_directory);
        byte[] anchor = TemporaryUpdateSigner.Sign("root", Root(), _rootKey);
        string pin = UpdateTrustMetadata.Digest(anchor);
        var client = new LocalUpdateTrustClient(workspace, pin, _clock);
        client.Initialize(anchor);
        return (workspace, client, pin);
    }
    private UpdateRoot Root() => new(1, UpdateTrustMetadata.Scope, 1, 1, _clock.Now.AddDays(1), [_rootKey.PublicKey], 1, [_releaseKey.PublicKey], 1);
    private UpdateRelease Release(UpdateArtifact artifact) => new(1, "AudioPilot", "local-test", 1, 1, "1.0.1", "1.0.0", _clock.Now, _clock.Now.AddHours(1), [artifact]);
    private byte[] Sign(UpdateRelease release) => TemporaryUpdateSigner.Sign("release", release, _releaseKey);
    private static UpdateArtifact Artifact() => new("candidate.zip", "portable", "win-x64", "self-contained", 100, new string('A', 64));
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose()
    {
        _rootKey.Dispose();
        _releaseKey.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

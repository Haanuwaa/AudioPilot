using System.Diagnostics;
using System.Security.Cryptography;

namespace AudioPilot.Updater.Trust;

/// <summary>Opt-in process-boundary proof; no network or application instance is started.</summary>
internal static class TufIntegrationSmoke
{
    private static readonly string[] RejectedCases = ["wrong architecture", "wrong bootstrap pin", "tampered package"];

    public static async Task RunAsync(string package, OwnedWorkspace workspace)
    {
        var selection = new UpdateSelection("portable", "win-x64", "self-contained", "1.0.0", "1.0.0");
        string pin = await TufLocalTrustClient.CreateFixtureAsync(workspace, package, "portable", CancellationToken.None);
        var client = new TufLocalTrustClient(workspace, pin);
        var timings = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            var clock = Stopwatch.StartNew();
            using var verified = await client.OpenPackageAsync(package, selection, CancellationToken.None);
            verified.Revalidate();
            timings.Add(clock.Elapsed.TotalMilliseconds);
        }
        await RejectAsync(() => client.OpenPackageAsync(package, selection with { Architecture = "win-arm64" }, CancellationToken.None));
        await RejectAsync(() => new TufLocalTrustClient(workspace, new string('0', 64)).OpenPackageAsync(package, selection, CancellationToken.None));
        string tamper = workspace.Resolve(Path.GetFileName(package));
        File.Copy(package, tamper);
        using (var stream = new FileStream(tamper, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            int value = stream.ReadByte();
            stream.Position = 0;
            stream.WriteByte((byte)(value ^ 1));
        }
        await RejectAsync(() => client.OpenPackageAsync(tamper, selection, CancellationToken.None));
        File.Delete(tamper);
        OwnedWorkspace.WriteJson(workspace.Resolve("tuf-proof.json"), new
        {
            Library = "go-tuf/v2 v2.4.2",
            LocalOnly = true,
            PackageBytes = new FileInfo(package).Length,
            ColdVerifyAndRevalidateMs = timings[0],
            WarmVerifyAndRevalidateMs = timings.Skip(1).ToArray(),
            Rejected = RejectedCases
        });
    }

    private static async Task RejectAsync(Func<Task<VerifiedLocalUpdate>> action)
    {
        try { using var unexpected = await action(); }
        catch (Exception error) when (error is InvalidDataException or CryptographicException) { return; }
        throw new InvalidOperationException("The TUF integration accepted an unauthorized package.");
    }
}

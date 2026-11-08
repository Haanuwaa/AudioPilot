using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioPilot.Benchmarks;

internal static class PayloadFingerprint
{
    private static readonly string[] BundledRuntimeFiles = ["coreclr.dll", "hostfxr.dll", "hostpolicy.dll"];

    internal static object Read(string cliPath)
    {
        cliPath = Path.GetFullPath(cliPath);
        string directory = Path.GetDirectoryName(cliPath)!;
        string[] files = [Path.GetFileName(cliPath), "AudioPilot.Cli.dll", "AudioPilot.dll",
            "AudioPilot.Cli.runtimeconfig.json", "AudioPilot.Cli.deps.json"];
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in files.Concat(BundledRuntimeFiles.Where(name => File.Exists(Path.Combine(directory, name)))))
        {
            using var file = File.OpenRead(Path.Combine(directory, name));
            hashes.Add(name, Convert.ToHexString(SHA256.HashData(file)));
        }
        using var image = new PEReader(File.OpenRead(cliPath));
        using var config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "AudioPilot.Cli.runtimeconfig.json")));
        JsonElement runtime = config.RootElement.GetProperty("runtimeOptions");
        var readyToRun = new Dictionary<string, bool>();
        foreach (string name in new[] { "AudioPilot.Cli.dll", "AudioPilot.dll" })
        {
            using var managedImage = new PEReader(File.OpenRead(Path.Combine(directory, name)));
            readyToRun.Add(name, managedImage.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.Size > 0);
        }
        return new
        {
            Machine = image.PEHeaders.CoffHeader.Machine.ToString(),
            Deployment = runtime.TryGetProperty("includedFrameworks", out _) ? "SelfContained" : "FrameworkDependent",
            RuntimeConfiguration = runtime.Clone(),
            ReadyToRun = readyToRun,
            Files = hashes,
        };
    }
}

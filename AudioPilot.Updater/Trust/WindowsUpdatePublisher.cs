using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AudioPilot.Updater.Trust;

internal sealed record WindowsPublisherPolicy(string CertificateSha256, bool PinnedSelfSigned);

/// <summary>Verifies actual Windows signatures without installing certificates or contacting trust servers.</summary>
internal static partial class WindowsUpdatePublisher
{
    private const int UntrustedRoot = unchecked((int)0x800B0109);
    [Flags]
    private enum TrustFlags : uint { RevocationCheckNone = 0x10, CacheOnlyUrlRetrieval = 0x1000, DisableMd2Md4 = 0x2000 }
    private static readonly string[] RequiredPortableFiles =
    [
        "AudioPilot.exe", "AudioPilot.dll", "AudioPilot.Cli.exe", "AudioPilot.Cli.dll",
        "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll",
        "AudioPilot.UpdateProtocol.dll", "AudioPilot.TufBridge.exe"
    ];

    internal static async Task VerifyPackageAsync(OwnedWorkspace workspace, string path, string kind, WindowsPublisherPolicy policy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (kind == "msi") { VerifyFile(path, policy); return; }
        if (kind != "portable") throw new InvalidDataException("Unsupported Windows signature package.");
        string relative = "publisher-check/" + Guid.NewGuid().ToString("N");
        string directory = UpdateStorage.CreateDirectory(workspace, relative, "publisher-check");
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count is 0 or > 4096) throw new InvalidDataException("Invalid signed payload entry count.");
            string prefix = zip.GetEntry("AudioPilot/AudioPilot.exe") == null ? "" : "AudioPilot/";
            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total = checked(total + entry.Length);
                if (total > 1024L * 1024 * 1024) throw new InvalidDataException("Expanded package is too large.");
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Mixed signed payload layouts.");
                string name = entry.FullName[prefix.Length..];
                if (name.Contains('/') || name.Contains('\\') || !name.StartsWith("AudioPilot", StringComparison.Ordinal) ||
                    !(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) continue;
                if (!files.TryAdd(name, entry)) throw new InvalidDataException("Duplicate signed payload entry.");
            }
            if (RequiredPortableFiles.Any(name => !files.ContainsKey(name))) throw new InvalidDataException("The signed updater payload is incomplete.");
            byte[] buffer = new byte[65536];
            foreach (var (name, entry) in files)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length is <= 0 or > 128 * 1024 * 1024 || name != Path.GetFileName(name) || name.Contains(':'))
                    throw new InvalidDataException("Invalid signed payload entry.");
                string target = Path.Combine(directory, name);
                await using (var input = entry.Open())
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    long length = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        length += count;
                        if (length > entry.Length) throw new InvalidDataException("Signed payload entry exceeds its declared length.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                    if (length != entry.Length) throw new InvalidDataException("Signed payload entry is truncated.");
                }
                VerifyFile(target, policy);
            }
        }
        finally
        {
            UpdateStorage.Trim(workspace, "publisher-check", "publisher-check", []);
        }
    }

    internal static void VerifyFile(string path, WindowsPublisherPolicy policy)
    {
        if (!UpdateTrustMetadata.IsDigest(policy.CertificateSha256)) throw new InvalidDataException("Missing Windows publisher pin.");
        OwnedWorkspace.CheckAncestors(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        nint name = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        nint filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<TrustFile>());
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = name };
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            Ui = 2,
            Choice = 1,
            File = filePointer,
            StateAction = 1,
            Flags = (uint)(TrustFlags.CacheOnlyUrlRetrieval | TrustFlags.RevocationCheckNone | TrustFlags.DisableMd2Md4)
        };
        try
        {
            int result = WinVerifyTrust(new nint(-1), ref action, ref data);
            if (result != 0 && !(policy.PinnedSelfSigned && result == UntrustedRoot))
                throw new CryptographicException($"Windows signature verification failed (0x{result:X8}).");
            nint provider = WTHelperProvDataFromStateData(data.State);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            nint chain = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (chain == 0) throw new CryptographicException("Windows did not return a verified signer.");
            var certificate = Marshal.PtrToStructure<ProviderCertificate>(chain);
            if (certificate.Certificate == 0) throw new CryptographicException("Windows did not return a signing certificate.");
            var context = Marshal.PtrToStructure<CertificateContext>(certificate.Certificate);
            if (context.Length is 0 or > 65536) throw new CryptographicException("Invalid Windows signing certificate.");
            byte[] encoded = new byte[context.Length];
            Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
            using var leaf = X509CertificateLoader.LoadCertificate(encoded);
            if (!leaf.GetCertHashString(HashAlgorithmName.SHA256).Equals(policy.CertificateSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("The Windows signer differs from the approved publisher.");
            if (policy.PinnedSelfSigned)
            {
                if (!leaf.SubjectName.RawData.AsSpan().SequenceEqual(leaf.IssuerName.RawData)) throw new CryptographicException("The pinned pilot publisher must be self-signed.");
                using var trust = new X509Chain();
                trust.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                trust.ChainPolicy.CustomTrustStore.Add(leaf);
                trust.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                trust.ChainPolicy.DisableCertificateDownloads = true;
                trust.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
                if (!trust.Build(leaf)) throw new CryptographicException("The pinned pilot signing certificate is invalid or expired.");
            }
        }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(new nint(-1), ref action, ref data);
            Marshal.FreeCoTaskMem(filePointer);
            Marshal.FreeCoTaskMem(name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile { public uint Size; public nint Path, Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint Policy, Sip;
        public uint Ui, Revocation, Choice;
        public nint File;
        public uint StateAction;
        public nint State, Url;
        public uint Flags, Context;
        public nint SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate { public uint Size; public nint Certificate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext { public uint Encoding; public nint Encoded; public uint Length; public nint Info, Store; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wintrust.dll")] private static partial int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wintrust.dll")] private static partial nint WTHelperProvDataFromStateData(nint state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wintrust.dll")] private static partial nint WTHelperGetProvSignerFromChain(nint provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("wintrust.dll")] private static partial nint WTHelperGetProvCertFromChain(nint signer, uint index);
}

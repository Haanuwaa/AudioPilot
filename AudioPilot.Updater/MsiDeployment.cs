using System.Runtime.InteropServices;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;
using Microsoft.Win32;

namespace AudioPilot.Updater;

internal sealed record MsiOptions(string InstallFolder, string DataFolder, bool DesktopShortcut, bool StartMenuShortcut, bool CliOnPath)
{
    public string ToProperties() => $"INSTALLFOLDER={Quote(InstallFolder)} AUDIOPILOT_DATA_FOLDER={Quote(DataFolder)} " +
        $"INSTALLDESKTOPSHORTCUT={(DesktopShortcut ? 1 : 0)} INSTALLSTARTMENUSHORTCUT={(StartMenuShortcut ? 1 : 0)} ADD_CLI_TO_PATH={(CliOnPath ? 1 : 0)} REBOOT=ReallySuppress";

    private static string Quote(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.Contains('"') || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid MSI folder.");
        return '"' + Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)) + '"';
    }

    public static MsiOptions ReadInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Haanuwaa\AudioPilot") ?? throw new InvalidOperationException("No installed AudioPilot registration.");
        return new(Path.TrimEndingDirectorySeparator(Read("InstallFolder")), Path.TrimEndingDirectorySeparator(Read("DataFolder")), ReadFlag("InstallDesktopShortcut"), ReadFlag("InstallStartMenuShortcut"), ReadFlag("AddCliToPath"));
        string Read(string name) => key.GetValue(name)?.ToString() ?? throw new InvalidDataException($"Missing installer option {name}.");
        bool ReadFlag(string name) => Read(name) switch { "1" => true, "0" or "" => false, _ => throw new InvalidDataException($"Invalid installer option {name}.") };
    }
}

/// <summary>Per-user MSI replacement with verified health before its transaction commits.</summary>
internal static partial class MsiDeployment
{
    public static bool Apply(VerifiedLocalUpdate package, MsiOptions options, Func<bool> healthCheck, string logPath, Action? stopCandidateBeforeRollback = null)
    {
        RequireRuntime();
        package.Revalidate();
        if (package.Authorization.Artifact.Kind != "msi") throw new InvalidDataException("An MSI deployment requires a signed MSI artifact.");
        return ApplyCore(package.Path, options, healthCheck, logPath, stopCandidateBeforeRollback);
    }

    public static bool Apply(string msi, MsiOptions options, Func<bool> healthCheck, string logPath, Action? stopCandidateBeforeRollback = null)
    {
        RequireSandbox();
        return ApplyCore(msi, options, healthCheck, logPath, stopCandidateBeforeRollback);
    }

    private static bool ApplyCore(string msi, MsiOptions options, Func<bool> healthCheck, string logPath, Action? stopCandidateBeforeRollback)
    {
        OwnedWorkspace.CheckAncestors(msi);
        Check(MsiEnableLog(0x3fff, logPath, 0), "enable logging");
        uint priorUi = MsiSetInternalUI(2, 0);
        uint transaction = 0;
        nint ownerChanged = 0;
        bool ended = false;
        try
        {
            Check(MsiBeginTransaction("AudioPilot update", 0, out transaction, out ownerChanged), "begin transaction");
            uint installed = MsiInstallProduct(msi, options.ToProperties());
            if (installed == 3010) throw new InvalidOperationException("Reboot-required updates need a separate recovery policy; updater rolls back.");
            Check(installed, "install");
            bool healthy = healthCheck();
            if (!healthy) stopCandidateBeforeRollback?.Invoke();
            Check(MsiEndTransaction(healthy ? 1u : 0u), healthy ? "commit" : "rollback");
            ended = true;
            return healthy;
        }
        finally
        {
            try
            {
                if (transaction != 0 && !ended)
                {
                    stopCandidateBeforeRollback?.Invoke();
                    Check(MsiEndTransaction(0), "rollback after failure");
                }
            }
            finally
            {
                if (transaction != 0) _ = MsiCloseHandle(transaction);
                if (ownerChanged != 0) _ = CloseHandle(ownerChanged);
                _ = MsiSetInternalUI(priorUi, 0);
                _ = MsiEnableLog(0, null, 0);
            }
        }
    }

    public static void RequireSandbox()
    {
        if (!Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Real AudioPilot MSI updater tests are restricted to Windows Sandbox.");
    }

    internal static void RequireRuntime()
    {
        if (Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase)) return;
        UpdateClientPolicy.RequireConfigured();
        if (UpdateClientPolicy.Current?.Channel == "staging" && RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("The updater pilot currently supports x64 packages only.");
    }

    public static async Task WaitForInstallerIdleAsync(CancellationToken token)
    {
        RequireRuntime();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            uint result = MsiBeginTransaction("AudioPilot updater recovery barrier", 0, out uint transaction, out nint ownerChanged);
            if (result == 1618) { await Task.Delay(250, token); continue; }
            Check(result, "check recovery barrier");
            try { Check(MsiEndTransaction(0), "close recovery barrier"); }
            finally
            {
                _ = MsiCloseHandle(transaction);
                if (ownerChanged != 0) _ = CloseHandle(ownerChanged);
            }
            return;
        }
    }

    public static unsafe string GetSingleInstalledProduct(string expectedFolder)
    {
        RequireSandbox();
        if (!MsiOptions.ReadInstalled().InstallFolder.Equals(expectedFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installed fixture belongs to another folder.");
        char* code = stackalloc char[39];
        Check(MsiEnumRelatedProducts("{8D18A00C-7CC8-487B-A462-7D3252139D72}", 0, 0, code), "find installed fixture");
        string productCode = new(code);
        if (MsiEnumRelatedProducts("{8D18A00C-7CC8-487B-A462-7D3252139D72}", 0, 1, code) != 259)
            throw new InvalidOperationException("More than one related fixture may be installed; refusing cleanup.");
        return Guid.TryParse(productCode, out _) ? productCode : throw new InvalidDataException("Invalid product code.");
    }

    private static void Check(uint result, string step)
    {
        if (result != 0) throw new InvalidOperationException($"Windows Installer {step} failed ({result}).");
    }

    [LibraryImport("msi.dll", EntryPoint = "MsiBeginTransactionW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiBeginTransaction(string name, uint attributes, out uint transaction, out nint ownerChanged);
    [LibraryImport("msi.dll")] private static partial uint MsiEndTransaction(uint state);
    [LibraryImport("msi.dll", EntryPoint = "MsiEnumRelatedProductsW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, char* productCode);
    [LibraryImport("msi.dll", EntryPoint = "MsiInstallProductW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiInstallProduct(string package, string properties);
    [LibraryImport("msi.dll")] private static partial uint MsiCloseHandle(uint handle);
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint handle);
    [LibraryImport("msi.dll")] private static partial uint MsiSetInternalUI(uint level, nint owner);
    [LibraryImport("msi.dll", EntryPoint = "MsiEnableLogW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiEnableLog(uint mode, string? path, uint attributes);
}

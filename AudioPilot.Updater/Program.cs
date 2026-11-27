using System.Diagnostics;

namespace AudioPilot.Updater;

internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["build-portable-delta", var baseline, var target, var version, var output])
            {
                using var cancel = new CancellationTokenSource();
                void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs) { eventArgs.Cancel = true; cancel.Cancel(); }
                Console.CancelKeyPress += OnCancel;
                try { await PortableDelta.CreateAsync(baseline, target, version, output, cancel.Token); }
                finally { Console.CancelKeyPress -= OnCancel; }
                return 0;
            }
            if (args is ["verify-publisher", var publisher, .. var files] && files.Length > 0)
            {
                foreach (string file in files) Trust.WindowsUpdatePublisher.VerifyFile(file, new(publisher, PinnedSelfSigned: true));
                Console.WriteLine("Verified pinned Windows publisher signature.");
                return 0;
            }
            if (args is ["pilot-launch", var root, .. var forwarded])
            {
                AudioPilot.UpdateProtocol.UpdateClientPolicy.RequirePackaged();
                AudioPilot.UpdateProtocol.UpdateClientPolicy.RequireUnelevated();
                return await PortableLauncher.RunAsync(new OwnedWorkspace(root), arguments: forwarded);
            }
#if AUDIOPILOT_UPDATE_ENGINEERING
            if (await TryRunValidationCommandAsync(args) is int exitCode) return exitCode;
#endif
            Console.Error.WriteLine("Open the packaged updater from AudioPilot Settings.");
            return 2;
        }
        catch (Exception error)
        {
            if (args is ["pilot-launch", ..])
            {
                int code = AudioPilot.UpdateProtocol.PortableLaunchFailure.ExitCode(error);
                try { Console.Error.WriteLine($"AudioPilot startup failed (code {code}; {error.GetType().Name}; HRESULT 0x{error.HResult:X8})."); }
                catch (IOException) { }
                return code;
            }
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static void RunMsi(string action, string path, string properties, string? logPath = null)
    {
        var start = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false, CreateNoWindow = true, Arguments = $"{action} \"{path}\" /qn /norestart {properties}" + (logPath == null ? "" : $" /l*vx! \"{logPath}\"") };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("MSI runner failed to start.");
        if (!process.WaitForExit(300000)) throw new TimeoutException("MSI operation remains active; do not terminate Windows Installer.");
        if (process.ExitCode != 0) throw new InvalidOperationException($"MSI {action} failed ({process.ExitCode}).");
    }
}

namespace AudioPilot.Updater;

internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["verify-publisher", var publisher, var file])
            {
                Trust.WindowsUpdatePublisher.VerifyFile(file, new(publisher, PinnedSelfSigned: true));
                Console.WriteLine("Verified pinned Windows publisher signature.");
                return 0;
            }
            if (args is ["pilot-launch", var root, .. var forwarded])
            {
                AudioPilot.UpdateProtocol.UpdateClientPolicy.RequirePackaged();
                return await PortableLauncher.RunAsync(new OwnedWorkspace(root), arguments: forwarded);
            }
            if (await TryRunValidationCommandAsync(args) is int exitCode) return exitCode;
            Console.Error.WriteLine("Open the packaged updater from AudioPilot Settings. Engineering validation commands are documented in AudioPilot.Updater/README.md.");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

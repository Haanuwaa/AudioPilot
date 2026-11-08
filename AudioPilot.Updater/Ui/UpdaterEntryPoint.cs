namespace AudioPilot.Updater.Ui;

internal static class UpdaterEntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("--", StringComparison.Ordinal))
            return Program.Main(args).GetAwaiter().GetResult();
        var application = new UpdaterApplication();
        application.InitializeComponent();
        return application.Run();
    }
}

namespace AudioPilot.Updater.Ui;

internal static class UpdaterEntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("--", StringComparison.Ordinal))
            return Program.Main(args).GetAwaiter().GetResult();
#if !AUDIOPILOT_UPDATE_ENGINEERING
        if (args[0] is not ("--automation" or "--pilot-ui" or "--pilot-recover")) return 2;
#endif
        var application = new UpdaterApplication();
        application.InitializeComponent();
        return application.Run();
    }
}

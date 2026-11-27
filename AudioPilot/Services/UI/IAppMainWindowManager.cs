namespace AudioPilot.Services.UI
{
    internal enum MainWindowOpenTarget
    {
        Default,
        Output,
        Input,
        Routines,
        Settings,
        VolumeMixer,
    }

    internal readonly record struct MainWindowHotkeyPresentation(Func<bool>? CanPresent, Func<nint> GetRaiseForeground);

    internal interface IAppMainWindowManager
    {
        bool IsCreated { get; }
        bool IsVisible { get; }
        bool CanRetryHotkeyShow => false;
        MainWindow? CurrentWindow { get; }

        Task<bool> ShowAsync(MainWindowOpenTarget target = MainWindowOpenTarget.Default, MainWindowHotkeyPresentation? hotkey = null,
            CancellationToken cancellationToken = default);
        bool Hide();
        Task<bool> HideAsync(CancellationToken cancellationToken = default);
        Task<Models.WindowLayoutSnapshot> GetLayoutAsync(CancellationToken cancellationToken = default)
            => Task.FromException<Models.WindowLayoutSnapshot>(new NotSupportedException("Window placement is unavailable."));
        Task<Models.WindowLayoutSnapshot> ChangeLayoutAsync(Models.WindowLayoutChange change, CancellationToken cancellationToken = default)
            => Task.FromException<Models.WindowLayoutSnapshot>(new NotSupportedException("Window placement is unavailable."));
        void BeginShutdown();
        Task CloseForShutdownAsync(CancellationToken cancellationToken = default);
    }
}

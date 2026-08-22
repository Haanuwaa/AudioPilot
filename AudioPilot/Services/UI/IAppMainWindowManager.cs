using System.Windows;

namespace AudioPilot.Services.UI
{
    internal enum MainWindowOpenTarget
    {
        Default,
        Output,
        Input,
        Routines,
        Settings,
    }

    internal interface IAppMainWindowManager
    {
        bool IsCreated { get; }
        bool IsVisible { get; }
        MainWindow? CurrentWindow { get; }

        Task<bool> ShowAsync(MainWindowOpenTarget target = MainWindowOpenTarget.Default, CancellationToken cancellationToken = default);
        bool Hide();
        Task<bool> HideAsync(CancellationToken cancellationToken = default);
        void BeginShutdown();
        Task CloseForShutdownAsync(CancellationToken cancellationToken = default);
    }
}

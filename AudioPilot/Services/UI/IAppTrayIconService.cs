using System.Windows.Media;

namespace AudioPilot.Services.UI
{
    internal interface IAppTrayIconService : IDisposable
    {
        bool IsReady { get; }
        bool EnsureVisible(ImageSource? icon = null);
        void Hide();
        void ShowBalloon(string title, string message, bool warning = false, MainWindowOpenTarget target = MainWindowOpenTarget.Default);
        void BeginShutdown();
    }
}

using System.Windows.Media;

namespace AudioPilot.Services.UI
{
    internal interface IAppTrayIconService : IDisposable
    {
        bool IsReady { get; }
        bool EnsureVisible(ImageSource? icon = null);
        void Hide();
        void ShowBalloon(string title, string message);
        void BeginShutdown();
    }
}

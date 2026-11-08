using System.Windows.Media;

namespace AudioPilot.Services.UI
{
    internal interface IAppTrayIconService : IDisposable
    {
        bool IsReady { get; }
        bool EnsureVisible(ImageSource? icon = null, bool logFailure = true);
        void Hide();
        Task<bool> ShowBalloonAsync(string title, string message, bool warning = false, Func<bool>? canShow = null);
        void BeginShutdown();
    }
}

using System.Windows;
using System.Windows.Media;
using AudioPilot.Services.UI;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class TestAppMainWindowManager(Window? visibilityWindow = null) : IAppMainWindowManager
{
    public Window? VisibilityWindow { get; set; } = visibilityWindow;
    public bool IsCreatedValue { get; set; }
    public bool IsVisibleValue { get; set; }
    public bool ShowResult { get; set; } = true;
    public bool HideResult { get; set; } = true;
    public List<MainWindowOpenTarget> ShowTargets { get; } = [];
    public int HideCalls { get; private set; }
    public bool ShutdownStarted { get; private set; }
    public bool IsCreated => IsCreatedValue;
    public bool IsVisible => VisibilityWindow is { Visibility: Visibility.Visible, WindowState: not WindowState.Minimized }
        || IsVisibleValue;
    public MainWindow? CurrentWindow => null;

    public Task<bool> ShowAsync(MainWindowOpenTarget target = MainWindowOpenTarget.Default, CancellationToken cancellationToken = default)
    {
        if (ShutdownStarted)
        {
            return Task.FromResult(false);
        }

        ShowTargets.Add(target);
        IsVisibleValue = ShowResult;
        return Task.FromResult(ShowResult);
    }

    public bool Hide()
    {
        HideCalls++;
        IsVisibleValue = false;
        return HideResult;
    }

    public Task<bool> HideAsync(CancellationToken cancellationToken = default) => Task.FromResult(Hide());
    public void BeginShutdown() => ShutdownStarted = true;
    public Task CloseForShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class TestAppTrayIconService : IAppTrayIconService
{
    public bool EnsureVisibleResult { get; set; } = true;
    public bool IsReady { get; private set; }
    public int EnsureVisibleCalls { get; private set; }
    public int BalloonCalls { get; private set; }
    public int HideCalls { get; private set; }
    public bool ShutdownStarted { get; private set; }

    public bool EnsureVisible(ImageSource? icon = null)
    {
        EnsureVisibleCalls++;
        IsReady = EnsureVisibleResult;
        return EnsureVisibleResult;
    }

    public void Hide()
    {
        HideCalls++;
        IsReady = false;
    }

    public void ShowBalloon(string title, string message) => BalloonCalls++;
    public void BeginShutdown()
    {
        ShutdownStarted = true;
        IsReady = false;
    }
    public void Dispose() => IsReady = false;
}

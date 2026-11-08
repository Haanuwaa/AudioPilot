using System.Windows;
using System.Windows.Media;

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
    public MainWindow? CurrentWindow => VisibilityWindow as MainWindow;

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
    public bool AcceptBalloons { get; set; } = true;
    public TaskCompletionSource<bool>? BalloonGate { get; set; }
    public bool LastBalloonWarning { get; private set; }
    public string LastBalloonMessage { get; private set; } = string.Empty;
    public int HideCalls { get; private set; }
    public bool ShutdownStarted { get; private set; }

    public bool EnsureVisible(ImageSource? icon = null, bool logFailure = true)
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

    public async Task<bool> ShowBalloonAsync(string title, string message, bool warning = false, Func<bool>? canShow = null)
    {
        if (BalloonGate != null) await BalloonGate.Task;
        if (!AcceptBalloons || canShow?.Invoke() == false) return false;
        BalloonCalls++;
        LastBalloonWarning = warning;
        LastBalloonMessage = message;
        return true;
    }
    public void BeginShutdown()
    {
        ShutdownStarted = true;
        IsReady = false;
    }
    public void Dispose() => IsReady = false;
}

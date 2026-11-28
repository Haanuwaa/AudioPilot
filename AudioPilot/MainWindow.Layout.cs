using System.Windows.Threading;

namespace AudioPilot;

internal readonly record struct MainWindowScrollPosition(double Offset, bool AtBottom);

public partial class MainWindow
{
    private MainWindowScrollPosition ReadScrollPosition()
    {
        double offset = MainContentScrollViewer.VerticalOffset;
        double bottom = MainContentScrollViewer.ScrollableHeight;
        return new(offset, bottom > 0.5 && Math.Abs(offset - bottom) <= 0.5);
    }

    internal (int TabIndex, MainWindowScrollPosition Position) CaptureLayoutScrollPosition()
    {
        int tab = DeviceTabControl.SelectedIndex;
        bool restorePending = _tabScrollRestoreWork is { Status: DispatcherOperationStatus.Pending } && tab is >= 0 and < 4;
        var position = restorePending ? _tabScrollPositions[tab] : ReadScrollPosition();
        CancelPendingTabScrollRestore();
        CancelPendingRestoreMixerScroll();
        return (tab, position);
    }

    internal void RestoreLayoutScrollPosition((int TabIndex, MainWindowScrollPosition Position) saved)
    {
        var (tab, position) = saved;
        if (_allowCloseForRuntimeShutdown || tab is < 0 or >= 4 || DeviceTabControl.SelectedIndex != tab) return;
        MainContentScrollViewer.UpdateLayout();
        if (DeviceTabControl.SelectedIndex != tab) return;
        if (position.AtBottom) MainContentScrollViewer.ScrollToBottom();
        else MainContentScrollViewer.ScrollToVerticalOffset(position.Offset);
        MainContentScrollViewer.UpdateLayout();
        if (DeviceTabControl.SelectedIndex == tab)
            _tabScrollPositions[tab] = ReadScrollPosition();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Helpers;
using AudioPilot.Services.Diagnostics;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot;

public partial class MainWindow
{
    private SettingsFindController? _settingsFind;
    private FrameworkElement? _settingsFindScope;
    private DispatcherOperation? _settingsFindWork;
    private bool _settingsFindDisposed;
    private bool _settingsFindHighlightDismissed;

    internal void ReviewDiagnosticSetting(DiagnosticReviewTarget target)
    {
        if (_settingsFindDisposed || _appVm.IsRuntimeShutdownRequested || Dispatcher.HasShutdownStarted || target.TabIndex is < 0 or > 3) return;
        _appVm.SelectedSettingsTabIndex = target.TabIndex;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_settingsFindDisposed || _appVm.IsRuntimeShutdownRequested || _appVm.SelectedSettingsTabIndex != target.TabIndex) return;
            MainContentScrollViewer.ScrollToTop();
            if (target.TabIndex == 3)
            {
                _settingsFindHighlightDismissed = false;
                SettingsSearchBox.Text = target.SearchQuery ?? string.Empty;
                ScheduleSettingsFind();
                if (target.SearchQuery != null) { SettingsSearchBox.Focus(); SettingsSearchBox.SelectAll(); }
            }
        }, DispatcherPriority.Loaded);
    }

    private void FindSettings_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        var box = e.OriginalSource as TextBox ?? Keyboard.FocusedElement as TextBox;
        e.CanExecute = !_settingsFindDisposed && (box == null
            || !Interaction.GetBehaviors(box).OfType<HotkeyCaptureBehavior>().Any());
        e.Handled = true;
    }

    private void FindSettings_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _appVm.SelectedSettingsTabIndex = 3;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_settingsFindDisposed || !_appVm.IsSettingsTabActive) return;
            SettingsSearchBox.Focus();
            SettingsSearchBox.SelectAll();
        }, DispatcherPriority.Input);
        e.Handled = true;
    }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _settingsFindHighlightDismissed = false;
        ScheduleSettingsFind();
    }

    private void ScheduleSettingsFind()
    {
        _settingsFindWork?.Abort();
        if (_settingsFindDisposed || Dispatcher.HasShutdownStarted) return;
        _settingsFindWork = Dispatcher.BeginInvoke(() =>
        {
            _settingsFindWork = null;
            if (_settingsFindDisposed || !_appVm.IsSettingsTabActive || DeviceTabControl.SelectedContent is not FrameworkElement scope) return;
            if (!ReferenceEquals(_settingsFindScope, scope))
            {
                _settingsFind?.Dispose();
                _settingsFind = new SettingsFindController(scope, MainContentScrollViewer);
                _settingsFindScope = scope;
            }
            _settingsFind!.Search(SettingsSearchBox.Text, revealMatch: !_settingsFindHighlightDismissed);
            UpdateSettingsFindStatus();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateSettingsFindStatus()
    {
        SettingsSearchStatus.Text = string.IsNullOrWhiteSpace(SettingsSearchBox.Text) ? string.Empty : _settingsFind?.Status ?? "No matches";
        PreviousSettingButton.IsEnabled = NextSettingButton.IsEnabled = _settingsFind?.HasMatches == true;
    }

    private void PreviousSetting_Click(object sender, RoutedEventArgs e) => MoveSettingsMatch(-1);
    private void NextSetting_Click(object sender, RoutedEventArgs e) => MoveSettingsMatch(1);

    private void MoveSettingsMatch(int direction)
    {
        if (_settingsFindDisposed || !_appVm.IsSettingsTabActive) return;
        _settingsFindHighlightDismissed = false;
        _settingsFind?.Move(direction);
        UpdateSettingsFindStatus();
    }

    private void SettingsFind_UserInput(object sender, RoutedEventArgs e)
    {
        if (_settingsFindDisposed || _settingsFindHighlightDismissed
            || (_settingsFindWork == null && _settingsFind?.HasMatches != true)) return;
        for (DependencyObject? source = e.OriginalSource as DependencyObject; source != null;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
        {
            if (ReferenceEquals(source, SettingsSearchBox) || ReferenceEquals(source, PreviousSettingButton)
                || ReferenceEquals(source, NextSettingButton)) return;
        }
        _settingsFindHighlightDismissed = true;
        _settingsFind?.DismissHighlight();
    }

    private void SettingsSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsSearchBox.Text.Length > 0)
        {
            SettingsSearchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.F3)
        {
            MoveSettingsMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
    }

    private void ResetSettingsFindForTabChange()
    {
        _settingsFindHighlightDismissed = true;
        _settingsFindWork?.Abort();
        _settingsFind?.Clear();
        if (_appVm.IsSettingsTabActive) ScheduleSettingsFind();
    }

    private void DisposeSettingsFind()
    {
        _settingsFindDisposed = true;
        _settingsFindWork?.Abort();
        _settingsFind?.Dispose();
        _settingsFind = null;
        _settingsFindScope = null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Localization;
using AudioPilot.Services.Diagnostics;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot;

public partial class MainWindow
{
    private SettingsFindController? _settingsFind;
    private FrameworkElement? _settingsFindScope;
    private DispatcherOperation? _settingsFindWork;
    private DispatcherOperation? _settingsPrewarmWork;
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
                SettingsSearchBox.Text = ResolveDiagnosticSearchQuery(target.SearchQuery, _appVm.Localization);
                ScheduleSettingsFind();
                if (target.SearchQuery != null) { SettingsSearchBox.Focus(); SettingsSearchBox.SelectAll(); }
            }
        }, DispatcherPriority.Loaded);
    }

    internal static string ResolveDiagnosticSearchQuery(string? searchQuery, LocalizationService localization) => searchQuery switch
    {
        "Log level" => localization["Settings_LogLabel"].TrimEnd(':'),
        "Redact sensitive log content" => localization["Settings_Redaction"],
        "Show/hide app" => localization["Ui_ShowHideApp"].TrimEnd(':'),
        "Show audio status" => localization["Ui_ShowAudioStatus"].TrimEnd(':'),
        "Device picker" => localization["Ui_DevicePicker"].TrimEnd(':'),
        "Quick mixer" => localization["Ui_QuickMixerLabel"].TrimEnd(':'),
        "Show current track" => localization["Ui_ShowCurrentTrack"].TrimEnd(':'),
        "Play/pause" => localization["Ui_PlayPause"].TrimEnd(':'),
        "Next track" => localization["Ui_NextTrack"].TrimEnd(':'),
        "Previous track" => localization["Ui_PreviousTrack"].TrimEnd(':'),
        "Seek forward" => localization["Ui_SeekForward"].TrimEnd(':'),
        "Seek backward" => localization["Ui_SeekBackward"].TrimEnd(':'),
        "Microphone mute" => localization["Ui_MicrophoneMute"].TrimEnd(':'),
        "Output mute" => localization["Ui_OutputMute"].TrimEnd(':'),
        "Deafen" => localization["Ui_Deafen"].TrimEnd(':'),
        "Push-to-talk" => localization["Ui_PushToTalk"].TrimEnd(':'),
        "Hold-to-mute" => localization["Ui_HoldToMute"].TrimEnd(':'),
        "Output volume up" => localization["Ui_OutputVolumeUp"].TrimEnd(':'),
        "Output volume down" => localization["Ui_OutputVolumeDown"].TrimEnd(':'),
        "Microphone up" => localization["Ui_MicrophoneUp"].TrimEnd(':'),
        "Microphone down" => localization["Ui_MicrophoneDown"].TrimEnd(':'),
        "Foreground app volume up" => localization["Ui_ForegroundAppVolumeUpHotkey"],
        "Foreground app volume down" => localization["Ui_ForegroundAppVolumeDownHotkey"],
        "Foreground app mute" => localization["Ui_ForegroundAppMuteHotkey"],
        "Listen hotkey" => localization["Ui_ListenHotkey"].TrimEnd(':'),
        "Output reverse" => localization["Ui_OutputReverse"].TrimEnd(':'),
        "Input reverse" => localization["Ui_InputReverse"].TrimEnd(':'),
        _ => searchQuery ?? string.Empty,
    };

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
        _settingsFindWork = null;
        if (_settingsFindDisposed || Dispatcher.HasShutdownStarted) return;
        _settingsFindWork = Dispatcher.BeginInvoke(() =>
        {
            _settingsFindWork = null;
            ExecuteSettingsFind(revealMatch: !_settingsFindHighlightDismissed);
        }, DispatcherPriority.Loaded);
    }

    private void ExecuteSettingsFind(bool revealMatch)
    {
        if (_settingsFindDisposed || !_appVm.IsSettingsTabActive || DeviceTabControl.SelectedContent is not FrameworkElement scope) return;
        if (!ReferenceEquals(_settingsFindScope, scope))
        {
            _settingsFind?.Dispose();
            _settingsFind = new SettingsFindController(scope, MainContentScrollViewer);
            _settingsFindScope = scope;
        }
        _settingsFind!.Search(SettingsSearchBox.Text, revealMatch: revealMatch);
        UpdateSettingsFindStatus();
    }

    private void UpdateSettingsFindStatus()
    {
        SettingsSearchStatus.Text = string.IsNullOrWhiteSpace(SettingsSearchBox.Text) ? string.Empty
            : _settingsFind?.Status ?? _appVm.Localization["Settings_FindNoMatches"];
        PreviousSettingButton.IsEnabled = NextSettingButton.IsEnabled = _settingsFind?.HasMatches == true;
    }

    private void PreviousSetting_Click(object sender, RoutedEventArgs e) => MoveSettingsMatch(-1);
    private void NextSetting_Click(object sender, RoutedEventArgs e) => MoveSettingsMatch(1);

    private void MoveSettingsMatch(int direction)
    {
        if (_settingsFindDisposed || !_appVm.IsSettingsTabActive) return;
        _settingsFindHighlightDismissed = false;
        if (_settingsFindWork != null)
        {
            _settingsFindWork.Abort();
            _settingsFindWork = null;
            ExecuteSettingsFind(revealMatch: false);
        }
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
        _settingsFindWork = null;
        _settingsFind?.Clear();
        if (_appVm.IsSettingsTabActive)
        {
            CancelDeferredSettingsPrewarm();
            if (!string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
            {
                ScheduleSettingsFind();
            }

            Services.Updates.AppUpdateLauncher.WarmStageInBackground();
        }
    }

    private void DisposeSettingsFind()
    {
        _settingsFindDisposed = true;
        CancelDeferredSettingsPrewarm();
        _languagePresentationWork?.Abort();
        ReleaseSettingsFindResources();
    }

    private void ScheduleDeferredSettingsPrewarm()
    {
        CancelDeferredSettingsPrewarm();
        if (_settingsFindDisposed || _allowCloseForRuntimeShutdown || Dispatcher.HasShutdownStarted || !IsVisible
            || DeviceTabControl.Items.Count <= 3 || DeviceTabControl.Items[3] is not TabItem settingsTab
            || DeferredTabContentBehavior.GetTemplate(settingsTab) == null)
        {
            return;
        }

        _settingsPrewarmWork = Dispatcher.BeginInvoke(() =>
        {
            _settingsPrewarmWork = null;
            if (_settingsFindDisposed || _allowCloseForRuntimeShutdown || !IsVisible || WindowState == WindowState.Minimized)
            {
                return;
            }

            if (DeviceTabControl.Items.Count > 3 && DeviceTabControl.Items[3] is TabItem tab)
            {
                DeferredTabContentBehavior.EnsureContentLoaded(tab);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void CancelDeferredSettingsPrewarm()
    {
        _settingsPrewarmWork?.Abort();
        _settingsPrewarmWork = null;
    }

    private void ReleaseSettingsFindResources()
    {
        _settingsFindWork?.Abort();
        _settingsFindWork = null;
        _settingsFind?.Dispose();
        _settingsFind = null;
        _settingsFindScope = null;
    }

    internal void TrimHiddenSettingsTab()
    {
        CancelDeferredSettingsPrewarm();
        // Only unload SettingsTab when it is not the active tab so hiding to tray
        // while actively working in Settings preserves scroll, find state, and instant restore.
        if (_settingsFindDisposed || DeviceTabControl.SelectedIndex == 3) return;
        ReleaseSettingsFindResources();
        if (DeviceTabControl.Items.Count > 3 && DeviceTabControl.Items[3] is TabItem settingsTab)
        {
            DeferredTabContentBehavior.UnloadContent(settingsTab);
        }
    }

    internal void RestoreVisibleDeferredTabs()
    {
        if (_settingsFindDisposed) return;
        if (DeviceTabControl.SelectedItem is TabItem selectedTab)
        {
            DeferredTabContentBehavior.EnsureContentLoaded(selectedTab);
        }
        if (_appVm.IsSettingsTabActive && !string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
        {
            ScheduleSettingsFind();
        }
    }

    private DispatcherOperation? _languagePresentationWork;

    private void RefreshLocalizedSettingsPresentation()
    {
        _languagePresentationWork?.Abort();
        if (_settingsFindDisposed || Dispatcher.HasShutdownStarted) return;
        double offset = MainContentScrollViewer.VerticalOffset;
        bool atBottom = MainContentScrollViewer.ScrollableHeight > 0 && Math.Abs(offset - MainContentScrollViewer.ScrollableHeight) <= 1;
        int tab = _appVm.SelectedSettingsTabIndex;
        _settingsFindHighlightDismissed = true;
        if (_appVm.IsSettingsTabActive)
        {
            ScheduleSettingsFind();
            Services.Updates.AppUpdateLauncher.WarmStageInBackground();
        }
        _languagePresentationWork = Dispatcher.BeginInvoke(() =>
        {
            _languagePresentationWork = null;
            if (_settingsFindDisposed || tab != _appVm.SelectedSettingsTabIndex) return;
            MainContentScrollViewer.UpdateLayout();
            MainContentScrollViewer.ScrollToVerticalOffset(atBottom ? MainContentScrollViewer.ScrollableHeight : Math.Min(offset, MainContentScrollViewer.ScrollableHeight));
        }, DispatcherPriority.Loaded);
    }
}

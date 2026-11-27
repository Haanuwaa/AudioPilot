using System.Diagnostics;
using System.IO;
using System.Windows;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Helpers;

public enum AppHelpTopic { None, AudioDevices, Mixer, Settings, Routines, RoutineConditions, ForegroundAudio, MicrophoneHolds, ApplicationRouting, Updates, PackagedApps, RoutineTriggers }

/// <summary>Routes F1 to the current task, retaining the packaged quick reference as a launch fallback.</summary>
public static class AppHelp
{
    public static readonly DependencyProperty TopicProperty = DependencyProperty.RegisterAttached(
        "Topic", typeof(AppHelpTopic), typeof(AppHelp), new FrameworkPropertyMetadata(AppHelpTopic.None, FrameworkPropertyMetadataOptions.Inherits));

    public static AppHelpTopic GetTopic(DependencyObject element) => (AppHelpTopic)element.GetValue(TopicProperty);
    public static void SetTopic(DependencyObject element, AppHelpTopic value) => element.SetValue(TopicProperty, value);

    internal static string[] ResolveTargets(string baseDirectory, AppHelpTopic topic, Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string anchor = topic switch
        {
            AppHelpTopic.AudioDevices => "output-and-input-tabs",
            AppHelpTopic.Mixer => "mixer-view",
            AppHelpTopic.Settings => "settings-tab",
            AppHelpTopic.Routines => "routines",
            AppHelpTopic.RoutineConditions => "routine-conditions",
            AppHelpTopic.ForegroundAudio => "foreground-app-audio",
            AppHelpTopic.MicrophoneHolds => "push-to-talk-and-hold-to-mute",
            AppHelpTopic.ApplicationRouting => "app-only-routing-vs-default-switching",
            AppHelpTopic.Updates => "install-an-update",
            AppHelpTopic.PackagedApps => "packaged-apps",
            AppHelpTopic.RoutineTriggers => "routine-trigger-modes",
            _ => "start-here",
        };
        string guide = AppConstants.Links.UserGuideUrl + "#" + anchor;
        string local = Path.Combine(baseDirectory, AppConstants.Files.AboutFileName);
        return (fileExists ?? File.Exists)(local) ? [guide, local] : [guide];
    }

    internal static bool TryOpen(ILogger logger, string owner, AppHelpTopic topic)
    {
        foreach (string target in ResolveTargets(AppContext.BaseDirectory, topic))
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                logger.Debug(owner, () => $"help-opened | topic={topic} local={Path.IsPathRooted(target)}");
                return true;
            }
            catch (Exception ex)
            {
                logger.Warning(owner, () => $"help-open-failed | topic={topic} error={ex.GetType().Name}", nameof(TryOpen), ex);
            }
        }
        return false;
    }
}

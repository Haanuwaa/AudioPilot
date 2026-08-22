using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AudioPilot.Logging;
using AudioPilot.Models;
using Microsoft.Win32;

namespace AudioPilot.Platform
{
    public static partial class WindowThemeHelper
    {
        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const string DarkThemeDictionaryUri = "pack://application:,,,/AudioPilot;component/Themes/DarkTheme.xaml";
        private const string LightThemeDictionaryUri = "pack://application:,,,/AudioPilot;component/Themes/LightTheme.xaml";
        private static readonly object PaletteSignatureKey = new();
        private readonly record struct PaletteSignature(bool HighContrast, Color Window, Color Text, Color Highlight, Color HighlightText);
        private static readonly DependencyProperty LastAppliedEffectiveThemeProperty = DependencyProperty.RegisterAttached(
            "LastAppliedEffectiveTheme",
            typeof(AppTheme?),
            typeof(WindowThemeHelper),
            new PropertyMetadata(null));
        private static readonly DependencyProperty LastAppliedWindowHandleProperty = DependencyProperty.RegisterAttached(
            "LastAppliedWindowHandle",
            typeof(long),
            typeof(WindowThemeHelper),
            new PropertyMetadata(0L));
        private static readonly DependencyProperty LastAppliedHighContrastProperty = DependencyProperty.RegisterAttached(
            "LastAppliedHighContrast",
            typeof(bool),
            typeof(WindowThemeHelper),
            new PropertyMetadata(false));

        public static bool IsSystemDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int useLightTheme)
                {
                    return useLightTheme == 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("WindowThemeHelper", () => $"Failed to read system theme from registry: {ex.GetType().Name}");
            }
            return false;
        }

        public static AppTheme ResolveEffectiveTheme(AppTheme configuredTheme)
        {
            return configuredTheme switch
            {
                AppTheme.Dark => AppTheme.Dark,
                AppTheme.Light => AppTheme.Light,
                _ => IsSystemDarkTheme() ? AppTheme.Dark : AppTheme.Light
            };
        }

        /// <summary>Applies resource brushes immediately; native window chrome waits for SourceInitialized when no HWND exists yet.</summary>
        public static void ApplyTheme(Window window, AppTheme theme)
        {
            ArgumentNullException.ThrowIfNull(window);
            AppTheme effectiveTheme = ApplyApplicationThemeResources(theme);
            ApplyWindowChrome(window, effectiveTheme, SystemParameters.HighContrast);
        }

        internal static void ApplyWindowChrome(Window window, AppTheme effectiveTheme, bool highContrast)
        {
            try
            {
                bool useDarkMode = !highContrast && effectiveTheme == AppTheme.Dark;

                var windowHelper = new WindowInteropHelper(window);
                var handle = windowHelper.Handle;
                if (handle == IntPtr.Zero)
                {
                    Logger.Instance.Trace("WindowThemeHelper", "Deferred title-bar theme until the window handle is available.");
                    return;
                }

                if (HasWindowThemeAlreadyApplied(window, handle, effectiveTheme, highContrast))
                {
                    Logger.Instance.Trace("WindowThemeHelper", () => $"Skipped theme reapply for effectiveTheme={effectiveTheme}.");
                    return;
                }

                int useImmersiveDarkMode = useDarkMode ? 1 : 0;
                int result = 0;

                if (Environment.OSVersion.Version.Build >= 19041)
                {
                    result = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));
                }
                else if (Environment.OSVersion.Version.Build >= 17763)
                {
                    result = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
                else
                {
                    Logger.Instance.Debug("WindowThemeHelper", "OS version does not support immersive dark mode.");
                    RecordWindowTheme(window, handle, effectiveTheme, highContrast);
                    return;
                }

                if (result != 0)
                {
                    Logger.Instance.Warning("WindowThemeHelper", () => $"DwmSetWindowAttribute failed with HRESULT: 0x{result:X8}");
                }
                else
                {
                    RecordWindowTheme(window, handle, effectiveTheme, highContrast);
                    Logger.Instance.Debug("WindowThemeHelper", () => $"Applied theme={effectiveTheme} highContrast={highContrast} including window chrome.");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("WindowThemeHelper", () => $"Exception while applying window chrome: {ex.GetType().Name}", nameof(ApplyWindowChrome), ex);
            }
        }

        internal static AppTheme ApplyApplicationThemeResources(AppTheme theme)
        {
            try
            {
                AppTheme effectiveTheme = ResolveEffectiveTheme(theme);
                return Application.Current is { } app
                    ? ApplyThemeResources(app.Resources, effectiveTheme, SystemParameters.HighContrast)
                    : effectiveTheme;
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning(
                    "WindowThemeHelper",
                    () => $"application-theme-resource-apply-failed | error={ex.GetType().Name}",
                    nameof(ApplyApplicationThemeResources),
                    ex);
                return ResolveEffectiveTheme(theme);
            }
        }

        internal static AppTheme ApplyThemeResources(ResourceDictionary resources, AppTheme effectiveTheme, bool highContrast)
        {
            bool useDarkMode = !highContrast && effectiveTheme == AppTheme.Dark;
            string requestedThemeDictionaryUri = useDarkMode ? DarkThemeDictionaryUri : LightThemeDictionaryUri;
            var palette = highContrast
                ? new PaletteSignature(true, SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.HighlightColor, SystemColors.HighlightTextColor)
                : default;
            if (!HasExactThemeDictionaryState(resources, requestedThemeDictionaryUri, palette))
            {
                ResourceDictionary themeDict;
                try
                {
                    themeDict = new ResourceDictionary { Source = new Uri(requestedThemeDictionaryUri) };
                }
                catch (Exception ex)
                {
                    Logger.Instance.Warning(
                        "WindowThemeHelper",
                        "Failed to load requested theme dictionary, falling back to LightTheme.",
                        nameof(ApplyApplicationThemeResources),
                        ex);
                    effectiveTheme = AppTheme.Light;
                    requestedThemeDictionaryUri = LightThemeDictionaryUri;
                    themeDict = new ResourceDictionary { Source = new Uri(LightThemeDictionaryUri) };
                }

                if (highContrast)
                {
                    ApplyHighContrastPalette(themeDict);
                }

                for (int index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
                {
                    if (IsThemeDictionary(resources.MergedDictionaries[index]))
                    {
                        resources.MergedDictionaries.RemoveAt(index);
                    }
                }

                themeDict[PaletteSignatureKey] = palette;
                resources.MergedDictionaries.Insert(0, themeDict);
            }

            return effectiveTheme;
        }

        private static bool IsThemeDictionary(ResourceDictionary dictionary)
        {
            string? source = dictionary.Source?.OriginalString;
            return string.Equals(source, DarkThemeDictionaryUri, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, LightThemeDictionaryUri, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasExactThemeDictionaryState(ResourceDictionary resources, string expectedThemeDictionaryUri, PaletteSignature palette)
        {
            int themeDictionaryCount = 0;

            foreach (ResourceDictionary dictionary in resources.MergedDictionaries)
            {
                if (!IsThemeDictionary(dictionary))
                {
                    continue;
                }

                themeDictionaryCount++;
                if (!string.Equals(dictionary.Source?.OriginalString, expectedThemeDictionaryUri, StringComparison.OrdinalIgnoreCase)
                    || !Equals(dictionary[PaletteSignatureKey], palette))
                {
                    return false;
                }
            }

            return themeDictionaryCount == 1;
        }

        private static bool HasWindowThemeAlreadyApplied(Window window, IntPtr handle, AppTheme effectiveTheme, bool highContrast)
        {
            AppTheme? lastAppliedTheme = (AppTheme?)window.GetValue(LastAppliedEffectiveThemeProperty);
            long lastAppliedHandle = (long)window.GetValue(LastAppliedWindowHandleProperty);
            bool lastAppliedHighContrast = (bool)window.GetValue(LastAppliedHighContrastProperty);
            return lastAppliedTheme == effectiveTheme
                && lastAppliedHandle == handle.ToInt64()
                && lastAppliedHighContrast == highContrast;
        }

        private static void RecordWindowTheme(Window window, IntPtr handle, AppTheme effectiveTheme, bool highContrast)
        {
            window.SetValue(LastAppliedEffectiveThemeProperty, effectiveTheme);
            window.SetValue(LastAppliedWindowHandleProperty, handle.ToInt64());
            window.SetValue(LastAppliedHighContrastProperty, highContrast);
        }

        internal static void ApplyHighContrastPalette(ResourceDictionary dictionary)
        {
            SetBrushColor(dictionary, "WindowBackgroundBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "PanelBackgroundBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "ControlBackgroundBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "ControlHoverBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "ControlPressedBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "AccentBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "AccentHoverBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "AccentSelectionHoverBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "AccentTextBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "CheckMarkBrush", SystemColors.HighlightTextColor);
            SetBrushColor(dictionary, "TextBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "MutedTextBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "PlaceholderTextBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "BorderBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "AudioMeterTrackBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "AudioMeterInactiveBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "AudioMeterLowBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "AudioMeterMidBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "AudioMeterHighBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "AudioMeterPeakBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "TrayMenuHoverBackgroundBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "TrayMenuHoverForegroundBrush", SystemColors.HighlightTextColor);
            SetBrushColor(dictionary, "HotkeyConflictBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "HotkeyReservedBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "HotkeyFallbackBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "RoutineSucceededBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "RoutineWaitingBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "RoutineFailedBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "RoutineSkippedBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "ScrollBarTrackBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "ScrollBarTrackHoverBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "ScrollBarThumbBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "ScrollBarThumbHoverBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "SliderThumbBrush", SystemColors.HighlightTextColor);
            SetBrushColor(dictionary, "ResetButtonBackgroundBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "ResetButtonHoverBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "ResetButtonPressedBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "KeyboardFocusOuterBrush", SystemColors.HighlightColor);
            SetBrushColor(dictionary, "DialogIconBackgroundBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "DialogInformationBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "DialogSuccessBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "DialogWarningBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "DialogErrorBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "DialogQuestionBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "DialogPrimaryActionForegroundBrush", SystemColors.HighlightTextColor);
            SetBrushColor(dictionary, "DialogDestructiveActionForegroundBrush", SystemColors.HighlightTextColor);
            SetBrushColor(dictionary, "OverlayBackgroundBrush", SystemColors.WindowColor);
            SetBrushColor(dictionary, "OverlayPrimaryTextBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "OverlaySuccessTextBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "OverlayOutputDeviceBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "OverlayInputDeviceBrush", SystemColors.WindowTextColor);
            SetBrushColor(dictionary, "OverlayErrorDeviceBrush", SystemColors.WindowTextColor);
        }

        private static void SetBrushColor(ResourceDictionary dictionary, string key, Color color)
        {
            if (!dictionary.Contains(key) || dictionary[key] is not SolidColorBrush brush)
            {
                return;
            }

            if (brush.IsFrozen)
            {
                brush = brush.Clone();
                dictionary[key] = brush;
            }

            brush.Color = color;
            brush.Opacity = 1d;
        }
    }
}

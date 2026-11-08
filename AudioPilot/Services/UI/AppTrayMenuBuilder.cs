using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioPilot.Controls;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.Services.UI
{
    internal static partial class AppTrayMenuBuilder
    {
        private const uint MonitorDefaultToNearest = 2;
        private const double DefaultTrayMenuMaxHeight = 560d;
        private const double MinimumUsableTrayMenuHeight = 48d;
        private const double TrayMenuWorkAreaMargin = 24d;
        private const uint DefaultDpi = 96;

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromPoint(TrayMenuPoint point, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(nint monitor, ref TrayMenuMonitorInfo monitorInfo);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out TrayMenuPoint point);

        [LibraryImport("shcore.dll")]
        private static partial int GetDpiForMonitor(nint monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

        internal static IReadOnlyList<TrayMenuEntry> BuildTrayMenuEntries(
            bool isWindowVisible,
            string? toggleAppVisibilityHotkey,
            bool hasOutputCycle,
            string? outputDeviceName,
            string? outputSwitchHotkey,
            bool hasInputCycle,
            string? inputDeviceName,
            string? inputSwitchHotkey,
            IReadOnlyList<AudioRoutine>? routines,
            bool automaticPaused = false,
            bool hasAutomaticRoutines = false)
        {
            List<TrayMenuEntry> entries =
            [
                new(
                    isWindowVisible ? TrayMenuEntryKind.HideWindow : TrayMenuEntryKind.ShowWindow,
                    isWindowVisible ? "Hide AudioPilot" : "Show AudioPilot",
                    GestureText: FormatTrayMenuGesture(toggleAppVisibilityHotkey)),
            ];

            if (hasOutputCycle || hasInputCycle)
            {
                entries.Add(TrayMenuEntry.Separator);
                if (hasOutputCycle)
                {
                    entries.Add(new TrayMenuEntry(
                        TrayMenuEntryKind.SwitchOutput,
                        "Switch output",
                        NormalizeTrayMenuDetail(outputDeviceName),
                        FormatTrayMenuGesture(outputSwitchHotkey)));
                }

                if (hasInputCycle)
                {
                    entries.Add(new TrayMenuEntry(
                        TrayMenuEntryKind.SwitchInput,
                        "Switch input",
                        NormalizeTrayMenuDetail(inputDeviceName),
                        FormatTrayMenuGesture(inputSwitchHotkey)));
                }
            }

            if (routines is { Count: > 0 })
            {
                bool hasRoutineEntry = false;
                foreach (AudioRoutine routine in routines)
                {
                    if (string.IsNullOrWhiteSpace(routine.Id))
                    {
                        continue;
                    }

                    if (!hasRoutineEntry)
                    {
                        entries.Add(TrayMenuEntry.Separator);
                        hasRoutineEntry = true;
                    }

                    entries.Add(new TrayMenuEntry(
                        TrayMenuEntryKind.Routine,
                        string.IsNullOrWhiteSpace(routine.Name) ? "Unnamed routine" : routine.Name.Trim(),
                        GestureText: FormatTrayMenuGesture(routine.Hotkey),
                        RoutineId: routine.Id));
                }
            }

            entries.Add(TrayMenuEntry.Separator);
            entries.Add(new TrayMenuEntry(TrayMenuEntryKind.WindowsAudioSettings, "Windows audio settings", Children:
            [
                new(TrayMenuEntryKind.WindowsSoundSettings, "Sound settings"),
                new(TrayMenuEntryKind.WindowsVolumeMixer, "Volume mixer"),
                new(TrayMenuEntryKind.MicrophonePrivacy, "Microphone permissions"),
                new(TrayMenuEntryKind.WindowsBluetoothSettings, "Open Bluetooth settings"),
            ]));
            if (hasAutomaticRoutines)
            {
                entries.Add(new TrayMenuEntry(TrayMenuEntryKind.Automation, automaticPaused ? "Automatic routines (paused)" : "Automatic routines", Children:
                    automaticPaused ? [new(TrayMenuEntryKind.ResumeAutomatic, "Resume automatic routines"), new(TrayMenuEntryKind.PauseAutomaticCustom, "Change pause duration…")] :
                    [new(TrayMenuEntryKind.PauseAutomatic15, "Pause for 15 minutes"), new(TrayMenuEntryKind.PauseAutomatic30, "Pause for 30 minutes"),
                     new(TrayMenuEntryKind.PauseAutomatic60, "Pause for 1 hour"),
                     new(TrayMenuEntryKind.PauseAutomaticCustom, "Custom duration…"), new(TrayMenuEntryKind.PauseAutomatic, "Pause until resumed")]));
            }
            else if (automaticPaused)
            {
                entries.Add(new TrayMenuEntry(TrayMenuEntryKind.ResumeAutomatic, "Resume automatic routines"));
            }
            entries.Add(new TrayMenuEntry(TrayMenuEntryKind.Settings, "Settings"));
            entries.Add(new TrayMenuEntry(TrayMenuEntryKind.ResetWindowLayout, "Reset window layout"));
            entries.Add(new TrayMenuEntry(TrayMenuEntryKind.Exit, "Exit"));
            return entries;
        }

        internal static bool ShouldShowSwitchMenuItem(int cycleDeviceCount, bool hotkeysEnabled) =>
            hotkeysEnabled && cycleDeviceCount > 0;

        internal static double CalculateTrayMenuMaxHeight(int workAreaHeightPx, uint dpiY)
        {
            if (workAreaHeightPx <= 0 || dpiY == 0)
            {
                return DefaultTrayMenuMaxHeight;
            }

            double workAreaHeightDip = workAreaHeightPx * (double)DefaultDpi / dpiY;
            double availableHeight = Math.Max(MinimumUsableTrayMenuHeight, workAreaHeightDip - TrayMenuWorkAreaMargin);
            return Math.Min(DefaultTrayMenuMaxHeight, availableHeight);
        }

        internal static double ResolveTrayMenuMaxHeightForRuntime(Point? anchor = null)
        {
            TrayMenuPoint cursorPoint;
            if (anchor is Point point)
            {
                cursorPoint = new TrayMenuPoint { X = (int)point.X, Y = (int)point.Y };
            }
            else if (!GetCursorPos(out cursorPoint))
            {
                return DefaultTrayMenuMaxHeight;
            }

            nint monitor = MonitorFromPoint(cursorPoint, MonitorDefaultToNearest);
            if (monitor == nint.Zero)
            {
                return DefaultTrayMenuMaxHeight;
            }

            TrayMenuMonitorInfo monitorInfo = new() { Size = (uint)Marshal.SizeOf<TrayMenuMonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return DefaultTrayMenuMaxHeight;
            }

            uint dpiY = DefaultDpi;
            if (GetDpiForMonitor(monitor, MonitorDpiType.Effective, out _, out uint resolvedDpiY) == 0 && resolvedDpiY > 0)
            {
                dpiY = resolvedDpiY;
            }

            return CalculateTrayMenuMaxHeight(monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top, dpiY);
        }

        internal static MenuItem CreateTrayMenuItem(TrayMenuEntry entry)
        {
            MenuItem menuItem = new()
            {
                Header = CreateTrayMenuHeader(entry),
                Icon = CreateTrayMenuGlyph(GetGlyphGeometry(entry.Kind)),
                InputGestureText = entry.GestureText ?? string.Empty,
            };
            menuItem.SetResourceReference(FrameworkElement.StyleProperty, "AppTrayMenuItemStyle");
            if (GetLabel(entry) is { } label)
                LocalizationContext.SetText(menuItem, AutomationProperties.NameProperty, label);
            else
                AutomationProperties.SetName(menuItem, entry.Label);
            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                LocalizationContext.SetText(menuItem, AutomationProperties.HelpTextProperty,
                    new("Tray_CurrentDevice", GetDetail(entry.Detail)));
            }

            return menuItem;
        }

        private static StackPanel CreateTrayMenuHeader(TrayMenuEntry entry)
        {
            StackPanel panel = new()
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 230d,
                Orientation = Orientation.Vertical,
            };
            TextBlock primaryText = new() { TextTrimming = TextTrimming.CharacterEllipsis };
            if (GetLabel(entry) is { } label)
                LocalizationContext.SetText(primaryText, TextBlock.TextProperty, label);
            else
                primaryText.Text = entry.Label;
            primaryText.SetBinding(TextBlock.ForegroundProperty, CreateMenuItemForegroundBinding());
            panel.Children.Add(primaryText);
            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                TextBlock secondaryText = new()
                {
                    FontSize = 11d,
                    Opacity = 0.72d,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                if (GetDetail(entry.Detail) is LocalizedText detail)
                    LocalizationContext.SetText(secondaryText, TextBlock.TextProperty, detail);
                else
                    secondaryText.Text = entry.Detail;
                secondaryText.SetBinding(TextBlock.ForegroundProperty, CreateMenuItemForegroundBinding());
                panel.Children.Add(secondaryText);
            }

            return panel;
        }

        private static LocalizedText? GetLabel(TrayMenuEntry entry) => entry.Kind switch
        {
            TrayMenuEntryKind.HideWindow => new("Tray_HideAudioPilot"),
            TrayMenuEntryKind.ShowWindow => new("Tray_ShowAudioPilot"),
            TrayMenuEntryKind.SwitchOutput => new("Tray_SwitchOutput"),
            TrayMenuEntryKind.SwitchInput => new("Tray_SwitchInput"),
            TrayMenuEntryKind.WindowsAudioSettings => new("Tray_WindowsAudioSettings"),
            TrayMenuEntryKind.WindowsSoundSettings => new("Tray_SoundSettings"),
            TrayMenuEntryKind.WindowsVolumeMixer => new("Tray_VolumeMixer"),
            TrayMenuEntryKind.MicrophonePrivacy => new("Tray_MicrophonePermissions"),
            TrayMenuEntryKind.WindowsBluetoothSettings => new("Tray_OpenBluetoothSettings"),
            TrayMenuEntryKind.PauseAutomatic15 => new("Tray_PauseFor15Minutes"),
            TrayMenuEntryKind.PauseAutomatic30 => new("Tray_PauseFor30Minutes"),
            TrayMenuEntryKind.PauseAutomatic60 => new("Tray_PauseFor1Hour"),
            TrayMenuEntryKind.PauseAutomatic => new("Tray_PauseUntilResumed"),
            TrayMenuEntryKind.ResumeAutomatic => new("Tray_ResumeAutomaticRoutines"),
            TrayMenuEntryKind.Settings => new("Common_Settings"),
            TrayMenuEntryKind.ResetWindowLayout => new("Ui_ResetWindowLayout"),
            TrayMenuEntryKind.Exit => new("Tray_Exit"),
            TrayMenuEntryKind.Unavailable => new("Tray_AudioPilotMenuUnavailable"),
            TrayMenuEntryKind.Automation when entry.Label == "Automatic routines (paused)" => new("Tray_AutomaticRoutinesPaused"),
            TrayMenuEntryKind.Automation => new("Tray_AutomaticRoutines"),
            TrayMenuEntryKind.PauseAutomaticCustom when entry.Label == "Change pause duration…" => new("Tray_ChangePauseDuration"),
            TrayMenuEntryKind.PauseAutomaticCustom => new("Tray_CustomDuration"),
            TrayMenuEntryKind.Routine when entry.Label == "Unnamed routine" => new("Tray_UnnamedRoutine"),
            _ => null,
        };

        private static object? GetDetail(string? detail) => detail == "Unavailable" ? new LocalizedText("Tray_Unavailable") : detail;

        private static Path CreateTrayMenuGlyph(Geometry geometry)
        {
            Path path = new()
            {
                Data = geometry,
                Fill = Brushes.Transparent,
                Height = 16d,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.35d,
                Width = 16d,
            };
            path.SetBinding(Shape.StrokeProperty, CreateMenuItemForegroundBinding());
            return path;
        }

        private static Binding CreateMenuItemForegroundBinding() => new(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MenuItem), 1),
        };

        private static Geometry GetGlyphGeometry(TrayMenuEntryKind kind) => kind switch
        {
            TrayMenuEntryKind.ShowWindow or TrayMenuEntryKind.HideWindow => AppMenuGlyphs.Window,
            TrayMenuEntryKind.SwitchOutput => AppMenuGlyphs.Output,
            TrayMenuEntryKind.SwitchInput => AppMenuGlyphs.Input,
            TrayMenuEntryKind.Routine or TrayMenuEntryKind.Automation or TrayMenuEntryKind.ResumeAutomatic => AppMenuGlyphs.Routine,
            TrayMenuEntryKind.PauseAutomatic or TrayMenuEntryKind.PauseAutomatic15 or TrayMenuEntryKind.PauseAutomatic30 or TrayMenuEntryKind.PauseAutomatic60 or TrayMenuEntryKind.PauseAutomaticCustom => AppMenuGlyphs.Pause,
            TrayMenuEntryKind.Settings => AppMenuGlyphs.Settings,
            TrayMenuEntryKind.WindowsAudioSettings or TrayMenuEntryKind.WindowsSoundSettings or TrayMenuEntryKind.WindowsVolumeMixer
                or TrayMenuEntryKind.MicrophonePrivacy or TrayMenuEntryKind.WindowsBluetoothSettings => AppMenuGlyphs.Settings,
            TrayMenuEntryKind.Exit => AppMenuGlyphs.Exit,
            _ => AppMenuGlyphs.Window,
        };

        private static string NormalizeTrayMenuDetail(string? detail) =>
            string.IsNullOrWhiteSpace(detail) ? "Unavailable" : detail.Trim();

        private static string? FormatTrayMenuGesture(string? gesture)
        {
            string formatted = HotkeyDisplayFormatter.FormatCompact(gesture);
            return formatted.Length == 0 ? null : formatted;
        }

        internal static string ResolveDefaultDeviceName(Func<string?> getDeviceName)
        {
            try
            {
                string? deviceName = getDeviceName();
                return string.IsNullOrWhiteSpace(deviceName) ? "Unavailable" : deviceName.Trim();
            }
            catch
            {
                return "Unavailable";
            }
        }

        internal enum TrayMenuEntryKind
        {
            Separator,
            ShowWindow,
            HideWindow,
            SwitchOutput,
            SwitchInput,
            WindowsAudioSettings,
            WindowsSoundSettings,
            WindowsVolumeMixer,
            MicrophonePrivacy,
            WindowsBluetoothSettings,
            Routine,
            Automation,
            PauseAutomatic15,
            PauseAutomatic30,
            PauseAutomatic60,
            PauseAutomaticCustom,
            PauseAutomatic,
            ResumeAutomatic,
            Settings,
            ResetWindowLayout,
            Exit,
            Unavailable,
        }

        internal readonly record struct TrayMenuEntry(
            TrayMenuEntryKind Kind,
            string Label,
            string? Detail = null,
            string? GestureText = null,
            string? RoutineId = null,
            IReadOnlyList<TrayMenuEntry>? Children = null)
        {
            internal static TrayMenuEntry Separator => new(TrayMenuEntryKind.Separator, string.Empty);
        }

        private enum MonitorDpiType { Effective }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrayMenuPoint { internal int X; internal int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrayMenuRect { internal int Left; internal int Top; internal int Right; internal int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrayMenuMonitorInfo
        {
            internal uint Size;
            internal TrayMenuRect Monitor;
            internal TrayMenuRect WorkArea;
            internal uint Flags;
        }
    }
}

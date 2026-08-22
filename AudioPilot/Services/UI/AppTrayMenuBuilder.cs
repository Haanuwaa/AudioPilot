using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioPilot.Controls;
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
            IReadOnlyList<AudioRoutine>? routines)
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
            entries.Add(new TrayMenuEntry(TrayMenuEntryKind.Settings, "Settings"));
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

        internal static double ResolveTrayMenuMaxHeightForRuntime()
        {
            if (!GetCursorPos(out TrayMenuPoint cursorPoint))
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
                Header = CreateTrayMenuHeader(entry.Label, entry.Detail),
                Icon = CreateTrayMenuGlyph(GetGlyphGeometry(entry.Kind)),
                InputGestureText = entry.GestureText ?? string.Empty,
            };
            menuItem.SetResourceReference(FrameworkElement.StyleProperty, "AppTrayMenuItemStyle");
            AutomationProperties.SetName(menuItem, entry.Label);
            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                AutomationProperties.SetHelpText(menuItem, $"Current device: {entry.Detail}");
            }

            return menuItem;
        }

        private static StackPanel CreateTrayMenuHeader(string label, string? detail)
        {
            StackPanel panel = new()
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 230d,
                Orientation = Orientation.Vertical,
            };
            TextBlock primaryText = new() { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
            primaryText.SetBinding(TextBlock.ForegroundProperty, CreateMenuItemForegroundBinding());
            panel.Children.Add(primaryText);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                TextBlock secondaryText = new()
                {
                    FontSize = 11d,
                    Opacity = 0.72d,
                    Text = detail,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                secondaryText.SetBinding(TextBlock.ForegroundProperty, CreateMenuItemForegroundBinding());
                panel.Children.Add(secondaryText);
            }

            return panel;
        }

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
            TrayMenuEntryKind.Routine => AppMenuGlyphs.Routine,
            TrayMenuEntryKind.Settings => AppMenuGlyphs.Settings,
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
            Routine,
            Settings,
            Exit,
            Unavailable,
        }

        internal readonly record struct TrayMenuEntry(
            TrayMenuEntryKind Kind,
            string Label,
            string? Detail = null,
            string? GestureText = null,
            string? RoutineId = null)
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

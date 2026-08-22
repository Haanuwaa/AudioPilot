using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using Microsoft.Win32;

namespace AudioPilot
{
    public partial class OverlayWindow : Window
    {
        internal readonly record struct OverlayPixelPlacement(int Left, int Top, int Width, int Height);

        private const uint MonitorDefaultToNearest = 2;
        private const int WmDpiChanged = 0x02E0;
        private const int UserDefaultScreenDpi = 96;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly Logger _logger = Logger.Instance;
        private readonly DispatcherTimer _closeTimer;
        private readonly Storyboard? _fadeOutStoryboard;
        private readonly OverlayInlineBuilder _inlineBuilder;
        private readonly double _baseOverlayTextFontSize;
        private readonly double _baseStructuredHeaderTextFontSize;
        private readonly double _baseStructuredRowTextFontSize;
        private readonly double _baseMediaHeaderTextFontSize;
        private readonly double _baseMediaTitleTextFontSize;
        private readonly double _baseMediaArtistTextFontSize;
        private readonly double _baseMinHeightDip;
        private int _structuredOverlayRowCount;
        private OverlayPosition _position = OverlayPosition.TopRight;
        private int _stackIndex;
        private int? _stackOffsetPixels;
        private int _stackAdvancePixels = 1;
        private double _durationSeconds = AudioPilot.Constants.AppConstants.Timing.OverlayAutoHideSeconds;
        private bool _repositionQueued;
        private bool _refreshTargetMonitorQueued;
        private HwndSource? _hwndSource;
        private nint _overlayWindowHandle;
        private nint _targetMonitor;
        private string _targetMonitorSource = "none";
        private bool _isFadeInRunning;
        private DoubleAnimation? _fadeInAnimation;
        private bool _isFadeInCompletionHooked;
        private bool _isFadeOutCompletionHooked;

        [LibraryImport("user32.dll")]
        private static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT lpPoint);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [LibraryImport("Shcore.dll", SetLastError = true)]
        private static partial int GetScaleFactorForMonitor(nint hmonitor, out int scaleFactor);

        [LibraryImport("user32.dll")]
        private static partial uint GetDpiForWindow(nint hwnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        public OverlayWindow(string text)
        {
            InitializeComponent();

            ShowActivated = false;
            var emojiImageSourceFactory = new ColorEmojiImageSourceFactory();
            _inlineBuilder = new OverlayInlineBuilder(emojiImageSourceFactory);
            MediaTitleText.SetEmojiImageSourceFactory(emojiImageSourceFactory);
            MediaArtistText.SetEmojiImageSourceFactory(emojiImageSourceFactory);
            SetOverlayText(text);

            Opacity = 1.0;

            _closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(_durationSeconds)
            };
            _closeTimer.Tick += OnCloseTimerTick;

            _fadeOutStoryboard = Resources["FadeOutStoryboard"] as Storyboard;
            _baseOverlayTextFontSize = OverlayText.FontSize;
            _baseStructuredHeaderTextFontSize = StructuredHeaderText.FontSize;
            _baseStructuredRowTextFontSize = StructuredRow1ValueText.FontSize;
            _baseMediaHeaderTextFontSize = MediaHeaderText.FontSize;
            _baseMediaTitleTextFontSize = MediaTitleText.FontSize;
            _baseMediaArtistTextFontSize = MediaArtistText.FontSize;
            _baseMinHeightDip = MinHeight;

            SourceInitialized += OnSourceInitialized;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _overlayWindowHandle = _hwndSource?.Handle ?? nint.Zero;
            _hwndSource?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (msg == WmDpiChanged)
                {
                    if (lParam != IntPtr.Zero)
                    {
                        RECT suggestedRect = Marshal.PtrToStructure<RECT>(lParam);
                        POINT suggestedCenter = new()
                        {
                            x = suggestedRect.left + ((suggestedRect.right - suggestedRect.left) / 2),
                            y = suggestedRect.top + ((suggestedRect.bottom - suggestedRect.top) / 2)
                        };

                        nint suggestedMonitor = MonitorFromPoint(suggestedCenter, MonitorDefaultToNearest);
                        if (suggestedMonitor != nint.Zero)
                        {
                            _targetMonitor = suggestedMonitor;
                            _targetMonitorSource = "dpi-suggested-rect";
                        }
                    }

                    QueueReposition();
                }
            }
            catch (Exception ex)
            {
                TryLogPositioningFailure("dpi-change", ex);
            }

            return IntPtr.Zero;
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            QueueReposition(refreshTargetMonitor: true);
        }

        private void QueueReposition(bool refreshTargetMonitor = false)
        {
            if (refreshTargetMonitor)
            {
                _refreshTargetMonitorQueued = true;
            }

            if (!IsVisible || _repositionQueued)
            {
                return;
            }

            _repositionQueued = true;
            if (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
            {
                _repositionQueued = false;
                return;
            }

            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _repositionQueued = false;
                    try
                    {
                        if (!IsVisible)
                        {
                            return;
                        }

                        if (_refreshTargetMonitorQueued)
                        {
                            _refreshTargetMonitorQueued = false;
                            RefreshTargetMonitor();
                        }

                        PositionWindow();
                    }
                    catch (Exception ex)
                    {
                        _refreshTargetMonitorQueued = false;
                        TryLogPositioningFailure("queued-reposition", ex);
                    }
                }, DispatcherPriority.Loaded);
            }
            catch (InvalidOperationException ex) when (AppDispatcherHelper.IsDispatcherUnavailable(Dispatcher))
            {
                _repositionQueued = false;
                TryLogQueueRepositionShutdown(ex);
            }
        }

        private static void TryLogPositioningFailure(string stage, Exception exception)
        {
            try
            {
                _logger.Warning(
                    "OverlayWindow",
                    () => $"overlay-positioning-failed | stage={stage} exceptionType={exception.GetType().Name}");
            }
            catch (Exception)
            {
            }
        }

        private void TryLogQueueRepositionShutdown(Exception ex)
        {
            const string message = "Skipping queued reposition because dispatcher shutdown is in progress";

            try
            {
                _logger.Trace("OverlayWindow", $"{message} | error={ex.GetType().Name}", nameof(QueueReposition));
            }
            catch (Exception loggingEx)
            {
                LifecycleFallbackDiagnostics.Write("OverlayWindow", message, nameof(QueueReposition), ex, loggingEx);
            }
        }

        private void RefreshTargetMonitor()
        {
            if (TryGetTargetMonitor(out nint monitor, out string source))
            {
                _targetMonitor = monitor;
                _targetMonitorSource = source;
            }
        }

        public void UpdateContent(string text)
        {
            SetOverlayText(text);
            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        public void UpdateContent(OverlayActionStateKind stateKind, string text)
        {
            ResetOverlayText();

            string brushKey = stateKind switch
            {
                OverlayActionStateKind.Enabled => "OverlaySuccessTextBrush",
                _ => "OverlayErrorDeviceBrush"
            };

            AppendOverlayText(text, FontWeights.Bold, brushKey);

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        internal int StackAdvancePixels => _stackAdvancePixels;

        public void ApplyDisplayOptions(OverlayPosition position, double durationSeconds, int stackIndex, int? stackOffsetPixels = null)
        {
            _position = position;
            _stackIndex = Math.Max(0, stackIndex);
            _stackOffsetPixels = stackOffsetPixels.HasValue
                ? Math.Max(0, stackOffsetPixels.Value)
                : null;
            _durationSeconds = Math.Clamp(durationSeconds, 0.5, 10.0);
            _closeTimer.Interval = TimeSpan.FromSeconds(_durationSeconds);
        }

        public void UpdateContent(string header, string deviceName)
        {
            UpdateContent(OverlayDeviceKind.Output, header, deviceName);
        }

        public void UpdateContent(OverlayDeviceKind kind, string header, string deviceName)
        {
            string deviceBrushKey = kind switch
            {
                OverlayDeviceKind.Input => "OverlayInputDeviceBrush",
                OverlayDeviceKind.Error => "OverlayErrorDeviceBrush",
                _ => "OverlayOutputDeviceBrush"
            };

            ShowStructuredOverlay(header);

            if (kind == OverlayDeviceKind.Input &&
                deviceName.Contains('\n', StringComparison.Ordinal) &&
                TrySplitListenOverlayDeviceLines(deviceName, out string inputLine, out string outputLine))
            {
                AddStructuredOverlayRow(label: null, inputLine, "OverlayInputDeviceBrush");
                AddStructuredOverlayRow("To: ", outputLine[4..], "OverlayOutputDeviceBrush");
            }
            else
            {
                AddStructuredOverlayRow(label: null, deviceName, deviceBrushKey);
            }

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        public void UpdateRoutineContent(string header, string? outputDeviceName, string? inputDeviceName)
        {
            ShowStructuredOverlay(header);

            if (!string.IsNullOrWhiteSpace(outputDeviceName))
            {
                AddStructuredOverlayRow(label: null, outputDeviceName, "OverlayOutputDeviceBrush");
            }

            if (!string.IsNullOrWhiteSpace(inputDeviceName))
            {
                AddStructuredOverlayRow(label: null, inputDeviceName, "OverlayInputDeviceBrush");
            }

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        public void UpdateRoutinePartialContent(string header, string? outputDeviceName, string? inputDeviceName, string? failedOutputDeviceName, string? failedInputDeviceName)
        {
            ShowStructuredOverlay(header);

            if (!string.IsNullOrWhiteSpace(outputDeviceName))
            {
                AddStructuredOverlayRow("Output: ", outputDeviceName, "OverlayOutputDeviceBrush");
            }

            if (!string.IsNullOrWhiteSpace(failedOutputDeviceName))
            {
                AddStructuredOverlayRow("Output failed: ", failedOutputDeviceName, "OverlayErrorDeviceBrush");
            }

            if (!string.IsNullOrWhiteSpace(inputDeviceName))
            {
                AddStructuredOverlayRow("Input: ", inputDeviceName, "OverlayInputDeviceBrush");
            }

            if (!string.IsNullOrWhiteSpace(failedInputDeviceName))
            {
                AddStructuredOverlayRow("Input failed: ", failedInputDeviceName, "OverlayErrorDeviceBrush");
            }

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        private static bool TrySplitListenOverlayDeviceLines(string deviceName, out string inputLine, out string outputLine)
        {
            inputLine = string.Empty;
            outputLine = string.Empty;

            string[] parts = deviceName.Split('\n', 2, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                return false;
            }

            inputLine = parts[0];
            outputLine = parts[1];

            return !string.IsNullOrWhiteSpace(inputLine) &&
                   outputLine.StartsWith("To: ", StringComparison.OrdinalIgnoreCase);
        }

        private void SetOverlayText(string text)
        {
            ResetOverlayText();
            AppendOverlayText(text, FontWeights.Normal);
        }

        private void ResetOverlayText()
        {
            MediaOverlayPanel.Visibility = Visibility.Collapsed;
            StructuredOverlayPanel.Visibility = Visibility.Collapsed;
            OverlayText.Visibility = Visibility.Visible;
            OverlayText.Text = string.Empty;
            OverlayText.Inlines.Clear();
            ClearStructuredOverlay();
        }

        private void AppendOverlayText(string text, FontWeight fontWeight, string? brushKey = null)
        {
            _inlineBuilder.Append(OverlayText, text, fontWeight, brushKey);
        }

        private void ShowStructuredOverlay(string header)
        {
            ResetOverlayText();
            OverlayText.Visibility = Visibility.Collapsed;
            StructuredOverlayPanel.Visibility = Visibility.Visible;
            SetStructuredText(StructuredHeaderText, header, FontWeights.Bold, "OverlayPrimaryTextBrush");
        }

        private void AddStructuredOverlayRow(string? label, string value, string valueBrushKey)
        {
            if (_structuredOverlayRowCount >= 4)
            {
                return;
            }

            _structuredOverlayRowCount++;
            (Grid row, TextBlock labelText, TextBlock valueText) = GetStructuredOverlayRow(_structuredOverlayRowCount);
            row.Visibility = Visibility.Visible;

            if (string.IsNullOrWhiteSpace(label))
            {
                labelText.Visibility = Visibility.Collapsed;
                ClearStructuredText(labelText);
                valueText.TextAlignment = TextAlignment.Center;
            }
            else
            {
                labelText.Visibility = Visibility.Visible;
                SetStructuredText(labelText, label, FontWeights.SemiBold, "OverlayPrimaryTextBrush");
                valueText.TextAlignment = TextAlignment.Left;
            }

            SetStructuredText(valueText, value, FontWeights.Bold, valueBrushKey);
        }

        private (Grid Row, TextBlock LabelText, TextBlock ValueText) GetStructuredOverlayRow(int rowNumber)
        {
            return rowNumber switch
            {
                1 => (StructuredRow1, StructuredRow1LabelText, StructuredRow1ValueText),
                2 => (StructuredRow2, StructuredRow2LabelText, StructuredRow2ValueText),
                3 => (StructuredRow3, StructuredRow3LabelText, StructuredRow3ValueText),
                _ => (StructuredRow4, StructuredRow4LabelText, StructuredRow4ValueText)
            };
        }

        private void ClearStructuredOverlay()
        {
            _structuredOverlayRowCount = 0;
            ClearStructuredText(StructuredHeaderText);
            ClearStructuredOverlayRow(StructuredRow1, StructuredRow1LabelText, StructuredRow1ValueText);
            ClearStructuredOverlayRow(StructuredRow2, StructuredRow2LabelText, StructuredRow2ValueText);
            ClearStructuredOverlayRow(StructuredRow3, StructuredRow3LabelText, StructuredRow3ValueText);
            ClearStructuredOverlayRow(StructuredRow4, StructuredRow4LabelText, StructuredRow4ValueText);
        }

        private static void ClearStructuredOverlayRow(Grid row, TextBlock labelText, TextBlock valueText)
        {
            row.Visibility = Visibility.Collapsed;
            labelText.Visibility = Visibility.Collapsed;
            valueText.TextAlignment = TextAlignment.Center;
            ClearStructuredText(labelText);
            ClearStructuredText(valueText);
        }

        private void SetStructuredText(TextBlock target, string text, FontWeight fontWeight, string brushKey)
        {
            ClearStructuredText(target);
            _inlineBuilder.Append(target, text, fontWeight, brushKey);
        }

        private static void ClearStructuredText(TextBlock target)
        {
            target.Text = string.Empty;
            target.Inlines.Clear();
        }

        public void UpdateContent(string header, string title, string? artist)
        {
            OverlayText.Visibility = Visibility.Collapsed;
            OverlayText.Text = string.Empty;
            OverlayText.Inlines.Clear();
            StructuredOverlayPanel.Visibility = Visibility.Collapsed;
            ClearStructuredOverlay();

            SetStructuredText(MediaHeaderText, header, FontWeights.Bold, "OverlayPrimaryTextBrush");
            SetMediaOverlayText(MediaTitleText, title, FontWeights.Bold, "OverlayOutputDeviceBrush");
            SetMediaOverlayText(MediaArtistText, artist ?? string.Empty, FontWeights.Bold, "OverlayInputDeviceBrush");
            MediaArtistText.Visibility = string.IsNullOrWhiteSpace(artist)
                ? Visibility.Collapsed
                : Visibility.Visible;
            MediaOverlayPanel.Visibility = Visibility.Visible;

            Opacity = 1.0;
            StopFadeOut();
            UpdateLayout();
        }

        private static void SetMediaOverlayText(MediaOverlayInlineTextBlock target, string text, FontWeight fontWeight, string brushKey)
        {
            target.Text = text;
            target.FontWeight = fontWeight;
            target.SetResourceReference(MediaOverlayInlineTextBlock.ForegroundProperty, brushKey);
        }

        private void OnCloseTimerTick(object? sender, EventArgs e)
        {
            _closeTimer.Stop();
            BeginFadeOutAndClose();
        }

        private void StopFadeOut()
        {
            if (_fadeInAnimation != null && _isFadeInCompletionHooked)
            {
                _fadeInAnimation.Completed -= OnFadeInCompleted;
                _isFadeInCompletionHooked = false;
            }

            _fadeInAnimation = null;

            if (_fadeOutStoryboard != null && _isFadeOutCompletionHooked)
            {
                _fadeOutStoryboard.Completed -= OnFadeCompleted;
                _isFadeOutCompletionHooked = false;
            }

            if (_fadeOutStoryboard != null)
            {
                _fadeOutStoryboard.Stop(this);
                _fadeOutStoryboard.Remove(this);
            }

            BeginAnimation(OpacityProperty, null);
            _isFadeInRunning = false;
        }

        private void BeginFadeIn()
        {
            _isFadeInRunning = true;

            _fadeInAnimation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(AppConstants.Overlay.FadeInDurationMs),
                FillBehavior = FillBehavior.Stop
            };

            _fadeInAnimation.Completed += OnFadeInCompleted;
            _isFadeInCompletionHooked = true;

            BeginAnimation(OpacityProperty, _fadeInAnimation, HandoffBehavior.SnapshotAndReplace);
        }

        private void OnFadeInCompleted(object? sender, EventArgs e)
        {
            if (_fadeInAnimation != null && _isFadeInCompletionHooked)
            {
                _fadeInAnimation.Completed -= OnFadeInCompleted;
                _isFadeInCompletionHooked = false;
            }

            _fadeInAnimation = null;
            _isFadeInRunning = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
        }

        private void BeginFadeOutAndClose()
        {
            if (_fadeOutStoryboard != null)
            {
                if (!_isFadeOutCompletionHooked)
                {
                    _fadeOutStoryboard.Completed += OnFadeCompleted;
                    _isFadeOutCompletionHooked = true;
                }

                _fadeOutStoryboard.Begin(this, HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                Hide();
            }
        }

        private void OnFadeCompleted(object? sender, EventArgs e)
        {
            if (_fadeOutStoryboard != null && _isFadeOutCompletionHooked)
            {
                _fadeOutStoryboard.Completed -= OnFadeCompleted;
                _isFadeOutCompletionHooked = false;
            }

            Hide();
        }

        private void PositionWindow()
        {
            nint overlayWindowHandle = _overlayWindowHandle;
            if (overlayWindowHandle == nint.Zero)
            {
                return;
            }

            if (_targetMonitor == nint.Zero)
            {
                RefreshTargetMonitor();
            }

            nint monitor = _targetMonitor;
            string source = _targetMonitorSource;

            if (monitor == nint.Zero ||
                !TryGetMonitorRectsPixels(monitor, out Rect workAreaPixels, out Rect monitorBoundsPixels))
            {
                return;
            }

            if (!TryGetMonitorScale(monitor, overlayWindowHandle, out double targetScaleX, out double targetScaleY))
            {
                return;
            }

            OverlayText.FontSize = _baseOverlayTextFontSize;
            StructuredHeaderText.FontSize = _baseStructuredHeaderTextFontSize;
            StructuredRow1LabelText.FontSize = _baseStructuredRowTextFontSize;
            StructuredRow1ValueText.FontSize = _baseStructuredRowTextFontSize;
            StructuredRow2LabelText.FontSize = _baseStructuredRowTextFontSize;
            StructuredRow2ValueText.FontSize = _baseStructuredRowTextFontSize;
            StructuredRow3LabelText.FontSize = _baseStructuredRowTextFontSize;
            StructuredRow3ValueText.FontSize = _baseStructuredRowTextFontSize;
            StructuredRow4LabelText.FontSize = _baseStructuredRowTextFontSize;
            StructuredRow4ValueText.FontSize = _baseStructuredRowTextFontSize;
            MediaHeaderText.FontSize = _baseMediaHeaderTextFontSize;
            MediaTitleText.FontSize = _baseMediaTitleTextFontSize;
            MediaArtistText.FontSize = _baseMediaArtistTextFontSize;
            MinHeight = _baseMinHeightDip;

            double workLeftPx = workAreaPixels.Left;
            double workTopPx = workAreaPixels.Top;
            double workRightPx = workAreaPixels.Right;
            double workBottomPx = workAreaPixels.Bottom;
            int workWidthPx = Math.Max(0, (int)Math.Round(workAreaPixels.Width));
            int workHeightPx = Math.Max(0, (int)Math.Round(workAreaPixels.Height));
            if (workWidthPx <= 0 || workHeightPx <= 0)
            {
                return;
            }

            double monitorLeftPx = monitorBoundsPixels.Left;
            double monitorTopPx = monitorBoundsPixels.Top;
            double monitorRightPx = monitorBoundsPixels.Right;
            double monitorBottomPx = monitorBoundsPixels.Bottom;
            int monitorWidthPx = Math.Max(0, (int)Math.Round(monitorBoundsPixels.Width));
            int monitorHeightPx = Math.Max(0, (int)Math.Round(monitorBoundsPixels.Height));
            if (monitorWidthPx <= 0 || monitorHeightPx <= 0)
            {
                return;
            }

            int overlayMarginPxX = Math.Max(1, (int)Math.Round(AppConstants.Overlay.MarginDip * targetScaleX));
            int overlayMarginPxY = Math.Max(1, (int)Math.Round(AppConstants.Overlay.MarginDip * targetScaleY));
            int overlayStackGapPx = Math.Max(1, (int)Math.Round(AppConstants.Overlay.StackGapDip * targetScaleY));
            int overlayTargetWidthPx = AppConstants.Overlay.TargetWidthPx;
            overlayMarginPxX = Math.Min(overlayMarginPxX, Math.Max(0, (workWidthPx - 1) / 2));
            overlayMarginPxY = Math.Min(overlayMarginPxY, Math.Max(0, (workHeightPx - 1) / 2));

            int availableWidthPx = Math.Max(1, workWidthPx - (overlayMarginPxX * 2));
            int availableHeightPx = Math.Max(1, workHeightPx - (overlayMarginPxY * 2));
            int targetWidthPx = Math.Min(overlayTargetWidthPx, availableWidthPx);

            double widthDip = targetWidthPx / targetScaleX;
            MaxWidth = availableWidthPx / targetScaleX;
            MaxHeight = availableHeightPx / targetScaleY;
            Width = widthDip;
            UpdateLayout();

            double measuredHeightDip = ActualHeight > 0 ? ActualHeight : Math.Max(MinHeight, 1d);
            int measuredHeightPx = Math.Max(1, (int)Math.Ceiling(measuredHeightDip * targetScaleY));
            int targetHeightPx = Math.Min(Math.Max(1, measuredHeightPx), availableHeightPx);

            OverlayPixelPlacement placement = CalculateOverlayPlacement(
                workAreaPixels,
                targetWidthPx,
                targetHeightPx,
                overlayMarginPxX,
                overlayMarginPxY,
                overlayStackGapPx,
                _position,
                _stackIndex,
                _stackOffsetPixels);
            targetWidthPx = placement.Width;
            targetHeightPx = placement.Height;
            int finalLeftPx = placement.Left;
            int finalTopPx = placement.Top;
            _stackAdvancePixels = checked(placement.Height + overlayStackGapPx);

            SetWindowPos(
                overlayWindowHandle,
                nint.Zero,
                finalLeftPx,
                finalTopPx,
                targetWidthPx,
                targetHeightPx,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace(
                    "OverlayWindow",
                    $"{AppConstants.Audio.LogEvents.Overlay.Position} | source={source} anchor={_position} monitor=0x{monitor.ToInt64():X} dpiScale={targetScaleX:F2},{targetScaleY:F2} monitorPx={monitorLeftPx:F0},{monitorTopPx:F0},{monitorWidthPx},{monitorHeightPx} workPx={workLeftPx:F0},{workTopPx:F0},{workWidthPx},{workHeightPx} windowPx={targetWidthPx}x{targetHeightPx} finalPx={finalLeftPx},{finalTopPx}",
                    nameof(PositionWindow));
            }
        }

        internal static OverlayPixelPlacement CalculateOverlayPlacement(
            Rect workAreaPixels,
            int desiredWidthPx,
            int desiredHeightPx,
            int marginPxX,
            int marginPxY,
            int stackGapPx,
            OverlayPosition position,
            int stackIndex,
            int? stackOffsetPx = null)
        {
            int workWidthPx = Math.Max(1, (int)Math.Round(workAreaPixels.Width));
            int workHeightPx = Math.Max(1, (int)Math.Round(workAreaPixels.Height));
            int effectiveMarginPxX = Math.Min(Math.Max(0, marginPxX), Math.Max(0, (workWidthPx - 1) / 2));
            int effectiveMarginPxY = Math.Min(Math.Max(0, marginPxY), Math.Max(0, (workHeightPx - 1) / 2));
            int availableWidthPx = Math.Max(1, workWidthPx - (effectiveMarginPxX * 2));
            int availableHeightPx = Math.Max(1, workHeightPx - (effectiveMarginPxY * 2));
            int widthPx = Math.Min(Math.Max(1, desiredWidthPx), availableWidthPx);
            int heightPx = Math.Min(Math.Max(1, desiredHeightPx), availableHeightPx);
            double workLeftPx = workAreaPixels.Left;
            double workTopPx = workAreaPixels.Top;
            double workRightPx = workAreaPixels.Right;
            double workBottomPx = workAreaPixels.Bottom;

            double leftPx = position switch
            {
                OverlayPosition.TopLeft or OverlayPosition.BottomLeft => workLeftPx + effectiveMarginPxX,
                OverlayPosition.TopCenter or OverlayPosition.BottomCenter or OverlayPosition.Center =>
                    workLeftPx + ((workWidthPx - widthPx) / 2.0),
                _ => workRightPx - widthPx - effectiveMarginPxX,
            };
            double topPx = position switch
            {
                OverlayPosition.TopLeft or OverlayPosition.TopCenter or OverlayPosition.TopRight => workTopPx + effectiveMarginPxY,
                OverlayPosition.Center => workTopPx + ((workHeightPx - heightPx) / 2.0),
                _ => workBottomPx - heightPx - effectiveMarginPxY,
            };

            double effectiveStackOffsetPx = stackOffsetPx.HasValue
                ? Math.Max(0, stackOffsetPx.Value)
                : Math.Max(0, stackIndex) * (heightPx + Math.Max(0, stackGapPx));
            bool anchorFromBottom = position is OverlayPosition.BottomLeft or OverlayPosition.BottomCenter or OverlayPosition.BottomRight;
            topPx = anchorFromBottom ? topPx - effectiveStackOffsetPx : topPx + effectiveStackOffsetPx;

            double minLeftPx = workLeftPx + effectiveMarginPxX;
            double maxLeftPx = workRightPx - widthPx - effectiveMarginPxX;
            double minTopPx = workTopPx + effectiveMarginPxY;
            double maxTopPx = workBottomPx - heightPx - effectiveMarginPxY;
            return new OverlayPixelPlacement(
                (int)Math.Round(ClampToRange(leftPx, minLeftPx, maxLeftPx)),
                (int)Math.Round(ClampToRange(topPx, minTopPx, maxTopPx)),
                widthPx,
                heightPx);
        }

        private static bool TryGetMonitorScale(nint monitor, nint overlayWindowHandle, out double scaleX, out double scaleY)
        {
            scaleX = 1.0;
            scaleY = 1.0;

            if (GetScaleFactorForMonitor(monitor, out int scaleFactor) == 0 && scaleFactor >= 100 && scaleFactor <= 500)
            {
                scaleX = scaleFactor / 100.0;
                scaleY = scaleX;
                return true;
            }

            if (overlayWindowHandle != nint.Zero)
            {
                uint windowDpi = GetDpiForWindow(overlayWindowHandle);
                if (windowDpi > 0)
                {
                    scaleX = windowDpi / (double)UserDefaultScreenDpi;
                    scaleY = windowDpi / (double)UserDefaultScreenDpi;
                    return true;
                }
            }

            return false;
        }

        private static double ClampToRange(double value, double min, double max)
        {
            if (max < min)
            {
                return min;
            }

            return Math.Min(Math.Max(value, min), max);
        }

        private bool TryGetTargetMonitor(out nint monitor, out string source)
        {
            monitor = nint.Zero;
            source = "none";

            nint overlayWindowHandle = _overlayWindowHandle;

            if (GetCursorPos(out POINT cursorPoint))
            {
                monitor = MonitorFromPoint(cursorPoint, MonitorDefaultToNearest);
                if (monitor != nint.Zero)
                {
                    source = "cursor";
                    return true;
                }
            }

            nint foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != nint.Zero && foregroundWindow != overlayWindowHandle)
            {
                monitor = MonitorFromWindow(foregroundWindow, MonitorDefaultToNearest);
                if (monitor != nint.Zero)
                {
                    source = "foreground-window";
                    return true;
                }
            }

            if (overlayWindowHandle != nint.Zero)
            {
                monitor = MonitorFromWindow(overlayWindowHandle, MonitorDefaultToNearest);
                if (monitor != nint.Zero)
                {
                    source = "overlay-window";
                    return true;
                }
            }

            nint originMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MonitorDefaultToNearest);
            if (originMonitor != nint.Zero)
            {
                monitor = originMonitor;
                source = "origin-fallback";
                return true;
            }

            return false;
        }

        private static bool TryGetMonitorRectsPixels(nint monitor, out Rect workArea, out Rect monitorBounds)
        {
            workArea = Rect.Empty;
            monitorBounds = Rect.Empty;

            if (monitor == nint.Zero)
            {
                return false;
            }

            MONITORINFO info = new()
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
            };

            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            double workWidth = info.rcWork.right - info.rcWork.left;
            double workHeight = info.rcWork.bottom - info.rcWork.top;
            if (workWidth <= 0 || workHeight <= 0)
            {
                return false;
            }

            double monitorWidth = info.rcMonitor.right - info.rcMonitor.left;
            double monitorHeight = info.rcMonitor.bottom - info.rcMonitor.top;
            if (monitorWidth <= 0 || monitorHeight <= 0)
            {
                return false;
            }

            workArea = new Rect(info.rcWork.left, info.rcWork.top, workWidth, workHeight);
            monitorBounds = new Rect(info.rcMonitor.left, info.rcMonitor.top, monitorWidth, monitorHeight);
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public void ShowOverlay()
        {
            _closeTimer?.Stop();
            StopFadeOut();
            RefreshTargetMonitor();

            bool wasHidden = !IsVisible || Visibility != Visibility.Visible;

            if (wasHidden)
            {
                Opacity = 0.0;
            }

            if (!IsVisible)
            {
                Show();
            }
            else if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }

            PositionWindow();

            if (wasHidden && !_isFadeInRunning)
            {
                BeginFadeIn();
            }
            else if (!wasHidden)
            {
                Opacity = 1.0;
            }

            _closeTimer?.Start();
        }

        public void Cleanup()
        {
            SourceInitialized -= OnSourceInitialized;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;

            if (_closeTimer != null)
            {
                _closeTimer.Stop();
                _closeTimer.Tick -= OnCloseTimerTick;
            }

            StopFadeOut();
        }

        protected override void OnClosed(EventArgs e)
        {
            Cleanup();
            base.OnClosed(e);
        }
    }
}

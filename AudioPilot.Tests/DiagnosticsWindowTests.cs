using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class DiagnosticsWindowTests
{
    private static readonly string[] InvalidPauseDurations = ["", "0", "1441", "-1", "1.5", "99999999", "later"];
    private static readonly string[] ValidPauseDurations = ["1", "45", "1440", " 90 "];
    private static readonly string[] DiagnosticActionButtons = ["CopyDiagnosticsButton", "RefreshButton", "ExportButton"];
    private static readonly string[] DiagnosticDetailExpanders = ["OperationDetails", "GuidanceDetails", "HotkeyDetails", "ProcessMonitorDetails", "EnvironmentDetails", "ProcessDetails", "BuildDetails"];

    [Fact]
    public Task ReviewAdviceUsesTheCapturedSnapshot_AndIgnoresClicksAfterClose() => SharedStaDispatcherHost.RunAsync(() =>
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ResourceDictionary previous = app.Resources;
        app.Resources = CreateResources(AppTheme.Dark);
        int reads = 0, reviews = 0;
        DiagnosticReviewTarget? target = null;
        var window = new DiagnosticsWindow(() =>
        {
            reads++;
            return new(new(DateTimeOffset.UtcNow, "gui", "inactive", null, null, null, 0,
                Logging: new("None", true, 0, null, null, null)), "No history");
        }, _ => Task.FromResult(new DiagnosticBundleSaveResult(true)), TestContext.Current.CancellationToken,
            reviewSetting: value => { reviews++; target = value; });
        try
        {
            var guidance = (Expander)window.FindName("GuidanceDetails");
            Assert.False(guidance.IsExpanded);
            guidance.IsExpanded = true;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(640, 540)); content.Arrange(new Rect(0, 0, 640, 540)); content.UpdateLayout();
            var rows = (ItemsControl)window.FindName("AdviceRows");
            var presenter = Assert.IsType<ContentPresenter>(rows.ItemContainerGenerator.ContainerFromIndex(0));
            var button = (Button)presenter.ContentTemplate.FindName("ReviewAdviceButton", presenter);
            Assert.Equal(Visibility.Visible, button.Visibility);
            WpfVisualAssertions.NoBindingErrors(content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new DiagnosticReviewTarget(3, "Log level"), target);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, reviews);
            Assert.Equal(1, reads);
        }
        finally { window.Close(); app.Resources = previous; }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("exported", false, false, false, "Review its contents")]
    [InlineData("missing", true, false, true, "missing log files were expected")]
    [InlineData("missing", false, false, false, "some diagnostic data was unavailable")]
    [InlineData("unavailable", true, false, false, "some diagnostic data was unavailable")]
    [InlineData("exported", false, true, false, "some diagnostic data was unavailable")]
    [InlineData("missing", true, true, false, "some diagnostic data was unavailable")]
    public Task BundleFeedbackDistinguishesPartialDataFromExpectedAbsentLogs(string entryStatus, bool disabled, bool collectionFailed, bool expected, string message)
        => SharedStaDispatcherHost.RunAsync(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(AppTheme.Dark);
            var export = new DiagnosticBundleExportResult("unused.zip", false, entryStatus != "exported" || collectionFailed, 4, 100,
                [new(entryStatus, "logs", "logs/", 0)], collectionFailed ? [new("diagnostics/media-status.json", "diagnostics-bundle-media-status-unavailable", "IOException")] : []);
            var result = DiagnosticBundleSaveResult.FromExport(export, disabled);
            Assert.Equal(expected, result.ExpectedMissingLogs);
            var window = new DiagnosticsWindow(() => new(new(DateTimeOffset.UtcNow, "gui", "inactive", null, null, null, 0), "No history"),
                _ => Task.FromResult(result), TestContext.Current.CancellationToken, chooseExportPath: () => "unused.zip");
            try
            {
                ((Button)window.FindName("ExportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(message, ((TextBlock)window.FindName("Status")).Text, StringComparison.Ordinal);
            }
            finally { window.Close(); app.Resources = previous; }
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task OverviewHistoryAndResourcesUseOneRedactedSnapshotWithoutPolling(AppTheme theme)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(theme);
            int reads = 0;
            string? copied = null;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var successful = DiagnosticsOperation.FromEntry(new("successful", now, ExecutionHistoryKind.Switch, "picker",
                "switch-output-device", true, false, "Device switch completed.", OutputDeviceName: "Private device"));
            var failed = DiagnosticsOperation.FromEntry(new("failed", now, ExecutionHistoryKind.Mute, "hotkey",
                "mute-mic-set", false, false, "Mute change partially applied.", DiagCode: "mute-change-partial", TraceId: "mute-trace"));
            var health = new RuntimeHealthSnapshot(now, "gui", "inactive", null,
                new(true, false, false, 2, true, true, false, [new(1, "ExternalConflict", null, 1409)]),
                new(true, false, 0, 0, [], HasAutomaticRoutines: false), 0,
                new("Available", "Unavailable", true, false, false),
                new(true, true, true, now, false, "UnauthorizedAccessException", false),
                new("Info", true, 0, now, false, "IOException"),
                new(now, false, 2, 1));
            var window = new DiagnosticsWindow(() =>
            {
                reads++;
                return new(health, "Complete redacted history", Process: new(42, "X64", 100, 1048576, reads * 2097152, 100, 20 + reads, 3, 1, 0, 0, TimeSpan.FromMinutes(5)),
                    Operations: [successful, failed]);
            }, _ => Task.FromResult(new DiagnosticBundleSaveResult(true)), TestContext.Current.CancellationToken, copyText: text => copied = text);
            try
            {
                foreach (string name in DiagnosticDetailExpanders)
                    Assert.False(((Expander)window.FindName(name)).IsExpanded);
                var overview = ((ItemsControl)window.FindName("HealthRows")).Items.Cast<KeyValuePair<string, string>>().ToArray();
                Assert.Contains(overview, row => row.Key == "Hotkeys" && row.Value.Contains("1 registration issue", StringComparison.Ordinal));
                Assert.Contains(overview, row => row.Key == "Automatic routines" && row.Value == "None configured");
                Assert.Contains(overview, row => row.Key == "Logging" && row.Value.Contains("failed", StringComparison.Ordinal));
                Assert.DoesNotContain(overview, row => row.Key == "Process monitor");
                var list = (ListBox)window.FindName("OperationsList");
                var copyOperation = (Button)window.FindName("CopyOperationButton");
                Assert.False(copyOperation.IsEnabled);
                Assert.Equal(2, list.Items.Count);
                ((ComboBox)window.FindName("HistoryFilter")).SelectedIndex = 1;
                Assert.Single(list.Items);
                list.SelectedIndex = 0;
                Assert.True(copyOperation.IsEnabled);
                copyOperation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("mute-trace", copied, StringComparison.Ordinal);
                Assert.DoesNotContain("successful", copied, StringComparison.Ordinal);
                Assert.DoesNotContain("Complete redacted history", copied, StringComparison.Ordinal);
                Assert.Equal(new DiagnosticReviewTarget(1), ((DiagnosticAdvice)((ContentControl)window.FindName("OperationAdvice")).Content).Target);
                Assert.Contains("mute-change-partial", ((TextBox)window.FindName("Report")).Text, StringComparison.Ordinal);
                Assert.False(((Expander)window.FindName("OperationDetails")).IsExpanded);
                Assert.Equal(1, reads);
                ((ComboBox)window.FindName("HistoryFilter")).SelectedIndex = 0;
                Assert.Equal("failed", ((DiagnosticsOperation)list.SelectedItem).Id);
                Assert.Contains("mute-change-partial", ((TextBox)window.FindName("Report")).Text, StringComparison.Ordinal);
                ((ComboBox)window.FindName("HistoryFilter")).SelectedIndex = 1;
                ((Button)window.FindName("CopyDiagnosticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Complete redacted history", copied, StringComparison.Ordinal);
                Assert.Contains("AllocatedBytes", copied, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Private device", successful.Details, StringComparison.Ordinal);
                ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, reads);
                Assert.Single(list.Items);
                Assert.Contains($"{2d:+0.0} MiB", ((TextBlock)window.FindName("ResourceComparison")).Text, StringComparison.Ordinal);
                Assert.Contains("+1", ((TextBlock)window.FindName("ResourceComparison")).Text, StringComparison.Ordinal);
                Assert.Contains(((ItemsControl)window.FindName("BuildRows")).Items.Cast<KeyValuePair<string, string>>(),
                    row => row.Key == "Deployment" && row.Value == AppBuildInformation.Current.Deployment);
                Assert.True(AppBuildInformation.Current.Deployment is "Framework-dependent" or "Self-contained");
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(440, 440));
                content.Arrange(new Rect(0, 0, 440, 440));
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                WpfVisualAssertions.NoBindingErrors(content);
                if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } directory)
                {
                    ((ComboBox)window.FindName("HistoryFilter")).SelectedIndex = 0;
                    content.Measure(new Size(640, 540));
                    content.Arrange(new Rect(0, 0, 640, 540));
                    ((ScrollViewer)window.FindName("HealthScrollViewer")).ScrollToTop();
                    await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                    content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(640, 540, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var drawing = background.RenderOpen())
                        drawing.DrawRectangle((Brush)app.Resources["WindowBackgroundBrush"], null, new Rect(0, 0, 640, 540));
                    bitmap.Render(background);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(directory);
                    using var file = File.Create(Path.Combine(directory, $"diagnostics-overview-{theme}.png"));
                    encoder.Save(file);
                }
            }
            finally { window.Close(); app.Resources = previous; }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task DiagnosticsRefreshesOnlyOnRequest_AndUsesThemeWithoutShowingAWindow(AppTheme theme)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(theme);
            int reads = 0;
            int opens = 0;
            string? copied = null;
            var lastWritten = new DateTime(2026, 10, 28, 12, 34, 0, DateTimeKind.Local);
            var window = new DiagnosticsWindow(() => new(new RuntimeHealthSnapshot(DateTimeOffset.UtcNow, "gui", "inactive", null,
                new HotkeyRuntimeSnapshot(true, false, false, 3, true, true, false,
                    [new(AppConstants.Hotkeys.OutputSwitchHotkeyId, "ExternalConflict", null, 1409),
                     new(AppConstants.Hotkeys.MediaPlayPauseId, "Fallback", "hook", 0), new(10001, "ParseError", null, 0)]),
                new RoutineWorkSnapshot(true, false, 0, 0, []), 0),
                $"Snapshot {++reads}\nRecent operations: none", lastWritten,
                new RuntimeProcessSnapshot(42, "X64", 32768, 16384, 65536, 49152, 75, 8, 4, 2, 1)), _ => Task.FromResult(new DiagnosticBundleSaveResult(true)), TestContext.Current.CancellationToken,
                copyText: text => copied = text, openAbout: () => { opens++; return true; });
            try
            {
                var report = (TextBox)window.FindName("Report");
                Assert.Equal(1, reads);
                Assert.Contains(((ItemsControl)window.FindName("SettingsRows")).Items.Cast<KeyValuePair<string, string>>(),
                    row => row.Key == "Settings last written" && row.Value == lastWritten.ToString("g"));
                Assert.Contains("Snapshot 1", report.Text);
                ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, reads);
                Assert.Contains("Snapshot 2", report.Text);
                Assert.False(window.IsVisible);
                Assert.True(report.IsReadOnly);
                Assert.Equal(((SolidColorBrush)app.Resources["WindowBackgroundBrush"]).Color, ((SolidColorBrush)window.Background).Color);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(640, 540));
                content.Arrange(new Rect(0, 0, 640, 540));
                content.UpdateLayout();
                var details = (Expander)window.FindName("HotkeyDetails");
                Assert.Same(app.Resources["AppExpanderStyle"], details.Style);
                var build = (Expander)window.FindName("BuildDetails");
                Assert.Same(app.Resources["AppExpanderStyle"], build.Style);
                Assert.False(build.IsExpanded);
                Assert.False(details.IsExpanded);
                var resources = (Expander)window.FindName("ProcessDetails");
                Assert.False(resources.IsExpanded);
                Assert.False(((Expander)window.FindName("ProcessMonitorDetails")).IsExpanded);
                Assert.False(((Button)window.FindName("TestProcessMonitorButton")).IsEnabled);
                var resourceRows = ((ItemsControl)window.FindName("ProcessRows")).Items.Cast<KeyValuePair<string, string>>().ToList();
                Assert.Contains(resourceRows, row => row.Key == "Managed heap" && row.Value == $"{16384 / 1048576d:N1} MiB");
                Assert.Contains(resourceRows, row => row.Key == "Handles" && row.Value == "75");
                var hotkeys = ((ItemsControl)window.FindName("DetailRows")).Items.Cast<KeyValuePair<string, string>>().ToList();
                Assert.Contains(hotkeys, row => row.Key == "Switch output" && row.Value.Contains("1409", StringComparison.Ordinal));
                Assert.Contains(hotkeys, row => row.Key == "Routine shortcut (10001)");
                Assert.Contains(hotkeys, row => row.Key == "Fallback registrations" && row.Value == "1");
                ((Button)window.FindName("CopyDiagnosticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Snapshot 2", copied, StringComparison.Ordinal);
                Assert.Contains("Process ID: 42", copied, StringComparison.Ordinal);
                Assert.Contains("not a delivery test", copied, StringComparison.Ordinal);
                Assert.DoesNotContain("Working fallbacks", copied, StringComparison.Ordinal);
                Assert.False(resources.IsExpanded);
                var information = AppBuildInformation.Current;
                Assert.Contains(information.Version, ((TextBlock)window.FindName("BuildSummary")).Text, StringComparison.Ordinal);
                Assert.Contains(information.ProcessArchitecture.ToLowerInvariant(), ((TextBlock)window.FindName("BuildSummary")).Text, StringComparison.Ordinal);
                build.IsExpanded = true;
                ((Button)window.FindName("CopyBuildInfoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(information.Version, copied, StringComparison.Ordinal);
                Assert.Contains(information.Framework, copied, StringComparison.Ordinal);
                Assert.Contains(information.OperatingSystem, copied, StringComparison.Ordinal);
                ((Button)window.FindName("OpenAboutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ApplicationCommands.Help.Execute(null, window);
                Assert.Equal(2, opens);
                Assert.Equal(2, reads);
                content.Measure(new Size(440, 440));
                content.Arrange(new Rect(0, 0, 440, 440));
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.False(((Expander)window.FindName("OperationDetails")).IsExpanded);
                foreach (string name in DiagnosticActionButtons)
                {
                    var button = (Button)window.FindName(name);
                    Rect bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                    Assert.InRange(bounds.Bottom, 0, content.ActualHeight + 1);
                    Assert.InRange(bounds.Left, 0, content.ActualWidth);
                    Assert.InRange(bounds.Right, 0, content.ActualWidth + 1);
                }
                content.Measure(new Size(640, 540));
                content.Arrange(new Rect(0, 0, 640, 540));
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                ((Button)window.FindName("OpenAboutButton")).BringIntoView();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                WpfVisualAssertions.NoBindingErrors(content);
                if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    build.IsExpanded = false;
                    ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var viewer = (ScrollViewer)window.FindName("HealthScrollViewer");
                    viewer.ScrollToTop();
                    await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                    content.UpdateLayout();
                    Capture(string.Empty);
                    resources.IsExpanded = true;
                    content.UpdateLayout();
                    resources.BringIntoView();
                    await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                    content.UpdateLayout();
                    Capture("-resources");

                    void Capture(string suffix)
                    {
                        var bitmap = new RenderTargetBitmap(640, 540, 96, 96, PixelFormats.Pbgra32);
                        var background = new DrawingVisual();
                        using (var drawing = background.RenderOpen())
                            drawing.DrawRectangle((Brush)app.Resources["WindowBackgroundBrush"], null, new Rect(0, 0, 640, 540));
                        bitmap.Render(background);
                        bitmap.Render(content);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, $"diagnostics-{theme}{suffix}.png"));
                        encoder.Save(file);
                    }
                }
            }
            finally { window.Close(); app.Resources = previous; }
        });
    }

    [Fact]
    public async Task BuildActionsReportClipboardAndLaunchFailuresWithoutThrowing()
    {
        await SharedStaDispatcherHost.RunAsync(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(AppTheme.Dark);
            var report = new DiagnosticsReport(new RuntimeHealthSnapshot(DateTimeOffset.UtcNow, "gui", "inactive", null, null, null, 0), "No recent operations");
            var window = new DiagnosticsWindow(() => report, _ => Task.FromResult(new DiagnosticBundleSaveResult(true)), TestContext.Current.CancellationToken,
                copyText: _ => throw new InvalidOperationException("Clipboard temporarily unavailable"), openAbout: () => false);
            try
            {
                ((Button)window.FindName("CopyBuildInfoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("could not be copied", ((TextBlock)window.FindName("Status")).Text, StringComparison.Ordinal);
                ((Button)window.FindName("CopyDiagnosticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Diagnostics could not be copied", ((TextBlock)window.FindName("Status")).Text, StringComparison.Ordinal);
                ((Button)window.FindName("OpenAboutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("could not be opened", ((TextBlock)window.FindName("Status")).Text, StringComparison.Ordinal);
            }
            finally { window.Close(); app.Resources = previous; }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ProcessMonitorTestIsExplicit_RejectsDuplicateClicks_AndRecoversFromFailure()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(AppTheme.Dark);
            int calls = 0;
            int captures = 0;
            var pending = new TaskCompletionSource<ProcessMonitorDiagnosticResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? copied = null;
            var window = new DiagnosticsWindow(() =>
            {
                captures++;
                return new(new(DateTimeOffset.UtcNow, "gui", "inactive", null, null, null, 0), "Redacted history");
            }, _ => Task.FromResult(new DiagnosticBundleSaveResult(true)), TestContext.Current.CancellationToken, copyText: text => copied = text,
                testProcessMonitor: _ => { calls++; return pending.Task; });
            try
            {
                var details = (Expander)window.FindName("ProcessMonitorDetails");
                var button = (Button)window.FindName("TestProcessMonitorButton");
                var report = (TextBlock)window.FindName("ProcessMonitorReport");
                details.IsExpanded = true;
                ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, calls);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, calls);
                Assert.False(button.IsEnabled);
                await CompleteAndWaitForEnabledAsync(button, () => pending.SetResult(new(true, "event-delivery-confirmed", true, true, true, false, null, 20)));
                Assert.True(button.IsEnabled);
                Assert.Contains("event delivery confirmed", report.Text, StringComparison.Ordinal);
                ((Button)window.FindName("CopyDiagnosticsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("event delivery confirmed", copied, StringComparison.Ordinal);
                Assert.Equal(2, captures);

                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await CompleteAndWaitForEnabledAsync(button, () => pending.SetException(new InvalidOperationException("Injected probe failure")));
                Assert.True(button.IsEnabled);
                Assert.Contains("could not be completed", report.Text, StringComparison.Ordinal);
                Assert.Equal(2, calls);
            }
            finally { window.Close(); app.Resources = previous; }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingOrShuttingDownCancelsTheProbe_AndIgnoresLateResults(bool shutdown)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(AppTheme.Dark);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var pending = new TaskCompletionSource<ProcessMonitorDiagnosticResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var export = new TaskCompletionSource<DiagnosticBundleSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken captured = default;
            var window = new DiagnosticsWindow(() => new(new(DateTimeOffset.UtcNow, "gui", "inactive", null, null, null, 0), "History"),
                _ => export.Task, stopping.Token, testProcessMonitor: token => { captured = token; return pending.Task; }, chooseExportPath: () => "unused.zip");
            try
            {
                ((Button)window.FindName("TestProcessMonitorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("ExportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var report = (TextBlock)window.FindName("ProcessMonitorReport");
                string before = report.Text;
                var status = (TextBlock)window.FindName("Status");
                string beforeStatus = status.Text;
                if (shutdown) stopping.Cancel();
                else window.Close();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.True(captured.IsCancellationRequested);
                if (shutdown) pending.SetResult(new(true, "event-delivery-confirmed", true, true, true, false, null, 20));
                else pending.SetException(new InvalidOperationException("Injected failure after close"));
                if (shutdown) export.SetResult(new(true));
                else export.SetException(new InvalidOperationException("Injected export failure after close"));
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(before, report.Text);
                Assert.Equal(beforeStatus, status.Text);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); app.Resources = previous; }
        });
    }

    [Fact]
    public async Task ExportHandlesPickerFailuresCancellationAndReentry_WithoutShowingADialog()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(AppTheme.Dark);
            int picks = 0;
            int writes = 0;
            int mode = 0;
            Button? button = null;
            var pending = new TaskCompletionSource<DiagnosticBundleSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new DiagnosticsWindow(() => new(new(DateTimeOffset.UtcNow, "gui", "inactive", null, null, null, 0), "History"),
                _ => { writes++; return pending.Task; }, TestContext.Current.CancellationToken, chooseExportPath: () =>
                {
                    picks++;
                    if (mode == 0) throw new InvalidOperationException("Injected picker failure");
                    if (mode == 1) return null;
                    button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return "unused.zip";
                });
            try
            {
                button = (Button)window.FindName("ExportButton");
                var status = (TextBlock)window.FindName("Status");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("could not be saved", status.Text, StringComparison.Ordinal);
                Assert.True(button.IsEnabled);
                mode = 1;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, writes);
                Assert.True(button.IsEnabled);
                mode = 2;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(3, picks);
                Assert.Equal(1, writes);
                Assert.False(button.IsEnabled);
                await CompleteAndWaitForEnabledAsync(button, () => pending.SetResult(new(true)));
                Assert.True(button.IsEnabled);
                Assert.Contains("bundle saved", status.Text, StringComparison.Ordinal);
            }
            finally { window.Close(); app.Resources = previous; }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task CustomPause_ValidatesTheDurationAndUsesTheAppTheme(AppTheme theme)
    {
        await SharedStaDispatcherHost.RunAsync(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(theme);
            var window = new PauseDurationWindow();
            try
            {
                var minutes = (TextBox)window.FindName("Minutes");
                var confirm = (Button)window.FindName("PauseButton");
                Assert.True(confirm.IsEnabled);
                foreach (string invalid in InvalidPauseDurations)
                {
                    minutes.Text = invalid;
                    Assert.False(confirm.IsEnabled);
                }
                foreach (string valid in ValidPauseDurations)
                {
                    minutes.Text = valid;
                    Assert.True(confirm.IsEnabled);
                }
                Assert.Null(window.SelectedDuration);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(380, double.PositiveInfinity));
                content.Arrange(new Rect(new Point(), content.DesiredSize));
                content.UpdateLayout();
                WpfVisualAssertions.NoBindingErrors(content);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); app.Resources = previous; }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EditorChecksTheUnsavedSnapshot_RejectsDuplicateChecks_AndInvalidatesChangedResults()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(AppTheme.Dark);
            using var model = new RoutineEditorViewModel([], [], new AudioRoutine { Name = "Draft", MasterVolumePercent = 45, ShowInTrayMenu = true }, preloadNetworks: false);
            var result = new TaskCompletionSource<RoutineReadinessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            AudioRoutine? captured = null;
            int calls = 0;
            var window = new RoutineEditorWindow(model, checkRoutine: (routine, _) => { calls++; captured = routine; return result.Task; });
            try
            {
                var button = (Button)window.FindName("CheckRoutineButton");
                var text = (TextBlock)window.FindName("ReadinessText");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, calls);
                Assert.Equal("Draft", captured!.Name);
                model.Name = "Changed draft";
                result.SetResult(new(DateTimeOffset.UtcNow, true, "not-required", []));
                await window.Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Assert.Contains("Routine changed", text.Text);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Assert.Equal(2, calls);
                Assert.Equal("Changed draft", captured.Name);
                Assert.Contains("Ready to run now", text.Text);
                var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
                var changes = new List<string>();
                void TrackText(object? sender, EventArgs args) => changes.Add(text.Text);
                descriptor.AddValueChanged(text, TrackText);
                try
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await window.Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    Assert.Empty(changes);
                }
                finally { descriptor.RemoveValueChanged(text, TrackText); }
                Assert.Null(window.ResultRoutine);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); app.Resources = previous; }
        });
    }

    private static async Task CompleteAndWaitForEnabledAsync(Button button, Action complete)
    {
        var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (button.IsEnabled) enabled.TrySetResult();
        }
        button.IsEnabledChanged += OnEnabledChanged;
        try
        {
            complete();
            await enabled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { button.IsEnabledChanged -= OnEnabledChanged; }
    }

    private static ResourceDictionary CreateResources(AppTheme theme)
    {
        var resources = new ResourceDictionary
        {
            ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
            ["InverseBoolToVisibilityConverter"] = new InverseBooleanToVisibilityConverter(),
        };
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative) });
        foreach (string name in new[] { "Undo", "Redo", "Cut", "Copy", "Paste", "Delete", "SelectAll" }) resources[$"AppMenu{name}Icon"] = new DrawingImage();
        return resources;
    }
}

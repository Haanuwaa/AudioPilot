using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Updater.Trust;
using AudioPilot.Updater.Validation;

namespace AudioPilot.Updater.Ui.Validation;

/// <summary>Explicit local-fixture exercises. Deployment entry points remain restricted to Windows Sandbox.</summary>
internal static class UpdaterUiSmoke
{
    public static async Task RunAsync(string root, string package, string output)
    {
        MsiDeployment.RequireSandbox();
        var workspace = new OwnedWorkspace(root);
        Directory.CreateDirectory(output);
        var launch = OwnedWorkspace.ReadJson<FixtureJobLaunch>(workspace.Resolve("job-fixture.json"));
        string settings = Path.Combine(launch.Context.DataRoot, "settings.json");
        string placement = Path.Combine(launch.Context.DataRoot, "window-placement.json");
        string placementBefore = File.ReadAllText(placement);
        var results = new List<object>();
        void Record(string name, UpdateJobState state)
        {
            results.Add(new { name, phase = state.Phase.ToString(), state.Reason });
            OwnedWorkspace.WriteJson(Path.Combine(output, "ui-results.json"), results);
        }

        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var runtime = new LocalFixtureJobRuntime(workspace, launch.Context))
        {
            var job = new LocalUpdateJob(workspace, new TufLocalTrustClient(workspace, launch.BootstrapHash), runtime,
                async (_, token) => { copied.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
            await using var session = new UpdaterSession(job, runtime, runtime.Inspect());
            var window = new UpdaterWindow(session, AppTheme.Dark, () => package, static (_, _) => Task.FromResult(true));
            window.Show();
            Click(window, "BrowseButton"); Click(window, "VerifyButton");
            await copied.Task.WaitAsync(TimeSpan.FromSeconds(60));
            window.Close();
            await window.WindowClosed.WaitAsync(TimeSpan.FromSeconds(30));
            Require(session.Snapshot.Phase == UpdateJobPhase.Paused && session.Snapshot.Bytes > 0, "Closing did not retain partial staging.");
            Record("close-copy", session.Snapshot);
        }

        foreach (string mode in new[] { "startup-failure", "commit" })
        {
            var context = launch.Context with { Mode = mode, ExerciseAutoSave = true };
            OwnedWorkspace.WriteJson(workspace.Resolve("job-fixture.json"), launch with { Context = context });
            await using var runtime = new LocalFixtureJobRuntime(workspace, context);
            var holding = new HoldingRuntime(runtime);
            var job = new LocalUpdateJob(workspace, new TufLocalTrustClient(workspace, launch.BootstrapHash), holding);
            await using var session = new UpdaterSession(job, runtime, runtime.Inspect());
            if (mode == "startup-failure" && context.Kind == "portable")
            {
                Require((await runtime.PrepareAsync(CancellationToken.None)).Ready, "Initial reversible preparation failed.");
                await runtime.CancelPreparationAsync(CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(95));
                Require((await runtime.PrepareAsync(CancellationToken.None)).Ready, "Preparation could not be retried after idle.");
                await runtime.CancelPreparationAsync(CancellationToken.None);
                Record("idle-preparation-retry", session.Snapshot);
            }
            var window = new UpdaterWindow(session, mode == "commit" ? AppTheme.Light : AppTheme.Dark, () => package, static (_, _) => Task.FromResult(true));
            window.Show();
            window.Refresh(true);
            if (session.Snapshot.Phase == UpdateJobPhase.Paused) Click(window, "PauseButton");
            else { Click(window, "BrowseButton"); Click(window, "VerifyButton"); }
            await window.ActiveAction!.WaitAsync(TimeSpan.FromSeconds(90));
            Require(session.Snapshot.Phase == UpdateJobPhase.Ready, "The UI could not verify the local package: " + session.Snapshot.Reason);
            Require(!Button(window, "InstallButton").IsDefault, "Install must require an explicit action.");
            await RenderAsync(window, Path.Combine(output, mode + "-ready.png"));
            Click(window, "InstallButton");
            await holding.Entered.Task.WaitAsync(TimeSpan.FromSeconds(90));
            string savedSettings = File.ReadAllText(settings);
            if (mode == "commit")
            {
                window.Close();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(!window.IsVisible && !window.WindowClosed.IsCompleted, "Closing interrupted an armed installation.");
            }
            holding.Release.TrySetResult();
            if (mode == "commit") await window.WindowClosed.WaitAsync(TimeSpan.FromMinutes(7));
            else
            {
                await window.ActiveAction!.WaitAsync(TimeSpan.FromMinutes(7));
                await RenderAsync(window, Path.Combine(output, "rollback.png"));
                window.Close();
                await window.WindowClosed.WaitAsync(TimeSpan.FromSeconds(30));
            }
            Require(session.Snapshot.Phase == (mode == "commit" ? UpdateJobPhase.Completed : UpdateJobPhase.RolledBack),
                "Unexpected UI result: " + session.Snapshot.Phase + "/" + session.Snapshot.Reason);
            Require(!new DeploymentRecovery(workspace).IsPending, "Recovery did not settle.");
            var recovery = new DeploymentRecovery(workspace).Read();
            Require(File.ReadAllText(settings) == savedSettings, "The final saved settings were not preserved.");
            Require(File.ReadAllText(placement) == placementBefore, "Window placement changed.");
            if (context.InstallerOptions is { } options) Program.VerifyOptions(options);
            Record(mode, session.Snapshot);
        }
    }

    public static async Task RenderPreviewAsync(string output)
    {
        Directory.CreateDirectory(output);
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            var window = new UpdaterWindow(null, theme) { ShowActivated = false, Left = -10000, Top = -10000 };
            window.Show();
            await RenderAsync(window, Path.Combine(output, theme + ".png"));
            var progress = (ProgressBar)window.FindName("PackageProgress");
            progress.Visibility = Visibility.Visible; progress.Value = 35;
            ((TextBlock)window.FindName("StatusHeading")).Text = "Package transfer (preview)";
            await RenderAsync(window, Path.Combine(output, theme + "-progress.png"));
            window.Close();
            await window.WindowClosed;
        }
    }

    internal static async Task RenderAsync(Window window, string output)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        if (window.Content is not FrameworkElement content) throw new InvalidOperationException("Missing window content.");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + 40), (int)Math.Ceiling(content.ActualHeight + 40), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            context.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, new Rect(20, 20, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(output); encoder.Save(file);
    }
    private static Button Button(Window window, string name) => (Button)window.FindName(name);
    internal static void Click(Window window, string name)
    {
        var button = Button(window, name);
        Require(button.IsEnabled && button.Visibility == Visibility.Visible, "The UI action is not available: " + name);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class HoldingRuntime(LocalFixtureJobRuntime inner) : ILocalUpdateRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Context => inner.Context;
        public bool RecoveryPending => inner.RecoveryPending;
        public UpdateSelection Inspect() => inner.Inspect();
        public Task<UpdateJobPreparation> PrepareAsync(CancellationToken token) => inner.PrepareAsync(token);
        public Task CancelPreparationAsync(CancellationToken token) => inner.CancelPreparationAsync(token);
        public Task<bool> RecoverAsync() => inner.RecoverAsync();
        public async Task<bool> DeployAsync(VerifiedLocalUpdate package)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30)); return await inner.DeployAsync(package); }
    }
}

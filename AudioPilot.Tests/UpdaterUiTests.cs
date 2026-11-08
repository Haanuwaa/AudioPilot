using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Updater;
using AudioPilot.Updater.Trust;
using AudioPilot.Updater.Ui;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class UpdaterUiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void StagingCdnPolicy_RejectsCrossOriginCredentialsAndDowngrades()
    {
        Assert.True(HttpsStagingFeed.IsReleaseAsset(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/file?sig=temporary")));
        foreach (string url in new[]
        {
            "http://release-assets.githubusercontent.com/github-production-release-asset/file",
            "https://release-assets.githubusercontent.com.evil.invalid/github-production-release-asset/file",
            "https://name:secret@release-assets.githubusercontent.com/github-production-release-asset/file",
            "https://release-assets.githubusercontent.com:444/github-production-release-asset/file",
            "https://release-assets.githubusercontent.com/unrelated/file"
        })
        {
            Assert.False(HttpsStagingFeed.IsReleaseAsset(new Uri(url)));
        }
    }

    [Fact]
    public async Task ClosingDuringCopy_PausesAndRetainsProgressForReopening()
    {
        using var fixture = new Fixture();
        var copied = Signal();
        var session = fixture.Session(async (_, token) => { copied.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        var action = session.VerifyAsync(fixture.Source);
        await copied.Task.WaitAsync(Token);
        await session.CloseSafelyAsync();
        Assert.Equal(UpdateJobPhase.Paused, (await action).Phase);
        Assert.True(session.Snapshot.Bytes > 0);
        Assert.True(fixture.Runtime.Disposed);
        Assert.Equal(0, fixture.Runtime.Deploys);
        Assert.True(File.Exists(fixture.Source));
        await using var reopened = fixture.Session();
        Assert.Equal(UpdateJobPhase.Paused, reopened.Snapshot.Phase);
        Assert.Equal(UpdateJobPhase.Ready, (await reopened.VerifyAsync(fixture.Source)).Phase);
    }

    [Fact]
    public async Task ClosingDuringPreparation_ReleasesAdmissionAndNeverDeploys()
    {
        using var fixture = new Fixture();
        fixture.Runtime.HoldPreparation = true;
        await using var session = fixture.Session();
        await session.VerifyAsync(fixture.Source);
        var action = session.InstallAsync();
        await fixture.Runtime.Preparing.Task.WaitAsync(Token);
        await session.CloseSafelyAsync();
        Assert.Equal(UpdateJobPhase.Cancelled, (await action).Phase);
        Assert.Equal(1, fixture.Runtime.CancelledPreparation);
        Assert.False(fixture.Runtime.AdmissionBlocked);
        Assert.Equal(0, fixture.Runtime.Deploys);
    }

    [Fact]
    public async Task ClosingDuringInstallation_JoinsCommitWithoutCancellingTheWorker()
    {
        using var fixture = new Fixture();
        fixture.Runtime.HoldDeployment = true;
        await using var session = fixture.Session();
        await session.VerifyAsync(fixture.Source);
        var action = session.InstallAsync();
        await fixture.Runtime.Deploying.Task.WaitAsync(Token);
        var closing = session.CloseSafelyAsync();
        Assert.False(closing.IsCompleted);
        Assert.False(fixture.Runtime.Disposed);
        Assert.False(session.Cancel());
        fixture.Runtime.ReleaseDeployment.TrySetResult();
        Assert.Equal(UpdateJobPhase.Completed, (await action).Phase);
        await closing;
        Assert.True(fixture.Runtime.Disposed);
        Assert.Equal(0, fixture.Runtime.CancelledPreparation);
    }

    [Fact]
    public async Task ClosingWithFailedRecoveryAllowsAttentionThenAnExplicitIdleClose()
    {
        using var fixture = new Fixture();
        fixture.Runtime.HoldDeployment = true;
        fixture.Runtime.FailDeployment = fixture.Runtime.FailRecovery = true;
        await using var session = fixture.Session();
        await session.VerifyAsync(fixture.Source);
        var action = session.InstallAsync();
        await fixture.Runtime.Deploying.Task.WaitAsync(Token);
        var closing = session.CloseSafelyAsync();
        fixture.Runtime.ReleaseDeployment.TrySetResult();
        Assert.Equal(UpdateJobPhase.RecoveryRequired, (await action).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => closing);
        Assert.False(fixture.Runtime.Disposed);
        await session.CloseSafelyAsync();
        Assert.True(fixture.Runtime.Disposed);
        Assert.True(File.Exists(fixture.Workspace.Resolve("job.json")));
        Assert.Equal(1, fixture.Runtime.Recovers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ThemedWindow_RequiresExplicitInstallAndHasAccessibleActions(bool light) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await using var session = fixture.Session();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ResourceDictionary resources = app.Resources;
        var window = new UpdaterWindow(session, light ? AppTheme.Light : AppTheme.Dark, () => fixture.Source, static (_, _) => Task.FromResult(true));
        try
        {
            var browse = (Button)window.FindName("BrowseButton");
            var verify = (Button)window.FindName("VerifyButton");
            var install = (Button)window.FindName("InstallButton");
            Assert.False(install.IsDefault);
            Assert.False(install.IsEnabled);
            browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(verify.IsEnabled);
            verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.ActiveAction!.WaitAsync(Token);
            Assert.True(install.IsEnabled);
            Assert.Equal(Visibility.Visible, install.Visibility);
            Assert.False(install.IsKeyboardFocused);
            Assert.False(((Expander)window.FindName("TechnicalDetails")).IsExpanded);
            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.ActiveAction!.WaitAsync(Token);
            Assert.Equal(UpdateJobPhase.Completed, session.Snapshot.Phase);
            Assert.Equal(1, fixture.Runtime.Deploys);
            Assert.Equal("AudioPilot was updated", ((TextBlock)window.FindName("StatusHeading")).Text);
            Assert.Equal("1.0.1 (x64)", ((TextBlock)window.FindName("InstalledText")).Text);
            window.Close();
            await window.WindowClosed.WaitAsync(Token);
        }
        finally { window.Close(); app.Resources = resources; WindowThemeResolver.SetApplicationThemeProvider(null); }
    });

    [Fact]
    public Task DeclinedDownloadAndInstall_DoNotStageOrPrepare() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var client = new HttpClient(new PackageHandler(fixture.Source));
        await using var session = fixture.RemoteSession(client);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = app.Resources;
        bool allowDownload = false;
        var window = new UpdaterWindow(session, AppTheme.Dark, confirm: (consent, _) => Task.FromResult(consent.Kind == UpdateConsentKind.Download && allowDownload));
        try
        {
            ((Button)window.FindName("VerifyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.ActiveAction!.WaitAsync(Token);
            Assert.Equal(UpdateJobPhase.Available, session.Snapshot.Phase);
            Assert.Equal(0, session.Snapshot.Bytes);
            Assert.False(Directory.Exists(fixture.Workspace.Resolve("job-stage")));
            allowDownload = true;
            ((Button)window.FindName("VerifyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.ActiveAction!.WaitAsync(Token);
            Assert.Equal(UpdateJobPhase.Ready, session.Snapshot.Phase);
            Assert.True(session.Snapshot.DownloadApproved);
            ((Button)window.FindName("InstallButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.ActiveAction!.WaitAsync(Token);
            Assert.Equal(UpdateJobPhase.Ready, session.Snapshot.Phase);
            Assert.False(fixture.Runtime.Preparing.Task.IsCompleted);
            Assert.Equal(0, fixture.Runtime.Deploys);
            window.Close(); await window.WindowClosed.WaitAsync(Token);
        }
        finally { window.Close(); app.Resources = resources; WindowThemeResolver.SetApplicationThemeProvider(null); }
    });

    [Fact]
    public Task ClosingPendingInstallConfirmation_CancelsConsentWithoutPreparing() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await using var session = fixture.Session();
        await session.VerifyAsync(fixture.Source);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = app.Resources;
        var entered = Signal();
        var window = new UpdaterWindow(session, AppTheme.Dark, confirm: async (_, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return true;
        });
        try
        {
            ((Button)window.FindName("InstallButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await entered.Task.WaitAsync(Token);
            var action = window.ActiveAction;
            window.Close(); await window.WindowClosed.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await action!.WaitAsync(Token);
            Assert.False(fixture.Runtime.Preparing.Task.IsCompleted);
            Assert.Equal(0, fixture.Runtime.Deploys);
            Assert.True(fixture.Runtime.Disposed);
        }
        finally { window.Close(); app.Resources = resources; WindowThemeResolver.SetApplicationThemeProvider(null); }
    });

    [Fact]
    public async Task MicrophoneOwnershipDefersInstallationAndKeepsThePackageReady()
    {
        using var fixture = new Fixture();
        fixture.Runtime.DeferralReason = "microphone-owned";
        await using var session = fixture.Session();
        await session.VerifyAsync(fixture.Source);
        var result = await session.InstallAsync();
        Assert.Equal(UpdateJobPhase.Deferred, result.Phase);
        Assert.Equal(0, fixture.Runtime.Deploys);
        Assert.False(fixture.Runtime.AdmissionBlocked);
        var view = UpdaterPresentation.From(result, busy: false, selected: true);
        Assert.Contains("microphone", view.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(view.CanInstall);
        Assert.False(view.CanBrowse);
        fixture.Runtime.DeferralReason = null;
        Assert.Equal(UpdateJobPhase.Completed, (await session.InstallAsync()).Phase);
        Assert.Equal(1, fixture.Runtime.Deploys);
    }

    [Fact]
    public void PresentationDoesNotOfferCancellationOrInstallationDuringRecovery()
    {
        var state = new UpdateJobState(1, 1, Guid.NewGuid().ToString("N"), "fixture", UpdateJobPhase.Recovering);
        var view = UpdaterPresentation.From(state, busy: true, selected: true);
        Assert.False(view.CanCancel);
        Assert.False(view.CanInstall);
        Assert.False(view.CanBrowse);
        Assert.True(view.Indeterminate);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Fixture : IDisposable, ILocalUpdateTrust
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-ui-" + Guid.NewGuid().ToString("N"));
        public OwnedWorkspace Workspace { get; }
        public string Source { get; }
        public Runtime Runtime { get; } = new();
        private readonly AuthorizedUpdate _authorization;
        public Fixture()
        {
            Workspace = new(Path.Combine(_root, "work"));
            Source = Path.Combine(_root, "candidate.zip");
            byte[] data = RandomNumberGenerator.GetBytes(180000);
            File.WriteAllBytes(Source, data);
            var artifact = new UpdateArtifact("candidate.zip", "portable", "win-x64", "self-contained", data.Length, Convert.ToHexString(SHA256.HashData(data)));
            _authorization = new(new(1, "AudioPilot", "local-test", 1, 1, "1.0.1", "1.0.0", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, [artifact]), artifact);
        }
        public UpdaterSession Session(Func<long, CancellationToken, Task>? checkpoint = null) => new(new LocalUpdateJob(Workspace, this, Runtime, checkpoint), Runtime, Runtime.Inspect());
        public UpdaterSession RemoteSession(HttpClient client) => new(new LocalUpdateJob(Workspace, this, Runtime), Runtime, Runtime.Inspect(),
            new HttpsTestFeed("https://127.0.0.1:45123/", "unused", "unused", "candidate.zip"), client);
        public Task<AuthorizedUpdate> AuthorizeAsync(string target, UpdateSelection selection, CancellationToken token) => Task.FromResult(_authorization);
        public async Task<VerifiedLocalUpdate> OpenPackageAsync(string path, UpdateSelection selection, CancellationToken token)
        {
            await ResumableDownload.VerifyAsync(path, _authorization.Artifact.Length, _authorization.Artifact.Sha256, token);
            return new(() => _authorization, _authorization, path, File.OpenRead(path));
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class PackageHandler(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(File.ReadAllBytes(path)), RequestMessage = request });
    }
    private sealed class Runtime : ILocalUpdateRuntime, IAsyncDisposable
    {
        public string Context => "ui-test";
        public bool RecoveryPending { get; private set; }
        public bool HoldPreparation, HoldDeployment, FailDeployment, FailRecovery, Disposed, AdmissionBlocked;
        public string? DeferralReason;
        public int Deploys, CancelledPreparation, Recovers;
        public TaskCompletionSource Preparing { get; } = Signal();
        public TaskCompletionSource Deploying { get; } = Signal();
        public TaskCompletionSource ReleaseDeployment { get; } = Signal();
        public UpdateSelection Inspect() => new("portable", "win-x64", "self-contained", "1.0.0", "1.0.0");
        public async Task<UpdateJobPreparation> PrepareAsync(CancellationToken token)
        {
            AdmissionBlocked = true; Preparing.TrySetResult();
            if (HoldPreparation) await Task.Delay(Timeout.Infinite, token);
            return DeferralReason == null ? new(true) : new(false, DeferralReason);
        }
        public Task CancelPreparationAsync(CancellationToken token) { CancelledPreparation++; AdmissionBlocked = false; return Task.CompletedTask; }
        public async Task<bool> DeployAsync(VerifiedLocalUpdate package)
        {
            Deploys++; RecoveryPending = true; Deploying.TrySetResult();
            if (HoldDeployment) await ReleaseDeployment.Task;
            if (FailDeployment) throw new IOException("injected deployment failure");
            RecoveryPending = false; return true;
        }
        public Task<bool> RecoverAsync() { Recovers++; if (FailRecovery) throw new IOException("injected recovery failure"); RecoveryPending = false; return Task.FromResult(false); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

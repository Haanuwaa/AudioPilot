using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRefreshRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditsDuringDiscovery_AreRecheckedBeforeReload(bool editDuringCollectionRefresh)
    {
        using var scenario = new RefreshScenario();
        var dependencies = scenario.Dependencies with
        {
            HasSettingsFileChangedAsync = () =>
            {
                if (!editDuringCollectionRefresh) scenario.Edit();
                return Task.FromResult(true);
            },
            RefreshAvailableDeviceCollectionsAsync = () =>
            {
                if (editDuringCollectionRefresh) scenario.Edit();
                return Task.CompletedTask;
            },
        };

        var result = await scenario.RunAsync(dependencies, TestContext.Current.CancellationToken);

        Assert.False(result.SettingsChanged);
        Assert.Equal(1, scenario.ExternalPrompts);
        Assert.Equal(0, scenario.CachedPrompts);
        Assert.Equal(0, scenario.Applications);
    }

    [Fact]
    public async Task SavedEditDuringDiscovery_DoesNotApplyAnOlderSnapshot()
    {
        using var scenario = new RefreshScenario();
        var dependencies = scenario.Dependencies with
        {
            LoadSettingsForRefreshAsync = () =>
            {
                scenario.Revision++;
                return Task.FromResult<Settings?>(new());
            },
        };

        Assert.False((await scenario.RunAsync(dependencies, TestContext.Current.CancellationToken)).SettingsChanged);
        Assert.Equal(0, scenario.Applications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditDuringConfirmation_PreservesNewerEdits(bool cachedSettings)
    {
        using var scenario = new RefreshScenario();
        scenario.Edit();
        Task<AppDialogResult> ConfirmAndEdit()
        {
            scenario.Edit();
            return Task.FromResult(AppDialogResult.Confirmed);
        }
        var dependencies = scenario.Dependencies with
        {
            HasSettingsFileChangedAsync = () => Task.FromResult(!cachedSettings),
            PromptReloadExternalSettings = ConfirmAndEdit,
            PromptReloadCachedSettings = ConfirmAndEdit,
        };

        await scenario.RunAsync(dependencies, TestContext.Current.CancellationToken);

        Assert.Equal(0, scenario.Applications);
    }

    [Fact]
    public async Task ExistingEditSavedDuringDiscovery_DoesNotApplyAnOlderSnapshot()
    {
        using var scenario = new RefreshScenario();
        scenario.Edit();
        var dependencies = scenario.Dependencies with
        {
            LoadSettingsForRefreshAsync = () =>
            {
                scenario.HasEdits = false;
                return Task.FromResult<Settings?>(new());
            },
        };

        Assert.False((await scenario.RunAsync(dependencies, TestContext.Current.CancellationToken)).SettingsChanged);
        Assert.Equal(0, scenario.Applications);
    }

    [Fact]
    public async Task ConfirmedReload_AppliesOnlyOnceAndSkipsCachedPrompt()
    {
        using var scenario = new RefreshScenario();
        scenario.Edit();
        var dependencies = scenario.Dependencies with
        {
            PromptReloadExternalSettings = () => Task.FromResult(AppDialogResult.Confirmed),
        };

        Assert.True((await scenario.RunAsync(dependencies, TestContext.Current.CancellationToken)).SettingsChanged);
        Assert.Equal(1, scenario.Applications);
        Assert.Equal(0, scenario.CachedPrompts);
    }

    [Fact]
    public async Task CancellationWhileLoading_ReleasesWaitAndDoesNotApplyLateResults()
    {
        using var scenario = new RefreshScenario();
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<Settings?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencies = scenario.Dependencies with { LoadSettingsForRefreshAsync = () => completion.Task };
        Task refresh = scenario.RunAsync(dependencies, cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        completion.SetResult(new());

        Assert.Equal(0, scenario.Applications);
        Assert.Empty(scenario.Effects);
    }

    [Theory]
    [InlineData("file-check")]
    [InlineData("load")]
    [InlineData("collections")]
    [InlineData("mute")]
    public async Task CleanupDuringRefresh_StopsSubsequentWork(string phase)
    {
        using var scenario = new RefreshScenario();
        bool stopping = false;
        var dependencies = scenario.Dependencies with
        {
            EnsureActive = () => { if (stopping) throw new OperationCanceledException(); },
            HasSettingsFileChangedAsync = () => { stopping = phase == "file-check"; return Task.FromResult(true); },
            LoadSettingsForRefreshAsync = () => { stopping = phase == "load"; return Task.FromResult<Settings?>(new()); },
            RefreshAvailableDeviceCollectionsAsync = () => { stopping = phase == "collections"; return Task.CompletedTask; },
            UpdateMuteFlagsFromSystemAsync = () => { stopping = phase == "mute"; return Task.CompletedTask; },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.RunAsync(dependencies, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("mixer", scenario.Effects);
        if (phase != "mute")
        {
            Assert.Equal(0, scenario.Applications);
            Assert.Empty(scenario.Effects);
        }
    }

    private sealed class RefreshScenario : IDisposable
    {
        private readonly TestLoggerScope _logger = new(nameof(AppRefreshRegressionTests), $"refresh-{Guid.NewGuid():N}.log", LogLevel.Info);
        public long Revision;
        public bool HasEdits;
        public int ExternalPrompts;
        public int CachedPrompts;
        public int Applications;
        public List<string> Effects { get; } = [];
        public AppRefreshExecutionDependencies Dependencies => new(
            () => HasEdits,
            () => Task.FromResult(true),
            () => { ExternalPrompts++; return Task.FromResult(AppDialogResult.Declined); },
            () => Task.CompletedTask,
            () => Task.FromResult<Settings?>(new()),
            _ => Applications++,
            () => HasEdits,
            () => { CachedPrompts++; return Task.FromResult(AppDialogResult.Declined); },
            () => new(),
            () => Effects.Add("reference"),
            () => Effects.Add("cache"),
            () => { Effects.Add("mute"); return Task.CompletedTask; },
            _ => { Effects.Add("mixer"); return Task.CompletedTask; },
            () => Revision,
            () => { });

        public void Edit() { Revision++; HasEdits = true; }
        public Task<AppRefreshExecutionResult> RunAsync(AppRefreshExecutionDependencies dependencies, CancellationToken cancellationToken = default) =>
            AppRefreshCycleCoordinator.ExecuteAsync(new(true, true, true, true, false, 0), dependencies, "refresh:regression", _logger.Logger, cancellationToken);
        public void Dispose() => _logger.Dispose();
    }
}

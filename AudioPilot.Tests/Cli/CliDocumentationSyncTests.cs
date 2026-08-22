using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Cli;

public sealed class CliDocumentationSyncTests
{
    [Fact]
    public void SyncCliGuide_PreservesManualQuickReferenceUseCasesAndCommandSelectorNotes()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        string synced = CliDocumentationSync.SyncCliGuide(markdown);

        Assert.Contains("Use cases:", synced, StringComparison.Ordinal);
        Assert.Contains("Selector notes:", synced, StringComparison.Ordinal);
        Assert.True(CliDocumentationSync.IsCliGuideInSync(synced));
    }

    [Fact]
    public void SyncCliGuide_ReplacesGeneratedSections()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));
        string mutated = markdown.Replace("AudioPilot.Cli.exe status --json", "AudioPilot.Cli.exe status", StringComparison.Ordinal);

        string synced = CliDocumentationSync.SyncCliGuide(mutated);

        Assert.Contains("AudioPilot.Cli.exe status --json", synced, StringComparison.Ordinal);
        Assert.True(CliDocumentationSync.IsCliGuideInSync(synced));
    }

    [Fact]
    public void SyncCliGuide_PreservesExactHelpNotesForSwitchCycleAndWait()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        string synced = CliDocumentationSync.SyncCliGuide(markdown);

        Assert.Contains("Input switching supports --reverse, --dry-run, and --require-current, but not --mute-mic, --mute-sound, or --deafen.", synced, StringComparison.Ordinal);
        Assert.Contains("cycle reorder expects the full current cycle device list in the new order; the parser rejects blank or duplicate ids, and execution verifies the configured cycle membership.", synced, StringComparison.Ordinal);
        Assert.Contains("Use --output or --input to scope the wait to one device class; the parser rejects passing both flags together, and omitting both lets either class satisfy the wait.", synced, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncCliGuide_NormalizesMarkdownToRepositoryLfPolicy()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        string crlfMarkdown = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        string synced = CliDocumentationSync.SyncCliGuide(crlfMarkdown);

        Assert.DoesNotContain("\r\n", synced, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryRootLocator_FindsRepositoryMarkersAboveNestedBuildDirectory()
    {
        using var scope = new TestScopedDirectory("repository-root-locator");
        File.WriteAllText(Path.Combine(scope.Root, "AudioPilot.sln"), string.Empty);
        Directory.CreateDirectory(Path.Combine(scope.Root, "docs"));
        File.WriteAllText(Path.Combine(scope.Root, "docs", "CLI.md"), string.Empty);
        string nestedDirectory = Directory.CreateDirectory(
            Path.Combine(scope.Root, "AudioPilot.CliHost", "bin", "isolated", "Audit", "Release", "tfm")).FullName;

        bool found = RepositoryRootLocator.TryFind([nestedDirectory], out string repositoryRoot);

        Assert.True(found);
        Assert.Equal(scope.Root, repositoryRoot);
    }

    [Fact]
    public void RepositoryRootLocator_RequiresSolutionAndCliGuideMarkers()
    {
        using var scope = new TestScopedDirectory("repository-root-locator-missing-marker");
        File.WriteAllText(Path.Combine(scope.Root, "AudioPilot.sln"), string.Empty);

        bool found = RepositoryRootLocator.TryFind([scope.Root], out string repositoryRoot);

        Assert.False(found);
        Assert.Equal(string.Empty, repositoryRoot);
    }

    [Fact]
    public void InternalDocsSync_MissingRepository_ReturnsControlledFailure()
    {
        using var scope = new TestScopedDirectory("cli-docs-maintenance-missing-root");
        var output = new StringWriter();
        var error = new StringWriter();

        bool handled = CliDocsMaintenance.TryHandle(
            ["internal-docs-sync", "--check"],
            output,
            error,
            [scope.Root],
            out int exitCode);

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Could not locate the AudioPilot repository", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(scope.Root, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InternalDocsSync_MalformedGuide_ReturnsControlledFailure()
    {
        using var scope = new TestScopedDirectory("cli-docs-maintenance-invalid-guide");
        File.WriteAllText(Path.Combine(scope.Root, "AudioPilot.sln"), string.Empty);
        Directory.CreateDirectory(Path.Combine(scope.Root, "docs"));
        File.WriteAllText(Path.Combine(scope.Root, "docs", "CLI.md"), "# Incomplete guide");
        var output = new StringWriter();
        var error = new StringWriter();

        bool handled = CliDocsMaintenance.TryHandle(
            ["internal-docs-sync", "--check"],
            output,
            error,
            [scope.Root],
            out int exitCode);

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Could not process docs\\CLI.md (InvalidOperationException)", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(scope.Root, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRepoRoot()
    {
        Assert.True(
            RepositoryRootLocator.TryFind([AppContext.BaseDirectory], out string repositoryRoot),
            "Could not locate the AudioPilot repository root from the test output directory.");
        return repositoryRoot;
    }
}
